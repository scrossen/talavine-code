using System.Text.Json;
using SwarmCore.LlmRouting.Models;

namespace SwarmCore.LlmRouting.Services;

/// <summary>Retention bounds diagnostic history, never the number of authorized work attempts.</summary>
public sealed record AdaptiveExecutionRetentionOptions
{
    public int MaximumTerminalDecisions { get; init; } = 256;
    public int MaximumUsageReferences { get; init; } = 512;
    public int MaximumPayloadCharacters { get; init; } = 2_000_000;
}

/// <summary>Portable append-only and identity rules; database/protection adapters supply the writer fence.</summary>
public static class AdaptiveExecutionPersistencePolicy
{
    public static bool TryPrepare(AdaptiveExecutionSnapshot proposed, AdaptiveExecutionSnapshot? previous,
        AdaptiveExecutionRetentionOptions limits, out AdaptiveExecutionSnapshot prepared, out string? reason)
    {
        prepared = proposed;
        reason = null;
        if (limits.MaximumTerminalDecisions < 1 || limits.MaximumUsageReferences < 1 || limits.MaximumPayloadCharacters < 1024)
            throw new ArgumentException("Invalid adaptive execution retention settings.");
        if (!Valid(proposed)) { reason = "invalid-adaptive-state"; return false; }
        if (previous is not null && (previous.Identity != proposed.Identity
            || previous.Generation == long.MaxValue || proposed.Generation != previous.Generation + 1
            || proposed.PolicyState.LastSequence < previous.PolicyState.LastSequence
            || proposed.PolicyState.LastAppliedSequence < previous.PolicyState.LastAppliedSequence
            || proposed.PolicyState.AppliedEscalations < previous.PolicyState.AppliedEscalations))
        { reason = "adaptive-state-regression"; return false; }

        var records = proposed.Decisions.ToDictionary(r => r.Id);
        var usages = new Dictionary<string, AdaptiveUsageReference>(StringComparer.Ordinal);
        foreach (var usage in proposed.Usage)
        {
            if (usages.TryGetValue(usage.Reference, out var old) && old != usage)
            { reason = "conflicting-physical-usage-reference"; return false; }
            usages[usage.Reference] = usage;
        }
        if (previous is not null)
        {
            var previousIds = previous.Decisions.Select(r => r.Id).ToHashSet();
            var retainedOrder = proposed.Decisions.Where(r => previousIds.Contains(r.Id)).Select(r => r.Id);
            if (!retainedOrder.SequenceEqual(previous.Decisions.Select(r => r.Id))
                || previous.Decisions.Any(r => !records.TryGetValue(r.Id, out var next) || !Equal(r, next))
                || previous.Usage.Any(u => !usages.TryGetValue(u.Reference, out var next) || u != next))
            { reason = "adaptive-history-is-append-only"; return false; }
        }

        var groups = proposed.Decisions.GroupBy(Key).ToArray();
        foreach (var group in groups)
        {
            if (group.Count(r => r.Outcome == AdaptiveDecisionOutcome.Proposed) > 1
                || group.Count(r => r.Outcome != AdaptiveDecisionOutcome.Proposed) > 1
                || group.Any(r => r.Decision != group.First().Decision))
            { reason = "conflicting-adaptive-decision-outcomes"; return false; }
        }
        var retainedKeys = groups.Where(g => g.All(r => r.Outcome == AdaptiveDecisionOutcome.Proposed)).Select(g => g.Key)
            .Concat(groups.Where(g => g.Any(r => r.Outcome != AdaptiveDecisionOutcome.Proposed))
                .OrderByDescending(g => g.Max(r => r.AtUtc)).ThenByDescending(g => g.Max(r => r.Observation.Sequence))
                .Take(limits.MaximumTerminalDecisions).Select(g => g.Key)).ToHashSet();
        var retained = proposed.Decisions.Where(r => retainedKeys.Contains(Key(r))).ToArray();
        var neededUsage = retained.SelectMany(r => r.UsageReferences).ToHashSet(StringComparer.Ordinal);
        var latestUsage = usages.Values.TakeLast(limits.MaximumUsageReferences).Select(u => u.Reference).ToHashSet(StringComparer.Ordinal);
        prepared = proposed with { Decisions = retained,
            Usage = usages.Values.Where(u => latestUsage.Contains(u.Reference) || neededUsage.Contains(u.Reference)).ToArray() };
        if (JsonSerializer.Serialize(prepared).Length > limits.MaximumPayloadCharacters)
        { reason = "adaptive-diagnostic-payload-limit"; return false; }
        return true;
    }

    public static bool Valid(AdaptiveExecutionSnapshot value)
    {
        if (value.SchemaVersion != 1 || value.Generation < 1 || value.Identity is null || value.Identity.ProfileId == Guid.Empty
            || string.IsNullOrWhiteSpace(value.Identity.ConversationId) || value.Authority is null
            || string.IsNullOrWhiteSpace(value.Authority.Digest) || string.IsNullOrWhiteSpace(value.Authority.FunctionType)
            || value.PolicyState is not { } state || state.WorkId != value.Identity.WorkId
            || state.LastSequence < -1 || state.LastAppliedSequence < -1 || state.LastAppliedSequence > state.LastSequence
            || state.AppliedEscalations < 0 || state.ConsecutiveFailures < 0 || state.ConsecutiveSuccesses < 0 || state.ConsecutiveRetryFailures < 0
            || value.Decisions is null || value.Usage is null || value.Decisions.Any(r => r is null)
            || value.Decisions.Select(r => r.Id).Distinct().Count() != value.Decisions.Count) return false;
        foreach (var record in value.Decisions)
            if (record.Id == Guid.Empty || record.Decision is not { } decision || record.Observation is not { } observation
                || decision.WorkId != state.WorkId || observation.WorkId != state.WorkId
                || decision.ObservationSequence != observation.Sequence || decision.EvidenceId != observation.EvidenceId
                || observation.Sequence < 0 || observation.Sequence > state.LastSequence
                || string.IsNullOrWhiteSpace(observation.EvidenceId) || string.IsNullOrWhiteSpace(record.AuthorityDigest)
                || !Enum.IsDefined(record.Outcome) || !Enum.IsDefined(decision.Action)
                || record.UsageReferences is null || record.EstimatedCost < 0) return false;
        return value.Usage.All(u => u is not null && !string.IsNullOrWhiteSpace(u.Reference)
            && !string.IsNullOrWhiteSpace(u.Provider) && !string.IsNullOrWhiteSpace(u.Model)
            && u.InputTokens is not < 0 && u.OutputTokens is not < 0 && u.Cost is not < 0
            && (u.LatencyMs is null || double.IsFinite(u.LatencyMs.Value) && u.LatencyMs >= 0));
    }

    private static (string Work, long Sequence, string Evidence) Key(AdaptiveDecisionRecord value)
        => (value.Decision.WorkId, value.Decision.ObservationSequence, value.Decision.EvidenceId);
    private static bool Equal(AdaptiveDecisionRecord first, AdaptiveDecisionRecord second)
        => JsonSerializer.Serialize(first) == JsonSerializer.Serialize(second);
}
