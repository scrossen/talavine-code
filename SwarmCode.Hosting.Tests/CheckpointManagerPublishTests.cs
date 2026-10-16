using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.ToolUseEngine;

namespace SwarmCode.Hosting.Tests;

/// <summary>
/// Drives SwarmCore's checkpoint manager against the file store and the coding validator the
/// way the engine does at a durable boundary: the receipts are persisted first, then the
/// checkpoint is validated and saved so a later turn can restore it.
/// </summary>
public class CheckpointManagerPublishTests : TempDirTest
{
    private static readonly WorkingContextIdentity Identity = new(Guid.Parse("5c0d1a6e-0000-4000-8000-5c0d1a6e0001"), "conv-publish", null);

    [Fact]
    public async Task Publish_at_a_durable_boundary_persists_receipts_then_saves_a_restorable_checkpoint()
    {
        var store = new FileWorkingContextCheckpointStore(Paths);
        var manager = new WorkingContextCheckpointManager(store, new CodingWorkingContextSourceValidator());
        var authority = new WorkingContextAuthority { Objective = "fix the build", Digest = "digest-1" };
        var state = new WorkingContextState(Identity, authority);
        var durable = new List<ChatMessage>
        {
            ChatMessage.Text(ChatRole.User, "fix the build"),
            new(ChatRole.Assistant, [new ToolUsePart("c1", "shell.exec", new System.Text.Json.Nodes.JsonObject { ["command"] = "dotnet build" })]),
            new(ChatRole.Tool, [new ToolResultPart("c1", "{\"exitCode\":0}")]),
        };
        var persisted = 0;
        state.PersistDurableBoundaryAsync = _ => { persisted++; state.CommittedPrefixCount = durable.Count; return Task.CompletedTask; };

        var restored = await manager.RestoreAsync(state, durable, CancellationToken.None);
        Assert.Equal(WorkingContextLoadStatus.Missing, restored.Status);

        var snapshot = new WorkingContextSnapshot([], [], [], [], [], []);
        var result = new WorkingContextCompactionResult(durable, snapshot, 100, 100, 0, true);
        var saved = await manager.PublishAsync(state, result, durable, CancellationToken.None, isCompaction: false);

        Assert.Equal(1, persisted); // receipts hit the session store before anything else
        Assert.True(saved);
        Assert.Equal(1, state.Generation);

        // A new turn restores it when the durable prefix is unchanged.
        var next = new WorkingContextState(Identity, authority);
        var load = await manager.RestoreAsync(next, durable, CancellationToken.None);
        Assert.Equal(WorkingContextLoadStatus.Available, load.Status);
        Assert.Equal(durable.Count, next.ResumeAfterMessageCount);
    }
}

public class LegacyRevalidationTests
{
    [Fact]
    public async Task Legacy_revalidation_returns_the_whole_transcript_with_complete_proof()
    {
        var validator = new CodingWorkingContextSourceValidator();
        var identity = new WorkingContextIdentity(Guid.NewGuid(), "conv", null);
        var transcript = new List<ChatMessage> { ChatMessage.Text(ChatRole.User, "first"), ChatMessage.Text(ChatRole.Assistant, "done") };
        var legacy = await validator.RevalidateLegacyAsync(identity, new WorkingContextAuthority { Objective = "o", Digest = "d" }, transcript);
        Assert.Equal(transcript, legacy.Messages);
        Assert.True(legacy.SourceProofComplete);
        Assert.Null(legacy.Reason);
        Assert.Empty(legacy.SourcePacks);
    }
}
