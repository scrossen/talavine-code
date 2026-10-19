using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.History;

namespace SwarmCode.Hosting.Tests;

public sealed class ConversationForkTests : TempDirTest
{
    private (WorkspaceSessionCatalog Catalog, FileAgentSessionStore Transcript, FileConversationStore Turns,
        FileTranscriptJournal Journal, FileConversationJobStore Jobs, ConversationForkService Forks) Stores()
    {
        var catalog = new WorkspaceSessionCatalog(Paths); var transcript = new FileAgentSessionStore(Paths);
        var turns = new FileConversationStore(Paths); var journal = new FileTranscriptJournal(Paths); var jobs = new FileConversationJobStore(Paths);
        return (catalog, transcript, turns, journal, jobs, new(catalog, transcript, turns, journal, jobs));
    }

    [Fact]
    public async Task Fork_survives_restart_with_history_settings_and_independent_turn_identity()
    {
        var s = Stores();
        var source = await s.Catalog.CreateAsync(Tmp, "Original", "codex", model: "chosen-model", permissionMode: "review");
        await s.Catalog.SetTemporaryAsync(source.Id, true);
        await s.Turns.GetOrCreateAsync(source.Id, source.Title);
        var turn = await s.Turns.AppendUserTurnAsync(source.Id, "Remember the blue marker", images: [new("AQID", "image/png")]);
        await s.Turns.CompleteAssistantTurnAsync(turn.Id, "Blue marker saved", null, TimeSpan.FromSeconds(1), metadataJson: "{\"checkpoint\":\"old\"}");
        await s.Turns.UpdateStateAsync(source.Id, new Dictionary<string, object> { ["codex.threadId"] = "must-not-resume" });
        await s.Transcript.AppendRangeAsync(source.Id, [ChatMessage.Text(ChatRole.User, "Remember the blue marker"), ChatMessage.Text(ChatRole.Assistant, "Blue marker saved")]);
        s.Jobs.Save(source.Id, new() { State = "Completed", Queue = [new("do not run", "codex", false, false)] });
        var fork = await s.Forks.ForkAsync(source.Id);
        var cold = Stores();
        Assert.NotEqual(source.Id, fork.Id); Assert.Equal(source.Id, fork.ForkedFromSessionId);
        Assert.Null(fork.ParentSessionId); Assert.True(fork.Temporary); Assert.Equal("review", fork.PermissionMode);
        Assert.Equal("chosen-model", fork.Selection!.Model); Assert.Equal(source.RepositoryRoot, fork.RepositoryRoot);
        var reloaded = await cold.Catalog.GetAsync(fork.Id);
        Assert.Equal(fork.Id, reloaded!.Id); Assert.Equal(fork.Selection, reloaded.Selection);
        Assert.Equal(fork.ModelChoices, reloaded.ModelChoices);
        var copied = Assert.Single(await cold.Turns.LoadTurnsAsync(fork.Id));
        Assert.NotEqual(turn.Id, copied.Id); Assert.Equal(fork.Id, copied.ConversationId);
        Assert.Equal("Blue marker saved", copied.AssistantText); Assert.Single(copied.AllUserImages); Assert.Null(copied.MetadataJson);
        Assert.Empty(await cold.Turns.GetStateAsync(fork.Id)); Assert.Null(cold.Jobs.Load(fork.Id));
        Assert.Equal(2, (await cold.Transcript.LoadAsync(fork.Id)).Count);
        Assert.Contains(cold.Journal.Load(fork.Id).Entries.OfType<UserEntry>(), e => e.Text == turn.UserText);
        await cold.Turns.AppendUserTurnAsync(fork.Id, "Fork-only message");
        await cold.Transcript.AppendAsync(fork.Id, ChatMessage.Text(ChatRole.User, "Fork-only message"));
        Assert.Single(await cold.Turns.LoadTurnsAsync(source.Id));
        Assert.Equal(2, (await cold.Transcript.LoadAsync(source.Id)).Count);
    }

    [Fact]
    public async Task Cleared_history_stays_cleared_and_action_controls_are_not_copied()
    {
        var s = Stores(); var source = await s.Catalog.CreateAsync(Tmp);
        await s.Transcript.AppendAsync(source.Id, ChatMessage.Text(ChatRole.User, "Hidden old history"));
        s.Journal.Append(source.Id, new UserEntry { Text = "Hidden old history" });
        s.Journal.AppendClear(source.Id, 1);
        s.Journal.Append(source.Id, new UserEntry { Text = "Visible history" });
        s.Journal.Append(source.Id, new PermissionEntry { RequestId = "old-request", Title = "Old approval", Permissions = [] });
        s.Journal.Append(source.Id, new TurnChangesEntry { CheckpointId = "old-checkpoint", RepositoryRoot = Tmp, Files = [] });
        var fork = await s.Forks.ForkAsync(source.Id);
        var visible = Stores().Journal.Load(fork.Id);
        Assert.Equal(1, visible.ReconcileFromMessage);
        Assert.Equal("Visible history", Assert.Single(visible.Entries.OfType<UserEntry>()).Text);
        Assert.DoesNotContain(visible.Entries, e => e is PermissionEntry or TurnChangesEntry or QuestionEntry);
    }

    [Fact]
    public async Task Missing_deleted_running_and_agent_sessions_cannot_fork()
    {
        var s = Stores();
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Forks.ForkAsync("missing"));
        var source = await s.Catalog.CreateAsync(Tmp);
        s.Jobs.Save(source.Id, new() { State = "Running" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Forks.ForkAsync(source.Id));
        Assert.Single(await s.Catalog.ListAsync());
        var agent = await s.Catalog.CreateAsync(Tmp, parentSessionId: source.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Forks.ForkAsync(agent.Id));
        await s.Catalog.DeleteAsync(source.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Forks.ForkAsync(source.Id));
        Assert.Single(await s.Catalog.ListAsync());
    }

    [Fact]
    public async Task Competing_catalog_writers_preserve_forks_and_unrelated_changes()
    {
        var s = Stores(); var source = await s.Catalog.CreateAsync(Tmp, "before");
        var independent = new WorkspaceSessionCatalog(Paths);
        var forks = await Task.WhenAll(s.Forks.ForkAsync(source.Id), Stores().Forks.ForkAsync(source.Id));
        await Task.WhenAll(independent.RenameAsync(source.Id, "after"), s.Catalog.SetTemporaryAsync(source.Id, true));
        Assert.Equal(3, (await independent.ListAsync()).Count);
        var current = await independent.GetAsync(source.Id);
        Assert.Equal("after", current!.Title); Assert.True(current.Temporary);
        Assert.Equal(2, forks.Select(f => f.Id).Distinct().Count());
    }

    [Fact]
    public async Task Delete_interleaving_cannot_resurrect_the_source_and_failed_preparation_is_not_published()
    {
        var s = Stores(); var source = await s.Catalog.CreateAsync(Tmp);
        var preparing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fork = s.Catalog.PublishForkAsync(source.Id, async (_, _) => { preparing.SetResult(); await release.Task; }, default);
        await preparing.Task;
        var deletion = new WorkspaceSessionCatalog(Paths).DeleteAsync(source.Id);
        release.SetResult();
        await fork; await deletion;
        Assert.Null(await s.Catalog.GetAsync(source.Id)); Assert.Single(await s.Catalog.ListAsync());
        var remaining = Assert.Single(await s.Catalog.ListAsync());
        await Assert.ThrowsAsync<IOException>(() => s.Catalog.PublishForkAsync(remaining.Id, (_, _) => throw new IOException("fixture failure"), default));
        Assert.Single(await s.Catalog.ListAsync());
    }

    [Fact]
    public void Transcript_copy_closes_uncertain_tools_removes_provider_state_and_does_not_share_mutable_input()
    {
        var input = new JsonObject { ["path"] = "before" };
        var source = new[] { new ChatMessage(ChatRole.Assistant, [new ToolUsePart("call", "shell.exec", input) { IsNativeAsync = true }, new ResponsesReasoningPart(new())]) };
        var fork = ConversationFork.CopyTranscript(source);
        input["path"] = "after";
        var call = Assert.Single(fork[0].Parts.OfType<ToolUsePart>());
        Assert.Equal("before", (string?)call.Input["path"]); Assert.False(call.IsNativeAsync);
        Assert.DoesNotContain(fork.SelectMany(m => m.Parts), p => p is ResponsesReasoningPart);
        var unknown = Assert.Single(fork[1].Parts.OfType<ToolResultPart>());
        Assert.True(unknown.IsError); Assert.Equal("call", unknown.ToolUseId);
    }

    [Fact]
    public void Reused_native_tool_ids_do_not_close_an_uncertain_call_from_a_later_turn()
    {
        var fork = ConversationFork.CopyTranscript([
            new(ChatRole.Assistant, [new ToolUsePart("item_0", "shell.exec", new())]),
            new(ChatRole.Tool, [new ToolResultPart("item_0", "old result")]),
            ChatMessage.Text(ChatRole.User, "next task"),
            new(ChatRole.Assistant, [new ToolUsePart("item_0", "shell.exec", new())]),
        ]);
        Assert.Equal(5, fork.Count);
        Assert.True(Assert.Single(fork[^1].Parts.OfType<ToolResultPart>()).IsError);
    }

    [Fact]
    public async Task Runner_freezes_starts_and_visible_clear_until_the_fork_snapshot_finishes()
    {
        var s = Stores(); var source = await s.Catalog.CreateAsync(Tmp);
        s.Journal.Append(source.Id, new UserEntry { Text = "Keep this visible history" });
        var services = new ServiceCollection(); services.AddLogging();
        services.AddSingleton<IConversationStore>(s.Turns); services.AddSingleton(s.Journal);
        services.AddSingleton(s.Jobs); services.AddSingleton(s.Forks);
        using var provider = services.BuildServiceProvider();
        using var runner = new ConversationRunner(provider, source.Id);
        await runner.LoadHistoryAsync();
        using (var lease = new FileStream(Paths.SessionIndexPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            using var cancel = new CancellationTokenSource();
            var pending = runner.ForkAsync(cancel.Token);
            Assert.False(runner.TrySend("must not start", null));
            runner.ClearTranscript();
            Assert.Single(runner.Entries.OfType<UserEntry>());
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        var fork = await runner.ForkAsync();
        Assert.Contains(s.Journal.Load(fork.Id).Entries.OfType<UserEntry>(), e => e.Text == "Keep this visible history");
    }
}
