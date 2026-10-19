using System.Text.Json;

namespace SwarmCore.HarnessLearning;

public sealed record LearningModelReply(string Text, long? Tokens, decimal? Cost);
public sealed class LearningModelException(string code, bool mayHaveExecuted, Exception inner)
    : Exception("Learning model call failed: " + code, inner)
{
    public string Code { get; } = code;
    public bool MayHaveExecuted { get; } = mayHaveExecuted;
}
public interface IHarnessLearningModel
{
    string Identity { get; }
    Task<LearningModelReply> CompleteAsync(string prompt, CancellationToken ct);
}
public sealed class HarnessLearningOptions
{
    public bool Enabled { get; set; } = true;
    public TimeSpan Interval { get; set; } = TimeSpan.FromDays(1);
    public TimeSpan CycleTimeout { get; set; } = TimeSpan.FromMinutes(10);
    public int Repetitions { get; set; } = 2;
    public long MaximumTokensPerCall { get; set; } = 8_000;
    public long MaximumTokensPerCycle { get; set; } = 136_000;
}

/// <summary>One bounded daily proposal and paired experiment. All execution and selection is shared;
/// hosts supply model credentials, a private root, lifecycle cancellation and current-scope validation.</summary>
public sealed class AutomaticHarnessLearning(FileHarnessLearningState store, string partition,
    HarnessLearningOptions options, Func<bool>? isCurrent = null, TimeProvider? clock = null, IHarnessLearningWorkload? workload = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly IHarnessLearningWorkload _workload = workload ?? new StructuredPreparationWorkload();
    public static string EnvironmentHash(IHarnessLearningModel model, IHarnessLearningWorkload? workload = null, HarnessLearningOptions? options = null)
    {
        options ??= new();
        workload ??= new StructuredPreparationWorkload();
        return HarnessEvidence.Hash(workload.Id + "\n" + model.Identity + "\n" + workload.SuiteHash
            + "\n" + JsonSerializer.Serialize(workload.Strategies.OrderBy(p => p.Key, StringComparer.Ordinal))
            + "\n" + JsonSerializer.Serialize(new { options.Repetitions, options.MaximumTokensPerCall, options.MaximumTokensPerCycle, options.CycleTimeout }));
    }

    public async Task TickAsync(IHarnessLearningModel model, CancellationToken ct = default)
    {
        if (!options.Enabled || await store.IsPausedAsync(ct)) return;
        if (options.Interval < TimeSpan.FromHours(1) || options.CycleTimeout <= TimeSpan.Zero
            || options.CycleTimeout > TimeSpan.FromHours(1) || options.Repetitions is < 1 or > 4
            || options.MaximumTokensPerCall < 1 || options.MaximumTokensPerCycle < options.MaximumTokensPerCall)
            throw new ArgumentException("Invalid automatic learning bounds.");
        using var lease = store.TryLease();
        if (lease is null) return;
        var state = await store.ReadAsync(ct);
        var now = _clock.GetUtcNow();
        // The reservation survives crashes, restarts and route changes. No automatic retry of uncertain spend.
        if (state.NextRunAt > now) return;
        // A user explicitly resumed after the three-failure auto-pause. Cadence/reservations still apply.
        if (state.ConsecutiveFailures >= 3) state = state with { ConsecutiveFailures = 0 };
        var environment = EnvironmentHash(model, _workload, options);
        if (state.EnvironmentHash != environment) state = state with { EnvironmentHash = environment,
            ActiveStrategy = _workload.BaselineStrategy, PreviousStrategy = null, TriedStrategies = [], ReportId = null, SearchComplete = false };
        if (state.SearchComplete) return;
        var available = _workload.Strategies.Keys.Except(state.TriedStrategies.Append(state.ActiveStrategy)).ToArray();
        if (available.Length == 0)
        {
            await store.SaveAsync(state with { Stage = "Converged", SearchComplete = true,
                Outcome = "All shipped strategies have been attempted for this model and contract. Inspect retained evidence for incomplete attempts." }, ct);
            return;
        }
        state = state with { Stage = "Proposing", Model = string.Join(" / ", model.Identity.Split('\n').Take(2)),
            CandidateStrategy = null, FailureCode = null, NextRunAt = now + options.Interval, RunId = Guid.NewGuid(),
            CompletedTrials = 0, TotalTrials = _workload.Tasks.Count * options.Repetitions * 2,
            ReportedTokens = 0, ReservedTokens = 0, PendingCall = false, UnknownUsageCalls = 0, RecordedCost = 0, UnpricedCalls = 0, Outcome = null };
        await store.SaveAsync(state, ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(options.CycleTimeout);
        async Task<LearningModelReply> Call(string prompt, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (isCurrent?.Invoke() == false) throw new OperationCanceledException("Learning scope changed.");
            if (await store.IsPausedAsync(token)) throw new OperationCanceledException("Learning paused.");
            if (options.MaximumTokensPerCycle - state.ReservedTokens < options.MaximumTokensPerCall)
                throw new InvalidOperationException("Learning token reservation exhausted.");
            state = state with { ReservedTokens = checked(state.ReservedTokens + options.MaximumTokensPerCall), PendingCall = true };
            await store.SaveAsync(state, token); // Reserve before physical work, including a proposal.
            LearningModelReply reply;
            try { reply = await model.CompleteAsync(prompt, token); }
            catch (Exception ex)
            {
                var uncertain = ex is not LearningModelException { MayHaveExecuted: false };
                state = state with { PendingCall = false, FailureCode = ex is LearningModelException failure ? failure.Code : ex.GetType().Name,
                    UnknownUsageCalls = state.UnknownUsageCalls + (uncertain ? 1 : 0), UnpricedCalls = state.UnpricedCalls + (uncertain ? 1 : 0) };
                await store.SaveAsync(state, CancellationToken.None);
                throw;
            }
            state = state with { PendingCall = false, ReportedTokens = checked(state.ReportedTokens + Math.Max(0, reply.Tokens ?? 0)),
                UnknownUsageCalls = state.UnknownUsageCalls + (reply.Tokens is null or < 0 ? 1 : 0),
                RecordedCost = state.RecordedCost + Math.Max(0, reply.Cost ?? 0),
                UnpricedCalls = state.UnpricedCalls + (reply.Cost is null or < 0 ? 1 : 0) };
            await store.SaveAsync(state, CancellationToken.None);
            if (reply.Tokens is null or < 0 || reply.Tokens > options.MaximumTokensPerCall)
                throw new InvalidDataException("Learning usage was unknown or exceeded its bound.");
            return reply;
        }
        try
        {
            var proposal = await Call(_workload.ProposalPrompt(state.ActiveStrategy, available), deadline.Token);
            var candidate = ParseProposal(proposal.Text);
            if (candidate == "stop")
            {
                state = state with { Stage = "Converged", SearchComplete = true,
                    ConsecutiveFailures = 0, Outcome = "The proposer found no useful change. No further proposals for this model and contract." };
                return;
            }
            if (!available.Contains(candidate, StringComparer.Ordinal)) throw new InvalidDataException("Invalid proposal.");
            // Claim this hypothesis before executing it. An interrupted hypothesis is never silently retried.
            state = state with { Stage = "Evaluating", CandidateStrategy = candidate, TriedStrategies = state.TriedStrategies.Append(candidate).ToArray() };
            await store.SaveAsync(state, deadline.Token);
            var scope = new HarnessExperimentScope(partition, _workload.Id, environment, _workload.SuiteHash);
            HarnessRevision Revision(string id) => new(id, "Reviewed structured preparation strategy: " + id,
                new Dictionary<string, string> { ["strategy"] = id });
            var plan = new HarnessExperimentPlan(scope, Revision(state.ActiveStrategy), [Revision(candidate)],
                _workload.Tasks,
                new() { Repetitions = options.Repetitions, MaximumCandidates = 1, MaximumTrials = state.TotalTrials,
                    MaximumTokens = options.MaximumTokensPerCycle, MaximumTokensPerTrial = options.MaximumTokensPerCall,
                    MaximumDuration = options.CycleTimeout, TrialTimeout = options.CycleTimeout < TimeSpan.FromMinutes(1) ? options.CycleTimeout : TimeSpan.FromMinutes(1), EditableComponents = ["strategy"] });
            var report = await new HarnessExperimentRunner(new FileHarnessExperimentHistory(Path.Combine(store.Root, "reports"), store.EnsureCurrentScope))
                .RunAsync(plan, new WorkloadDomain(_workload, Call), deadline.Token, async _ =>
                {
                    state = state with { CompletedTrials = state.CompletedTrials + 1 };
                    await store.SaveAsync(state, CancellationToken.None);
                });
            state = state with { ReportId = report.Id };
            state = state with { ConsecutiveFailures = report.StopReason is null ? 0 : state.ConsecutiveFailures + 1 };
            if (report.StopReason is null && report.RecommendedRevisionId == candidate
                && isCurrent?.Invoke() != false && !await store.IsPausedAsync(CancellationToken.None))
                state = state with { PreviousStrategy = state.ActiveStrategy, ActiveStrategy = candidate,
                    Stage = "Retained", Outcome = "Verified strategy retained for this exact model and preparation contract." };
            else state = state with { Stage = report.StopReason is null ? "Baseline retained" : "Stopped",
                Outcome = HarnessExperimentOverview.Explain(report.StopReason ?? report.Comparisons.FirstOrDefault()?.Reason ?? "incomplete_evaluation") };
        }
        catch (OperationCanceledException) { state = state with { Stage = "Stopped", ConsecutiveFailures = state.ConsecutiveFailures + 1, Outcome = "Cancelled, paused, timed out or scope changed. Reserved spend remains counted." }; }
        catch (Exception ex) { state = state with { Stage = "Stopped", FailureCode = ex is LearningModelException failure ? failure.Code : ex.GetType().Name,
            ConsecutiveFailures = state.ConsecutiveFailures + 1, Outcome = "Proposal, execution or usage verification failed. Baseline retained; inspect recorded evidence." }; }
        finally
        {
            if (state.ConsecutiveFailures >= 3)
            {
                await store.SetPausedAsync(true, CancellationToken.None);
                state = state with { Outcome = "Three cycles failed in a row. Automatic learning paused; existing strategy and spend reservations were preserved." };
            }
            await store.SaveAsync(state, CancellationToken.None);
        }
    }
    private sealed class WorkloadDomain(IHarnessLearningWorkload workload, Func<string, CancellationToken, Task<LearningModelReply>> call) : IHarnessExperimentDomain
    {
        public async Task<HarnessTrialObservation> RunAsync(HarnessTrialRequest request, CancellationToken ct)
        {
            var reply = await call(workload.TrialPrompt(request), ct);
            return workload.Verify(request, reply);
        }
    }
    private static string ParseProposal(string text)
    {
        text = text.Trim();
        if (!text.StartsWith('{') && !text.StartsWith('"')) return text;
        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.ValueKind == JsonValueKind.String) return doc.RootElement.GetString()!;
        var properties = doc.RootElement.EnumerateObject().ToArray();
        if (properties.Length == 1 && properties[0].Name == "strategy" && properties[0].Value.ValueKind == JsonValueKind.String)
            return properties[0].Value.GetString()!;
        throw new InvalidDataException("Invalid strategy proposal shape.");
    }
}
