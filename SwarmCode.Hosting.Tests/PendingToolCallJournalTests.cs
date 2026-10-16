using Microsoft.Extensions.DependencyInjection;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.ToolUseEngine;

namespace SwarmCode.Hosting.Tests;

public class PendingToolCallJournalTests : TempDirTest
{
    private static PendingToolCall Call(string id, ToolSideEffect effect = ToolSideEffect.Exec)
        => new(id, "shell.exec", "{\"command\":\"" + id + "\"}", effect, new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Requested_then_started_rows_fold_into_pending_calls_in_request_order()
    {
        var journal = new FilePendingToolCallJournal(Paths);
        await journal.RecordRequestedAsync("conv", [Call("c1"), Call("c2", ToolSideEffect.ReadOnly)]);
        await journal.RecordStartedAsync("conv", "c1");

        var pending = await journal.LoadAsync("conv");
        Assert.Equal(["c1", "c2"], pending.Select(p => p.CallId));
        Assert.NotNull(pending[0].StartedUtc);
        Assert.Null(pending[1].StartedUtc);
        Assert.Equal(ToolSideEffect.ReadOnly, pending[1].SideEffect);
        Assert.Contains("\"command\":\"c2\"", pending[1].InputJson);
        Assert.Equal("shell.exec", pending[1].ToolName);
    }

    [Fact]
    public async Task Clear_removes_the_file_and_a_fresh_process_sees_nothing()
    {
        var journal = new FilePendingToolCallJournal(Paths);
        await journal.RecordRequestedAsync("conv", [Call("c1")]);
        Assert.True(File.Exists(Path.Combine(Paths.PendingToolCallsDirectory, "conv.jsonl")));
        await journal.ClearAsync("conv");
        Assert.False(File.Exists(Path.Combine(Paths.PendingToolCallsDirectory, "conv.jsonl")));
        Assert.Empty(await new FilePendingToolCallJournal(Paths).LoadAsync("conv"));
    }

    [Fact]
    public async Task Rows_survive_a_new_instance_like_a_restart()
    {
        await new FilePendingToolCallJournal(Paths).RecordRequestedAsync("conv", [Call("c1")]);
        await new FilePendingToolCallJournal(Paths).RecordStartedAsync("conv", "c1");
        var pending = Assert.Single(await new FilePendingToolCallJournal(Paths).LoadAsync("conv"));
        Assert.Equal("c1", pending.CallId);
        Assert.NotNull(pending.StartedUtc);
    }

    [Fact]
    public async Task Started_before_requested_is_kept_as_a_started_call()
    {
        var journal = new FilePendingToolCallJournal(Paths);
        await journal.RecordStartedAsync("conv", "early");
        var pending = Assert.Single(await journal.LoadAsync("conv"));
        Assert.Equal("early", pending.CallId);
        Assert.NotNull(pending.StartedUtc);
    }

    [Fact]
    public async Task Corrupt_lines_are_skipped()
    {
        var journal = new FilePendingToolCallJournal(Paths);
        await journal.RecordRequestedAsync("conv", [Call("c1")]);
        await File.AppendAllTextAsync(Path.Combine(Paths.PendingToolCallsDirectory, "conv.jsonl"), "not json\n");
        await journal.RecordStartedAsync("conv", "c1");
        var pending = Assert.Single(await journal.LoadAsync("conv"));
        Assert.NotNull(pending.StartedUtc);
    }

    [Fact]
    public async Task Invalid_conversation_ids_are_rejected()
    {
        var journal = new FilePendingToolCallJournal(Paths);
        await Assert.ThrowsAsync<ArgumentException>(() => journal.RecordRequestedAsync("../x", [Call("c1")]));
    }

    [Fact]
    public void Host_composition_registers_the_file_journal()
    {
        var repo = Path.Combine(Tmp, "repo");
        Directory.CreateDirectory(repo);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSwarmCodeHost(new SwarmCodeHostOptions { RepositoryRoot = repo, DataPaths = Paths });
        using var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        Assert.IsType<FilePendingToolCallJournal>(sp.GetRequiredService<IPendingToolCallJournal>());
    }
}
