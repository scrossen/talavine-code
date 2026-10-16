using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SwarmCore.Coding.Routing;

public enum ModelResponseOutcome { Declined, PartiallyDeclined, AnsweredUnverified, Verified, AcceptanceFailed, RuntimeFailed }

/// <summary>Immutable host observation. No prompt, answer, credential, or executable action belongs here.</summary>
public sealed record ModelResponseObservation(string Id, string ScopeId, string Provider, string Model,
    string TaskFingerprint, ModelResponseOutcome Outcome, DateTimeOffset ObservedUtc, string EvidenceKind)
{
    public int SchemaVersion { get; init; } = 1;
    /// <summary>A reviewed correction replaces an observation in ranking while retaining its original receipt.</summary>
    public string? SupersedesId { get; init; }
}

public interface IModelResponseMemory
{
    Task AppendAsync(ModelResponseObservation observation, CancellationToken ct = default);
    Task<IReadOnlyList<ModelResponseObservation>> ReadAsync(string scopeId, CancellationToken ct = default);
}

public sealed record CodingFallbackContext(string ScopeId, string TaskFingerprint, bool UseMemory);
public sealed record CodingFallbackDiscoveryResult(IReadOnlyList<CodingLane> Candidates, string? HistoryWarning = null);

public sealed record ModelResponseSummary(string Provider, string Model, int Answered, int Verified,
    int Declined, int PartiallyDeclined, int AcceptanceFailed, int RuntimeFailed, DateTimeOffset LastObservedUtc);

/// <summary>Recent, exact-request evidence dominates weak cross-request history. This is willingness evidence,
/// never permission, benchmark clearance, or verified task completion.</summary>
public static class ModelResponseLearning
{
    public static IReadOnlyList<ModelResponseObservation> Current(IReadOnlyList<ModelResponseObservation> history)
    {
        var replaced = history.Where(r => r.SupersedesId is not null)
            .Select(r => (r.ScopeId, r.SupersedesId)).ToHashSet();
        return history.Where(r => !replaced.Contains((r.ScopeId, r.Id))).ToArray();
    }

    public static string Fingerprint(string protectedUserMessage)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Regex.Replace(protectedUserMessage.Trim(), @"\s+", " ").ToLowerInvariant()))).ToLowerInvariant();

    public static bool IsExactModel(string model)
        => !string.IsNullOrWhiteSpace(model) && !model.StartsWith("configured:", StringComparison.OrdinalIgnoreCase)
            && !model.StartsWith('~') && !model.EndsWith(":default", StringComparison.OrdinalIgnoreCase)
            && !model.Equals("openrouter/auto", StringComparison.OrdinalIgnoreCase);

    public static double Score(string provider, string model, string? taskFingerprint,
        IReadOnlyList<ModelResponseObservation> history, DateTimeOffset now)
    {
        double exactDelta = 0, exactWeight = 0, generalDelta = 0, generalWeight = 0;
        foreach (var row in Current(history).Where(r => r.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase)
            && r.Model.Equals(model, StringComparison.OrdinalIgnoreCase)))
        {
            var age = (now - row.ObservedUtc).TotalDays;
            if (age < 0 || age > 90 || row.Outcome == ModelResponseOutcome.RuntimeFailed) continue;
            var weight = Math.Pow(.5, age / 30)
                * (row.EvidenceKind.StartsWith("host-classifier", StringComparison.Ordinal) ? .5 : 1);
            var delta = row.Outcome switch
            {
                ModelResponseOutcome.Declined => -1d,
                ModelResponseOutcome.PartiallyDeclined => -.5d,
                ModelResponseOutcome.Verified => 1d,
                ModelResponseOutcome.AnsweredUnverified => .25d,
                ModelResponseOutcome.AcceptanceFailed => -.5d,
                _ => 0d,
            };
            generalDelta += weight * delta; generalWeight += weight;
            if (row.TaskFingerprint == taskFingerprint)
            { exactDelta += weight * delta; exactWeight += weight; }
        }
        // Shrink sparse evidence toward unknown (zero). A single answer cannot declare a model "flexible".
        return .8 * exactDelta / (exactWeight + 3) + .2 * generalDelta / (generalWeight + 10);
    }

    public static IReadOnlyList<ModelResponseSummary> Summarize(IReadOnlyList<ModelResponseObservation> history)
        => Current(history).GroupBy(r => (r.Provider, r.Model)).Select(g => new ModelResponseSummary(g.Key.Provider, g.Key.Model,
            g.Count(r => r.Outcome == ModelResponseOutcome.AnsweredUnverified),
            g.Count(r => r.Outcome == ModelResponseOutcome.Verified), g.Count(r => r.Outcome == ModelResponseOutcome.Declined),
            g.Count(r => r.Outcome == ModelResponseOutcome.PartiallyDeclined),
            g.Count(r => r.Outcome == ModelResponseOutcome.AcceptanceFailed), g.Count(r => r.Outcome == ModelResponseOutcome.RuntimeFailed),
            g.Max(r => r.ObservedUtc))).OrderByDescending(r => r.LastObservedUtc).ToArray();
}
