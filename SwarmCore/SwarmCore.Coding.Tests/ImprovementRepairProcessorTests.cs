using SwarmCore.Abstractions.Crm;
using SwarmCore.Coding.Improvement;

namespace SwarmCore.Coding.Tests;

public sealed class ImprovementRepairProcessorTests
{
    private sealed class Queue(ImprovementStage stage = ImprovementStage.Reported) : IImprovementRepairQueue
    {
        public ImprovementTicket Ticket { get; private set; } = new(1, "TKT-1",
            new("Bug", new string('a', 64), "Pass", "Fail", "Synthetic steps", "Regression passes", "Talavine", "1.0.1"), stage);
        public Task<IReadOnlyList<ImprovementTicket>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ImprovementTicket>>([Ticket]);
        public Task<ImprovementTicket> ClaimAsync(ImprovementTicket ticket, string owner, CancellationToken ct)
            => Task.FromResult(Ticket = ticket with { Revision = ticket.Revision + 1, ClaimOwner = owner, ClaimExpiresAt = DateTimeOffset.UtcNow.AddMinutes(120) });
        public Task<ImprovementTicket> AdvanceAsync(ImprovementTicket ticket, ImprovementAdvance change, CancellationToken ct)
            => Task.FromResult(Ticket = ImprovementTicketPolicy.Advance(ticket, change));
    }
    private sealed class Executor(ImprovementRepairProof proof) : IImprovementRepairExecutor
    {
        public int Calls { get; private set; }
        public Task<ImprovementRepairProof> RepairAsync(ImprovementTicket ticket, CancellationToken ct) { Calls++; return Task.FromResult(proof); }
    }

    [Fact]
    public async Task ProductScopeDoesNotClaimOrRepairAnotherProduct()
    {
        var queue = new Queue();
        var executor = new Executor(new(true, true, true, new string('b', 40), "proof"));
        var processor = new ImprovementRepairProcessor(queue, executor);
        Assert.Null(await processor.RunOneAsync("worker", default, product: "SwarmCRM"));
        Assert.Null(await processor.RunOneAsync("worker", default, product: "talavine"));
        Assert.Equal(0, queue.Ticket.Revision);
        Assert.Null(queue.Ticket.ClaimOwner);
        Assert.Equal(0, executor.Calls);
        Assert.Equal(ImprovementStage.Validated, (await processor.RunOneAsync("worker", default, product: "Talavine"))!.Stage);
        Assert.Equal(1, executor.Calls);
    }

    [Fact]
    public async Task IncompleteProofReleasesClaimsAcrossEligibleStages()
    {
        foreach (var stage in new[] { ImprovementStage.Reproduced, ImprovementStage.Reported, ImprovementStage.Fixing })
        foreach (var proof in new ImprovementRepairProof[]
        {
            new(true, false, false, null, "Candidate and gate did not pass."),
            new(false, true, true, new string('b', 40), "Baseline did not fail."),
            new(true, false, true, new string('b', 40), "Candidate did not pass."),
            new(true, true, false, new string('b', 40), "Gate did not pass."),
            new(true, true, true, null, "Commit is missing."),
            new(true, true, true, " ", "Commit is blank.")
        })
        {
            var queue = new Queue(stage);
            var executor = new Executor(proof);
            var processor = new ImprovementRepairProcessor(queue, executor);
            var result = await processor.RunOneAsync("worker", default);
            Assert.Equal(ImprovementStage.NeedsInformation, result!.Stage);
            Assert.Null(result.ClaimOwner);
            Assert.Null(result.ClaimExpiresAt);
            Assert.Null(result.ChangeRevision);
            Assert.Equal(proof.Evidence, result.Evidence);
            Assert.Equal(2, result.Revision);
            Assert.Null(await processor.RunOneAsync("worker", default));
            Assert.Equal(1, executor.Calls);
        }
    }

    [Theory]
    [InlineData(ImprovementStage.Reported, 4)]
    [InlineData(ImprovementStage.Reproduced, 3)]
    [InlineData(ImprovementStage.Fixing, 2)]
    public async Task CompleteProofFollowsLifecycleFromEachEligibleStage(ImprovementStage stage, long revision)
    {
        var queue = new Queue(stage);
        var commit = new string('b', 40);
        var result = await new ImprovementRepairProcessor(queue,
            new Executor(new(true, true, true, commit, "All proof passed."))).RunOneAsync("worker", default);
        Assert.Equal(ImprovementStage.Validated, result!.Stage);
        Assert.Equal(revision, result.Revision);
        Assert.Equal(commit, result.ChangeRevision);
        Assert.Null(result.ClaimOwner);
        Assert.Null(result.ClaimExpiresAt);
        Assert.Null(result.ReleasedVersion);
    }

    [Fact]
    public void NeedsInformationRetainsLifecycleGuardsAndCanBeReportedAgain()
    {
        var ticket = new Queue(ImprovementStage.Reproduced).Ticket;
        Assert.Throws<InvalidOperationException>(() => ImprovementTicketPolicy.Advance(ticket, new(1, ImprovementStage.NeedsInformation, "proof")));
        Assert.Throws<ArgumentException>(() => ImprovementTicketPolicy.Advance(ticket, new(0, ImprovementStage.NeedsInformation, "")));
        Assert.Throws<InvalidOperationException>(() => ImprovementTicketPolicy.Advance(ticket, new(0, ImprovementStage.Validated, "proof", new string('b', 40))));
        var waiting = ImprovementTicketPolicy.Advance(ticket, new(0, ImprovementStage.NeedsInformation, "Missing proof."));
        Assert.Throws<InvalidOperationException>(() => ImprovementTicketPolicy.Advance(waiting, new(1, ImprovementStage.Fixing, "proof")));
        Assert.Equal(ImprovementStage.Reported, ImprovementTicketPolicy.Advance(waiting, new(1, ImprovementStage.Reported, "New evidence.")).Stage);
    }

    [Fact]
    public async Task OnlyProvenCandidatesBecomeValidatedAndNeverReleased()
    {
        var queue = new Queue(); var executor = new Executor(new(true, true, true, new string('b', 40), "Baseline fails; patch and gate pass."));
        var processor = new ImprovementRepairProcessor(queue, executor);
        var result = await processor.RunOneAsync("worker", default);
        Assert.Equal(ImprovementStage.Validated, result!.Stage);
        Assert.Null(result.ClaimOwner);
        Assert.Null(result.ReleasedVersion);
        Assert.Null(await processor.RunOneAsync("worker", default));
        Assert.Equal(1, executor.Calls);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task MissingProofCannotReachDailyBuildQueue(bool baseline, bool candidate, bool gate)
    {
        var queue = new Queue(); var executor = new Executor(new(baseline, candidate, gate, new string('b', 40), "Missing deterministic proof."));
        var result = await new ImprovementRepairProcessor(queue, executor).RunOneAsync("worker", default);
        Assert.Equal(ImprovementStage.NeedsInformation, result!.Stage);
        Assert.Null(result.ChangeRevision);
    }

    [Fact]
    public async Task ActiveClaimPreventsAnotherCodingRun()
    {
        var queue = new Queue(); await queue.ClaimAsync(queue.Ticket, "other", default);
        var executor = new Executor(new(true, true, true, new string('b', 40), "proof"));
        Assert.Null(await new ImprovementRepairProcessor(queue, executor).RunOneAsync("worker", default));
        Assert.Equal(0, executor.Calls);
    }

    [Fact]
    public async Task ExplicitResumeRenewsOnlyTheSelectedOwnersTicket()
    {
        var queue = new Queue(); await queue.ClaimAsync(queue.Ticket, "worker", default);
        var executor = new Executor(new(true, true, true, new string('b', 40), "proof"));
        var processor = new ImprovementRepairProcessor(queue, executor);
        Assert.Null(await processor.RunOneAsync("other", default, 1));
        Assert.Null(await processor.RunOneAsync("worker", default, 2));
        Assert.Null(await processor.RunOneAsync("worker", default));
        Assert.Equal(ImprovementStage.Validated, (await processor.RunOneAsync("worker", default, 1))!.Stage);
        Assert.Equal(1, executor.Calls);
    }
}
