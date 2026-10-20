using SwarmCore.Abstractions.LlmRouting;
using System.Text.Json;

namespace SwarmCore.LlmRouting.Providers;

public partial class OllamaLlmProvider : ILlmModelCapabilityEvidenceProvider
{
    /// <summary>Only tags/show reads. No pull, lifecycle, warmup, inference or model-name heuristic.</summary>
    public async Task<LlmModelCapabilityEvidence?> GetModelCapabilityEvidenceAsync(string model, CancellationToken ct = default)
    {
        var node = SelectToolUseNode();
        var before = await GetInstalledArtifactDigestAsync(node, model, ct, forceRefresh: true).ConfigureAwait(false);
        if (!before.Success || before.Digest is null) return null;
        var metadata = await TryGetModelMetadataAsync(node, model, ct, RequestMetadataTimeout).ConfigureAwait(false);
        var after = await GetInstalledArtifactDigestAsync(node, model, ct, forceRefresh: true).ConfigureAwait(false);
        if (!after.Success || before.Digest != after.Digest)
            throw new InvalidOperationException("Model artifact changed while reading capability metadata.");
        if (metadata?.Capabilities is not { } capabilities) return null; // Missing is unknown, never a guessed yes.
        if (capabilities.Count > 32 || capabilities.Any(c => string.IsNullOrWhiteSpace(c) || c.Length > 64))
            return null;
        var declared = capabilities.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        // A component repair can change this exact tag while the provider remains alive.
        // Fresh, artifact-bound metadata replaces the discovery-time vision verdict.
        _visionCapabilityCache[model.Trim()] = declared.Contains("vision", StringComparer.Ordinal);
        int? maximumContext = null;
        if (metadata.ModelInfo is { ValueKind: JsonValueKind.Object } info)
            foreach (var item in info.EnumerateObject())
                if (item.Name.EndsWith(".context_length", StringComparison.Ordinal) && item.Value.ValueKind == JsonValueKind.Number
                    && item.Value.TryGetInt32(out var size) && size > 0)
                    maximumContext = maximumContext is null ? size : Math.Min(maximumContext.Value, size);
        return new(ProviderName, model, "ollama-native-v1", node.Endpoint, before.Digest, DateTimeOffset.UtcNow,
            declared, declared.Contains("tools", StringComparer.Ordinal), declared.Contains("vision", StringComparer.Ordinal),
            null, maximumContext, "provider-reported:ollama/api/show;artifact=" + before.Digest);
    }
}
