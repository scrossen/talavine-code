using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding;
using SwarmCore.Coding.Checkpoints;
using SwarmCore.Coding.Routing;
using SwarmCore.Testing;

namespace SwarmCode.Hosting.Tests;

/// <summary>
/// The runner checkpoints the repository around each turn, whatever edits the files (here a scripted lane writing to
/// disk, as a native CLI does), records the changed files in the transcript, and undoes them on request.
/// </summary>
public sealed class ConversationRunnerUndoTests : TempDirTest
{
    private static readonly CodingLane Lane = new("claude", "ClaudeCode", "claude-code:default");

    private sealed class DiskEditingOrchestrator(Action edit) : ICodingTurnOrchestrator
    {
        public IReadOnlyList<CodingLane> Lanes { get; } = [Lane];
        public CodingLane? ResolveLane(string? name) => Lane;

        public async IAsyncEnumerable<TurnEvent> RunAsync(CodingTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return new CodingLaneSelected("t", Lane, 1);
            edit();
            yield return new TurnCompleted("t", "Edited the files.", TimeSpan.FromSeconds(1));
        }
    }

    private sealed class FixedWorkspace(string root) : ICodingWorkspaceResolver
    {
        public Task<CodingWorkspace?> ResolveAsync(string conversationId, CancellationToken ct = default)
            => Task.FromResult<CodingWorkspace?>(new CodingWorkspace { RepositoryRoot = root });
    }

    private string NewRepo()
    {
        var repo = Directory.CreateDirectory(Path.Combine(Tmp, "repo")).FullName;
        foreach (var args in new[]
        {
            new[] { "init", "-q" }, ["config", "core.autocrlf", "false"], ["config", "user.name", "T"],
            ["config", "user.email", "t@example.com"], ["config", "commit.gpgsign", "false"],
        })
            Assert.Equal(0, TestGit.Run(repo, args).ExitCode);
        File.WriteAllText(Path.Combine(repo, "a.txt"), "original\n");
        Assert.Equal(0, TestGit.Run(repo, "add", ".").ExitCode);
        Assert.Equal(0, TestGit.Run(repo, "commit", "-q", "-m", "init").ExitCode);
        return repo;
    }

    private (ServiceProvider Provider, ConversationRunner Runner) Build(string repo, Action edit)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new FileTranscriptJournal(Paths));
        services.AddSingleton<IConversationStore>(new FileConversationStore(Paths));
        services.AddSingleton(new FileConversationJobStore(Paths));
        services.AddSingleton<IPermissionBroker>(new UiPermissionBroker());
        services.AddSingleton<ICodingTurnOrchestrator>(new DiskEditingOrchestrator(edit));
        services.AddSingleton<ICodingWorkspaceResolver>(new FixedWorkspace(repo));
        services.AddSingleton(new TurnCheckpointService(new TurnCheckpointOptions()));
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        return (provider, new ConversationRunner(provider, "conv"));
    }

    private static async Task RunTurnAsync(ConversationRunner runner, string text)
    {
        var finished = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.TurnFinished += r => finished.TrySetResult(r);
        Assert.True(runner.TrySend(text, "claude"));
        var result = await finished.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(result.Completed);
        Assert.True(result.WorkspaceChanged);
    }

    [GitFact]
    public async Task A_turn_that_edits_files_on_disk_records_them_and_can_be_undone_after_a_reload()
    {
        var repo = NewRepo();
        var (provider, runner) = Build(repo, () =>
        {
            File.WriteAllText(Path.Combine(repo, "a.txt"), "edited by the lane\n");
            File.WriteAllText(Path.Combine(repo, "created.txt"), "new\n");
        });
        await using (provider)
        await using (runner)
        {
            await RunTurnAsync(runner, "edit");
            var changes = Assert.Single(runner.Entries.OfType<TurnChangesEntry>());
            Assert.Equal(2, changes.Files.Count);
            await runner.FlushJournalAsync();
        }

        // A reopened session shows the same changes and can still undo them.
        var (provider2, reopened) = Build(repo, () => { });
        await using (provider2)
        await using (reopened)
        {
            await reopened.LoadHistoryAsync();
            var entry = Assert.IsType<TurnChangesEntry>(reopened.LastUndoableTurn());
            Assert.Contains(entry.Files, f => f is { Path: "created.txt", Kind: TurnFileChangeKind.Added });

            var preview = await reopened.PreviewUndoAsync(entry.Id);
            Assert.Equal(TurnUndoStatus.Undone, preview.Status);
            Assert.Equal("edited by the lane\n", File.ReadAllText(Path.Combine(repo, "a.txt")));

            var outcome = await reopened.UndoAsync(entry.Id, TurnUndoConflictMode.Refuse);
            Assert.Equal(TurnUndoStatus.Undone, outcome.Status);
            Assert.Equal("original\n", File.ReadAllText(Path.Combine(repo, "a.txt")));
            Assert.False(File.Exists(Path.Combine(repo, "created.txt")));
            Assert.True(entry.Undone);
            Assert.Null(reopened.LastUndoableTurn());
            Assert.Contains(reopened.Entries.OfType<NoticeEntry>(), n => n.Text == outcome.Summary);
        }
    }

    [GitFact]
    public async Task Undo_reports_a_file_edited_after_the_turn_and_only_overwrites_it_when_forced()
    {
        var repo = NewRepo();
        var (provider, runner) = Build(repo, () => File.WriteAllText(Path.Combine(repo, "a.txt"), "edited by the lane\n"));
        await using (provider)
        await using (runner)
        {
            await RunTurnAsync(runner, "edit");
            var entry = runner.LastUndoableTurn()!;
            File.WriteAllText(Path.Combine(repo, "a.txt"), "the person's later edit\n");

            var refused = await runner.UndoAsync(entry.Id, TurnUndoConflictMode.Refuse);
            Assert.Equal(TurnUndoStatus.ConflictsNeedDecision, refused.Status);
            Assert.Equal(new[] { "a.txt" }, refused.Conflicts);
            Assert.Equal("the person's later edit\n", File.ReadAllText(Path.Combine(repo, "a.txt")));
            Assert.False(entry.Undone);

            var forced = await runner.UndoAsync(entry.Id, TurnUndoConflictMode.Force);
            Assert.Equal(TurnUndoStatus.Undone, forced.Status);
            Assert.Equal("original\n", File.ReadAllText(Path.Combine(repo, "a.txt")));
            Assert.Contains(forced.BackupRef!, ConversationRunner.UndoDetails(forced));
        }
    }
}
