namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>Fresh provider-reported metadata, not a benchmark or successful tool invocation.</summary>
public sealed record LlmModelCapabilityEvidence(string Provider, string Model, string Transport,
    string Endpoint, string ArtifactDigest, DateTimeOffset ObservedAtUtc, IReadOnlyList<string> DeclaredCapabilities,
    bool? SupportsToolUse, bool? SupportsVision, bool? SupportsStructuredOutput, int? MaxContextTokens, string Source);

public interface ILlmModelCapabilityEvidenceProvider
{
    /// <summary>Opt-in hosts may prohibit synthetic inference when read-only metadata is unavailable.</summary>
    bool RequiresModelCapabilityEvidence => false;
    Task<LlmModelCapabilityEvidence?> GetModelCapabilityEvidenceAsync(string model, CancellationToken ct = default);
}
