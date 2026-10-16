using System.Security.Cryptography;
using System.Text;

namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// Identity of a single calibration result. The staleness contract is encoded entirely
/// by what's in the key — assembly version is NOT a component, so daily dev rebuilds do
/// not invalidate calibration. Invalidation triggers are:
///
///   - The prompt (or tool schema) content actually changes → <see cref="PromptHash"/> changes
///   - A new provider/model is being tested → different <see cref="ProviderId"/> / <see cref="ModelId"/>
///   - The local hardware changed (matters for local models) → different <see cref="HardwareKey"/>
///   - A mutable local artifact or runtime changed → different <see cref="ModelDigest"/>,
///     <see cref="RuntimeVersion"/>, or <see cref="Quantization"/>
///   - The user clicks "retest" → caller forces a re-run regardless of cache state
///
/// Used by tool-use validation, message-pipeline calibration, and any future
/// high-value-function calibration so all of them share a single staleness model.
/// </summary>
/// <param name="LaneKey">Calibration lane, e.g. "message-pipeline", "tool-use", "json-contract:agent-chat".</param>
/// <param name="PromptHash">SHA-256 over the prompt or tool schema content under test.</param>
/// <param name="ProviderId">Provider key in registry form ("OpenAI", "Claude", "Ollama").</param>
/// <param name="ModelId">Provider-native model id (e.g. "claude-sonnet-4-6").</param>
/// <param name="HardwareKey">
/// Coarse hardware fingerprint for local providers (e.g. "5090-32gb-vram"). Cloud providers
/// can supply <c>"cloud"</c> — the key still has to exist so a single column type works.
/// </param>
public sealed record RoutingCalibrationKey(
    string LaneKey,
    string PromptHash,
    string ProviderId,
    string ModelId,
    string HardwareKey,
    string? ModelDigest = null,
    string? RuntimeVersion = null,
    string? Quantization = null)
{
    /// <summary>Compute a SHA-256 hex hash over a string. Use for <see cref="PromptHash"/>.</summary>
    public static string HashOf(string content)
    {
        if (string.IsNullOrEmpty(content)) return new string('0', 64);
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public string ToStableString()
    {
        var baseline = $"{LaneKey}|{PromptHash}|{ProviderId}|{ModelId}|{HardwareKey}";
        if (string.IsNullOrWhiteSpace(ModelDigest)
            && string.IsNullOrWhiteSpace(RuntimeVersion)
            && string.IsNullOrWhiteSpace(Quantization))
        {
            return baseline;
        }

        return string.Join('|',
            baseline,
            EncodeIdentityPart(ModelDigest),
            EncodeIdentityPart(RuntimeVersion),
            EncodeIdentityPart(Quantization));
    }

    private static string EncodeIdentityPart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value.Trim()))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
