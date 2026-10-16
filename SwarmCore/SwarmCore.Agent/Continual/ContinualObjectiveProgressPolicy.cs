using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SwarmCore.Agent.Continual;

/// <summary>Completed tool evidence, excluding invocation identity and timing.</summary>
public sealed record ContinualProgressEvidence(
    string ToolName, string? InputJson, string? Result, bool IsError, string SideEffect);

/// <summary>
/// Detects unchanged evidence without spending another model call. Rewording an
/// answer, replaying a receipt, or changing JSON formatting is not progress.
/// This policy affects retry timing only, never authority or completion.
/// </summary>
public static class ContinualObjectiveProgressPolicy
{
    // Only top-level observation metadata is excluded. Nested event timestamps,
    // record dates, versions and resource/mission IDs remain material evidence.
    private static readonly HashSet<string> ObservationMetadata = new(StringComparer.OrdinalIgnoreCase)
    {
        "checkedAtUtc", "observedAtUtc", "retrievedAtUtc", "generatedAtUtc",
        "elapsedMs", "durationMs",
    };

    public static string Fingerprint(IEnumerable<ContinualProgressEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var entries = evidence.Select(item => JsonSerializer.Serialize(new[]
            {
                item.ToolName,
                Canonicalize(item.InputJson, ignoreObservationMetadata: false),
                Canonicalize(item.Result, ignoreObservationMetadata: true),
                item.IsError ? "error" : "ok",
                item.SideEffect,
            }))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join("\n", entries)))).ToLowerInvariant();
    }

    public static int NextNoProgressCount(
        int current, string? priorOutputFingerprint, string outputFingerprint,
        string? priorEvidenceFingerprint, string evidenceFingerprint)
    {
        // A changed concrete receipt can establish progress even when the model
        // uses the same final wording. Older checkpoints acquire the evidence
        // baseline once; exact repeated output still backs off during migration.
        var unchanged = priorEvidenceFingerprint is not null
            ? string.Equals(priorEvidenceFingerprint, evidenceFingerprint, StringComparison.Ordinal)
            : priorOutputFingerprint is not null
              && string.Equals(priorOutputFingerprint, outputFingerprint, StringComparison.Ordinal);
        return unchanged ? current == int.MaxValue ? int.MaxValue : Math.Max(0, current) + 1 : 0;
    }

    public static TimeSpan RetryBackoff(int noProgress, bool unattendedRepair = false)
    {
        var repairStalled = unattendedRepair && noProgress > 0;
        var baseSeconds = repairStalled ? 60 : 2;
        var maximumSeconds = repairStalled ? 3600 : 300;
        return TimeSpan.FromSeconds(Math.Min(maximumSeconds,
            baseSeconds * Math.Pow(2, Math.Clamp(noProgress, 0, 30))));
    }

    private static string Canonicalize(string? value, bool ignoreObservationMetadata)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        try
        {
            using var document = JsonDocument.Parse(value);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
                WriteCanonical(writer, document.RootElement, ignoreObservationMetadata);
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return value.ReplaceLineEndings("\n").Trim();
        }
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element, bool ignoreObservationMetadata)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (ignoreObservationMetadata && ObservationMetadata.Contains(property.Name)) continue;
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value, ignoreObservationMetadata: false);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonical(writer, item, ignoreObservationMetadata: false);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
