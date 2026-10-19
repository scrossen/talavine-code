using System.Diagnostics;

namespace SwarmCore.HarnessLearning;

/// <summary>Bounded paired evaluations. Missing trials never disappear from the denominator or become wins.</summary>
public sealed class HarnessExperimentRunner(IHarnessExperimentHistory history)
{
    public async Task<HarnessExperimentReport> RunAsync(HarnessExperimentPlan plan, IHarnessExperimentDomain domain,
        CancellationToken ct = default, Func<HarnessTrial, Task>? trialCompleted = null)
    {
        Validate(plan);
        ArgumentNullException.ThrowIfNull(domain);
        // Snapshot mutable caller collections before any awaits or execution.
        plan = Snapshot(plan);
        var prior = await history.ReadAsync(plan.Scope, ct).ConfigureAwait(false);
        var tried = prior.Where(r => r.Scope == plan.Scope && r.Baseline.Fingerprint == plan.Baseline.Fingerprint)
            .SelectMany(r => r.Comparisons).Where(c => c.Reason != "already_evaluated")
            .Select(c => c.RevisionHash).ToHashSet(StringComparer.Ordinal);
        var trials = new List<HarnessTrial>();
        var comparisons = new List<HarnessComparison>();
        var started = DateTimeOffset.UtcNow;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(plan.Policy.MaximumDuration);
        long tokens = 0;
        string? stop = null;

        foreach (var candidate in plan.Candidates)
        {
            if (tried.Contains(candidate.Fingerprint))
            {
                comparisons.Add(Reject(candidate, "already_evaluated"));
                continue;
            }
            // Alternate order to limit systematic warm-up/time bias; each execution uses fresh state.
            foreach (var task in plan.Tasks)
            {
                for (var repetition = 0; repetition < plan.Policy.Repetitions; repetition++)
                {
                    var order = repetition % 2 == 0 ? new[] { plan.Baseline, candidate } : new[] { candidate, plan.Baseline };
                    foreach (var revision in order)
                    {
                        if (deadline.IsCancellationRequested) { stop = ct.IsCancellationRequested ? "cancelled" : "deadline"; break; }
                        if (trials.Count >= plan.Policy.MaximumTrials) { stop = "trial_budget"; break; }
                        // Reserve a whole trial before dispatch. An adapter must enforce this physical ceiling.
                        if (plan.Policy.MaximumTokens - tokens < plan.Policy.MaximumTokensPerTrial) { stop = "token_budget"; break; }
                        using var trialDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                        trialDeadline.CancelAfter(plan.Policy.TrialTimeout);
                        var elapsed = Stopwatch.StartNew();
                        HarnessTrial trial;
                        try
                        {
                            var observation = await domain.RunAsync(new(plan.Scope, revision, task, repetition,
                                plan.Policy.MaximumTokensPerTrial), trialDeadline.Token).ConfigureAwait(false);
                            var valid = double.IsFinite(observation.Score) && observation.Score is >= 0 and <= 1
                                && !string.IsNullOrWhiteSpace(observation.VerifierId)
                                && HarnessEvidence.IsHash(observation.EvidenceHash) && observation.ToolCalls >= 0;
                            var failure = trialDeadline.IsCancellationRequested ? "trial_timeout"
                                : !valid ? "invalid_receipt" : !observation.InvariantsPassed ? "invariant_failed"
                                : observation.Tokens is null ? "unknown_usage"
                                : observation.Tokens < 0 || observation.Tokens > plan.Policy.MaximumTokensPerTrial ? "invalid_usage" : null;
                            trial = new(revision.Fingerprint, task.Id, task.Split, repetition,
                                valid ? observation.Score : 0, valid && observation.InvariantsPassed,
                                valid ? observation.VerifierId : null, valid ? observation.EvidenceHash : null,
                                observation.Tokens, observation.ToolCalls, elapsed.Elapsed.TotalMilliseconds, failure);
                        }
                        catch (OperationCanceledException) when (trialDeadline.IsCancellationRequested)
                        {
                            trial = FailedTrial(revision, task, repetition, elapsed, "trial_timeout");
                        }
                        catch (Exception)
                        {
                            // Raw provider messages, task data and tool output do not enter shared learning history.
                            trial = FailedTrial(revision, task, repetition, elapsed, "execution_failed");
                        }
                        trials.Add(trial);
                        if (trialCompleted is not null) await trialCompleted(trial).ConfigureAwait(false);
                        if (trial.Tokens is >= 0 && trial.Tokens <= plan.Policy.MaximumTokensPerTrial)
                            tokens = checked(tokens + trial.Tokens.Value);
                        // Unaccounted or malformed execution cannot silently buy more model calls.
                        if (trial.FailureCode is not null) { stop = trial.FailureCode; break; }
                    }
                    if (stop is not null) break;
                }
                if (stop is not null) break;
            }
            comparisons.Add(stop is null ? Compare(plan, candidate, trials) : Reject(candidate, stop));
            tried.Add(candidate.Fingerprint);
            if (stop is not null) break;
        }
        var winner = comparisons.Where(c => c.Accepted).OrderByDescending(c => c.HeldOutGain)
            .ThenByDescending(c => c.DevelopmentGain).FirstOrDefault();
        var report = new HarnessExperimentReport(Guid.NewGuid(), plan.Scope, started, plan.Baseline, plan.Candidates,
            plan.Policy, trials.ToArray(), comparisons.ToArray(), stop is null ? winner?.RevisionId : null, stop);
        // Cancellation stops new work, but the already-observed evidence must survive for inspection.
        using var saveDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await history.AppendAsync(report, saveDeadline.Token).ConfigureAwait(false);
        return report;
    }

    private static HarnessComparison Compare(HarnessExperimentPlan plan, HarnessRevision candidate, List<HarnessTrial> all)
    {
        // Baselines are paired per candidate, not pooled across rounds with different temporal conditions.
        var count = plan.Tasks.Count * plan.Policy.Repetitions * 2;
        var trials = all.TakeLast(count).ToArray();
        var baseline = trials.Where(t => t.RevisionHash == plan.Baseline.Fingerprint).ToArray();
        var changed = trials.Where(t => t.RevisionHash == candidate.Fingerprint).ToArray();
        if (baseline.Length != count / 2 || changed.Length != count / 2 || trials.Any(t => t.FailureCode is not null))
            return Reject(candidate, "incomplete_evaluation");
        var development = Gain(HarnessTaskSplit.Development);
        var heldout = Gain(HarnessTaskSplit.HeldOut);
        var reason = development.Gain <= development.Floor ? "development_gain_below_floor"
            : heldout.Gain <= heldout.Floor ? "heldout_gain_below_floor"
            : changed.Sum(t => t.Tokens!.Value) > baseline.Sum(t => t.Tokens!.Value) * plan.Policy.MaximumTokenRatio ? "token_cost_regression"
            : changed.Sum(t => t.DurationMilliseconds) > baseline.Sum(t => t.DurationMilliseconds) * plan.Policy.MaximumDurationRatio ? "latency_regression"
            : "measured_improvement";
        return new(candidate.Id, candidate.Fingerprint, candidate.Hypothesis, reason == "measured_improvement", reason,
            development.Gain, heldout.Gain, development.Floor, heldout.Floor);

        (double Gain, double Floor) Gain(HarnessTaskSplit split)
        {
            // Repetitions are clustered by task; they are not independent extra tasks.
            var pairs = plan.Tasks.Where(t => t.Split == split).Select(task =>
                Enumerable.Range(0, plan.Policy.Repetitions).Select(repetition =>
                    changed.Single(t => t.TaskId == task.Id && t.Repetition == repetition).Score
                    - baseline.Single(t => t.TaskId == task.Id && t.Repetition == repetition).Score).ToArray()).ToArray();
            var differences = pairs.Select(p => p.Average()).ToArray();
            var mean = differences.Average();
            var variance = differences.Sum(d => (d - mean) * (d - mean)) / (differences.Length - 1);
            var taskError = Math.Sqrt(variance / differences.Length);
            var repeatError = plan.Policy.Repetitions == 1 ? 0 : Math.Sqrt(
                pairs.Sum(p => p.Sum(d => Math.Pow(d - p.Average(), 2)))
                / (pairs.Length * (plan.Policy.Repetitions - 1)) / (pairs.Length * plan.Policy.Repetitions));
            var noise = plan.Policy.NoiseMultiplier * Math.Max(taskError, repeatError);
            return (mean, Math.Max(plan.Policy.MinimumScoreGain, noise));
        }
    }

    private static HarnessTrial FailedTrial(HarnessRevision revision, HarnessExperimentTask task, int repetition,
        Stopwatch elapsed, string code) => new(revision.Fingerprint, task.Id, task.Split, repetition,
            0, false, null, null, null, 0, elapsed.Elapsed.TotalMilliseconds, code);

    private static HarnessComparison Reject(HarnessRevision candidate, string reason)
        => new(candidate.Id, candidate.Fingerprint, candidate.Hypothesis, false, reason, 0, 0, 0, 0);

    private static HarnessExperimentPlan Snapshot(HarnessExperimentPlan plan)
        => plan with
        {
            Baseline = plan.Baseline with { Components = new SortedDictionary<string, string>(plan.Baseline.Components.ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal) },
            Candidates = plan.Candidates.Select(c => c with { Components = new SortedDictionary<string, string>(c.Components.ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal) }).ToArray(),
            Tasks = plan.Tasks.ToArray(),
            Policy = plan.Policy with { EditableComponents = plan.Policy.EditableComponents.ToArray() }
        };

    private static void Validate(HarnessExperimentPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var p = plan.Policy;
        if (string.IsNullOrWhiteSpace(plan.Scope.Partition) || string.IsNullOrWhiteSpace(plan.Scope.Workload)
            || !HarnessEvidence.IsHash(plan.Scope.EnvironmentHash) || !HarnessEvidence.IsHash(plan.Scope.SuiteHash))
            throw new ArgumentException("A host-bound partition, workload, environment and suite are required.");
        if (p.Repetitions is < 1 or > 20 || p.MaximumCandidates is < 1 or > 10 || p.MaximumChangedComponents < 1
            || p.MaximumTrials is < 1 or > 10_000 || p.MaximumTokensPerTrial <= 0 || p.MaximumTokens < p.MaximumTokensPerTrial
            || p.MaximumDuration <= TimeSpan.Zero || p.MaximumDuration > TimeSpan.FromDays(1)
            || p.TrialTimeout <= TimeSpan.Zero || p.TrialTimeout > p.MaximumDuration
            || !double.IsFinite(p.MinimumScoreGain) || p.MinimumScoreGain is <= 0 or > 1
            || !double.IsFinite(p.NoiseMultiplier) || p.NoiseMultiplier < 1
            || !double.IsFinite(p.MaximumTokenRatio) || p.MaximumTokenRatio < 1
            || !double.IsFinite(p.MaximumDurationRatio) || p.MaximumDurationRatio < 1)
            throw new ArgumentException("Invalid experiment bounds or selection policy.");
        if (plan.Candidates.Count < 1 || plan.Candidates.Count > p.MaximumCandidates
            || plan.Tasks.Count > 1000 || plan.Tasks.Any(t => string.IsNullOrWhiteSpace(t.Id) || !Enum.IsDefined(t.Split))
            || plan.Tasks.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != plan.Tasks.Count
            || Enum.GetValues<HarnessTaskSplit>().Any(split => plan.Tasks.Count(t => t.Split == split) < 2))
            throw new ArgumentException("Candidates must be bounded; use at least two unique tasks in each split.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var revision in new[] { plan.Baseline }.Concat(plan.Candidates))
        {
            if (string.IsNullOrWhiteSpace(revision.Id) || !ids.Add(revision.Id) || !hashes.Add(revision.Fingerprint)
                || string.IsNullOrWhiteSpace(revision.Hypothesis) || revision.Hypothesis.Length > 1000
                || revision.Components.Count > 32 || revision.Components.Any(c => string.IsNullOrWhiteSpace(c.Key) || c.Value is null || c.Value.Length > 16_000))
                throw new ArgumentException("Harness revisions require distinct identities, bounded data and a hypothesis.");
            if (revision == plan.Baseline) continue;
            var changes = plan.Baseline.Components.Keys.Union(revision.Components.Keys, StringComparer.Ordinal)
                .Where(key => plan.Baseline.Components.GetValueOrDefault(key) != revision.Components.GetValueOrDefault(key)).ToArray();
            if (changes.Length == 0 || changes.Length > p.MaximumChangedComponents || changes.Any(key => !p.EditableComponents.Contains(key, StringComparer.Ordinal)))
                throw new ArgumentException("Candidate changes exceed the host's explicit component allowlist or edit budget.");
        }
    }
}
