using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SwarmCore.Agent.ToolUseEngine;

namespace SwarmCode.Hosting.Tests;

public class WorkingContextCheckpointStoreTests : TempDirTest
{
    private static readonly WorkingContextIdentity Identity = new(Guid.Parse("5c0d1a6e-0000-4000-8000-5c0d1a6e0001"), "conv-1", null);

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static WorkingContextCheckpoint Checkpoint(long generation, ManualTime time, string digest = "d1") => new()
    {
        Identity = Identity,
        Generation = generation,
        Authority = new WorkingContextAuthority { Objective = "fix the build", Digest = digest },
        Snapshot = new WorkingContextSnapshot([], [], [], [], [], []),
        ProjectionJson = "[]",
        DurableMessageCount = 0,
        DurablePrefixDigest = "",
        RetentionPolicyVersion = new WorkingContextRetentionPolicy().Version,
        CreatedAtUtc = time.Now,
        ExpiresAtUtc = time.Now + TimeSpan.FromDays(1),
    };

    [Fact]
    public async Task Save_Load_RoundTrip_WithCompareAndSwap()
    {
        var time = new ManualTime();
        var store = new FileWorkingContextCheckpointStore(Paths, time: time);
        Assert.Equal(WorkingContextLoadStatus.Missing, (await store.LoadAsync(Identity)).Status);

        Assert.True(await store.TrySaveAsync(Checkpoint(1, time), expectedGeneration: 0));
        var loaded = await store.LoadAsync(Identity);
        Assert.Equal(WorkingContextLoadStatus.Available, loaded.Status);
        Assert.Equal(1, loaded.Generation);
        Assert.Equal("fix the build", loaded.Checkpoint!.Authority.Objective);

        // A stale writer loses; the current writer wins.
        Assert.False(await store.TrySaveAsync(Checkpoint(1, time), expectedGeneration: 0));
        Assert.True(await store.TrySaveAsync(Checkpoint(2, time, "d2"), expectedGeneration: 1));
        Assert.Equal("d2", (await store.LoadAsync(Identity)).Checkpoint!.Authority.Digest);

        // A fresh store instance reads the same file.
        Assert.Equal(2, (await new FileWorkingContextCheckpointStore(Paths, time: time).LoadAsync(Identity)).Generation);
    }

    [Fact]
    public async Task Delete_LeavesTombstone_ThatBlocksStaleWriters_AndExpiryRequiresReconstruction()
    {
        var time = new ManualTime();
        var store = new FileWorkingContextCheckpointStore(Paths, time: time);
        Assert.True(await store.TrySaveAsync(Checkpoint(1, time), 0));
        await store.DeleteAsync(Identity);
        var afterDelete = await store.LoadAsync(Identity);
        Assert.Equal(WorkingContextLoadStatus.Missing, afterDelete.Status);
        Assert.Equal(2, afterDelete.Generation);
        Assert.False(await store.TrySaveAsync(Checkpoint(2, time), 1)); // stale
        Assert.True(await store.TrySaveAsync(Checkpoint(3, time), 2));

        time.Now += TimeSpan.FromDays(2);
        Assert.Equal(WorkingContextLoadStatus.ReconstructionRequired, (await store.LoadAsync(Identity)).Status);
    }

    [Fact]
    public async Task Corrupt_File_RequiresReconstruction_AndInvalidGenerations_Throw()
    {
        var time = new ManualTime();
        var store = new FileWorkingContextCheckpointStore(Paths, time: time);
        File.WriteAllText(store.PathFor(Identity), "{not json");
        Assert.Equal(WorkingContextLoadStatus.ReconstructionRequired, (await store.LoadAsync(Identity)).Status);
        await Assert.ThrowsAsync<ArgumentException>(() => store.TrySaveAsync(Checkpoint(5, time), 1));
    }

    [Fact]
    public async Task Validator_AcceptsMatchingAuthority_RejectsDriftAndInjectedSources()
    {
        var validator = new CodingWorkingContextSourceValidator();
        var time = new ManualTime();
        var checkpoint = Checkpoint(1, time) with { SourceProofComplete = true };
        Assert.True((await validator.ValidateAsync(checkpoint, checkpoint.Authority)).Valid);
        await AssertRejected("checkpoint-authority-changed", checkpoint, checkpoint.Authority with { Digest = "other" });
        await AssertRejected("checkpoint-authority-changed", checkpoint, checkpoint.Authority with { SteeringRevision = checkpoint.Authority.SteeringRevision + 1 });
        await AssertRejected("checkpoint-source-proof-incomplete", checkpoint with { SourceProofComplete = false }, checkpoint.Authority);

        // Account-scoped sources cannot be revalidated in a coding workspace, so a checkpoint carrying any is never replayed.
        await AssertRejected("checkpoint-source-revalidation-unavailable",
            checkpoint with { SourcePacks = [new SwarmCore.Abstractions.Context.ContextPack { Purpose = "mail", Audience = "agent" }] },
            checkpoint.Authority);
        await AssertRejected("checkpoint-source-revalidation-unavailable",
            checkpoint with { ObservedStates = [new SwarmCore.Agent.Continual.MissionObservedState(SwarmCore.Agent.Continual.MissionEvidenceKind.StateObservation, "inbox", "v1")] },
            checkpoint.Authority);

        async Task AssertRejected(string reason, WorkingContextCheckpoint candidate, WorkingContextAuthority authority)
        {
            var result = await validator.ValidateAsync(candidate, authority);
            Assert.False(result.Valid);
            Assert.Equal(reason, result.Reason);
        }
    }

    [Fact]
    public void Host_RegistersCheckpointManager()
    {
        var repo = Path.Combine(Tmp, "repo");
        Directory.CreateDirectory(repo);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSwarmCodeHost(new SwarmCodeHostOptions { RepositoryRoot = repo, DataPaths = Paths });
        using var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        Assert.IsType<FileWorkingContextCheckpointStore>(sp.GetRequiredService<IWorkingContextCheckpointStore>());
        Assert.IsType<CodingWorkingContextSourceValidator>(sp.GetRequiredService<IWorkingContextSourceValidator>());
        Assert.NotNull(sp.GetRequiredService<WorkingContextCheckpointManager>());
    }
}
