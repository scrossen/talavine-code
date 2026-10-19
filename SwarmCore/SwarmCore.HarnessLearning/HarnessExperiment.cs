using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SwarmCore.HarnessLearning;

/// <summary>Host-owned partition and exact evaluation identity. Never put credentials or source text here.</summary>
public sealed record HarnessExperimentScope(string Partition, string Workload, string EnvironmentHash, string SuiteHash);

public enum HarnessTaskSplit { Development, HeldOut }
public sealed record HarnessExperimentTask(string Id, HarnessTaskSplit Split);

/// <summary>Data-only harness revision. The host interprets only explicitly allowed components.</summary>
public sealed record HarnessRevision(string Id, string Hypothesis, IReadOnlyDictionary<string, string> Components)
{
    public string Fingerprint => HarnessEvidence.Hash(JsonSerializer.Serialize(
        Components.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray()));
}

public sealed record HarnessExperimentPolicy
{
    public int Repetitions { get; init; } = 2;
    public int MaximumCandidates { get; init; } = 2;
    public int MaximumChangedComponents { get; init; } = 1;
    public int MaximumTrials { get; init; } = 100;
    public long MaximumTokens { get; init; } = 500_000;
    public long MaximumTokensPerTrial { get; init; } = 50_000;
    public TimeSpan MaximumDuration { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan TrialTimeout { get; init; } = TimeSpan.FromMinutes(3);
    public double MinimumScoreGain { get; init; } = .02;
    public double NoiseMultiplier { get; init; } = 2;
    public double MaximumTokenRatio { get; init; } = 1.25;
    public double MaximumDurationRatio { get; init; } = 1.5;
    public IReadOnlyList<string> EditableComponents { get; init; } = [];
}

public sealed record HarnessExperimentPlan(HarnessExperimentScope Scope, HarnessRevision Baseline,
    IReadOnlyList<HarnessRevision> Candidates, IReadOnlyList<HarnessExperimentTask> Tasks, HarnessExperimentPolicy Policy);

/// <summary>Each invocation must use fresh isolated state. A budget is a ceiling, never additional authority.</summary>
public sealed record HarnessTrialRequest(HarnessExperimentScope Scope, HarnessRevision Revision,
    HarnessExperimentTask Task, int Repetition, long TokenLimit);

/// <summary>
/// Independent host judgement, not model self-assessment. Score is [0,1]; a failed task may have a valid
/// verification receipt with score zero. Unknown usage is null. EvidenceHash identifies the host receipt.
/// </summary>
public sealed record HarnessTrialObservation(double Score, bool InvariantsPassed, string VerifierId,
    string EvidenceHash, long? Tokens, int ToolCalls = 0);

public interface IHarnessExperimentDomain
{
    /// <summary>
    /// Enforce the supplied token ceiling at physical dispatch, current host authority and cancellation;
    /// return only after owned execution has stopped. Evaluators and held-out answers must remain outside
    /// the executor's editable workspace. Never probe live sends, payments or uncertain external actions.
    /// </summary>
    Task<HarnessTrialObservation> RunAsync(HarnessTrialRequest request, CancellationToken ct);
}

public sealed record HarnessTrial(string RevisionHash, string TaskId, HarnessTaskSplit Split, int Repetition,
    double Score, bool InvariantsPassed, string? VerifierId, string? EvidenceHash, long? Tokens,
    int ToolCalls, double DurationMilliseconds, string? FailureCode);

public sealed record HarnessComparison(string RevisionId, string RevisionHash, string Hypothesis,
    bool Accepted, string Reason, double DevelopmentGain, double HeldOutGain,
    double DevelopmentNoiseFloor, double HeldOutNoiseFloor);

/// <summary>A recommendation with immutable evidence; this is never production activation or a permission grant.</summary>
public sealed record HarnessExperimentReport(Guid Id, HarnessExperimentScope Scope, DateTimeOffset StartedAt,
    HarnessRevision Baseline, IReadOnlyList<HarnessRevision> Candidates, HarnessExperimentPolicy Policy,
    IReadOnlyList<HarnessTrial> Trials, IReadOnlyList<HarnessComparison> Comparisons,
    string? RecommendedRevisionId, string? StopReason);

/// <summary>Host-supplied, partition-scoped, append-only evidence. No mutable active head or implicit replay.</summary>
public interface IHarnessExperimentHistory
{
    Task<IReadOnlyList<HarnessExperimentReport>> ReadAsync(HarnessExperimentScope scope, CancellationToken ct);
    Task AppendAsync(HarnessExperimentReport report, CancellationToken ct);
}

public static class HarnessEvidence
{
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static bool IsHash(string? value) => value is { Length: 64 }
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
