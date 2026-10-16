using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting.Services;

/// <summary>Projects fresh exact-route declarations while preserving explicit negative evidence.</summary>
public static class LlmModelCapabilityEvidenceResolver
{
    public static async Task<LlmModelCapabilityEvidence?> ResolveAsync(ILlmProvider provider, string model,
        CancellationToken ct = default)
    {
        if (provider is not ILlmModelCapabilityEvidenceProvider source) return null;
        var evidence = await source.GetModelCapabilityEvidenceAsync(model, ct).ConfigureAwait(false);
        if (evidence is null) return null;
        if (evidence.Provider != provider.ProviderName || evidence.Model != model
            || string.IsNullOrWhiteSpace(evidence.Transport) || string.IsNullOrWhiteSpace(evidence.Source)
            || !Uri.TryCreate(evidence.Endpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https") || endpoint.UserInfo.Length != 0
            || evidence.ArtifactDigest.Length != 64 || evidence.ArtifactDigest.Any(c => !Uri.IsHexDigit(c))
            || evidence.ObservedAtUtc > DateTimeOffset.UtcNow.AddMinutes(1)
            || evidence.ObservedAtUtc < DateTimeOffset.UtcNow.AddMinutes(-1))
            throw new InvalidOperationException("Fresh capability metadata does not bind the exact provider/model/artifact endpoint.");
        return evidence with { DeclaredCapabilities = evidence.DeclaredCapabilities.ToArray() };
    }

    public static void Apply(LlmModelSelection route, LlmModelCapabilityEvidence evidence)
    {
        // Any negative wins; a provider's API declaration cannot erase a measured/catalog refusal.
        route.SupportsToolUse = Merge(route.SupportsToolUse, evidence.SupportsToolUse);
        route.SupportsStructuredOutput = Merge(route.SupportsStructuredOutput, evidence.SupportsStructuredOutput);
        if (evidence.MaxContextTokens is > 0)
            route.MaxContextTokens = route.MaxContextTokens is > 0 ? Math.Min(route.MaxContextTokens.Value, evidence.MaxContextTokens.Value) : evidence.MaxContextTokens;
        if (route.CapabilityEvidenceSource is null) route.CapabilityEvidenceSource = evidence.Source;
        else if (!route.CapabilityEvidenceSource.Contains(evidence.Source, StringComparison.Ordinal))
            route.CapabilityEvidenceSource += ";fresh-declaration:" + evidence.Source;
        // This is a declaration only. Do not manufacture a measured score or benchmark provenance.
    }
    private static bool? Merge(bool? current, bool? declared)
        => current == false || declared == false ? false : current == true || declared == true ? true : null;
}
