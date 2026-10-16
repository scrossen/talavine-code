using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SwarmCore.Agent.Continual;

/// <summary>Versioned, host-neutral physical audit contract. Changing this policy changes the hash.</summary>
public static class ContinualDeliveryAuditProtocol
{
    public const int Version = 1;
    public const int ShapeVersion = 1;
    public const int MaximumAudits = 2;
    public const int MaximumPhysicalCallsPerAudit = 1;
    public const int MaximumAuditedOutputCharacters = 6000;

    private const string CanonicalPolicy =
        "continual-delivery-audit/v1|exact-candidate|strict-original-single-json-object|" +
        "exact-keys:verdict,reason,evidenceReferences,observedReceiptReferences,hostDisposition|" +
        "verdict:delivered,retry,needs_user_help|reason:nonblank-string|references:string-arrays|" +
        "delivered-disposition:completed,waiting_for_input|other-disposition:null|" +
        "repair:only-delivered-valid-disjoint-successful-references-and-reason-with-missing-or-invalid-disposition|" +
        "max-logical-audits:2|max-physical-calls-per-audit:1|max-full-output-chars:6000|" +
        "unknown-sealed|no-action-replay";

    public static string ProtocolSha256 { get; } =
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalPolicy))).ToLowerInvariant();

    /// <summary>
    /// Validate the original provider response, before any router JSON normalization.
    /// The sole intentionally optional field is hostDisposition, so one complete
    /// re-audit can repair its absence. No markdown fences, prose, JSON5, duplicate
    /// properties, missing reason or missing reference arrays are accepted.
    /// </summary>
    public static bool TryParseStrictPhysicalResponse(string? text, out ContinualDeliveryAudit audit)
        => TryParseStrictPhysicalResponse(text, out audit, out _);

    public static bool TryParseStrictPhysicalResponse(string? text, out ContinualDeliveryAudit audit,
        out bool hostDispositionPresent)
    {
        audit = null!;
        hostDispositionPresent = false;
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            string? verdict = null, reason = null, disposition = null;
            List<string>? evidence = null, observed = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!seen.Add(property.Name)) return false;
                switch (property.Name)
                {
                    case "verdict" when property.Value.ValueKind == JsonValueKind.String:
                        verdict = property.Value.GetString();
                        break;
                    case "reason" when property.Value.ValueKind == JsonValueKind.String:
                        reason = property.Value.GetString();
                        break;
                    case "evidenceReferences" when property.Value.ValueKind == JsonValueKind.Array:
                        if (!TryReadStringArray(property.Value, out evidence)) return false;
                        break;
                    case "observedReceiptReferences" when property.Value.ValueKind == JsonValueKind.Array:
                        if (!TryReadStringArray(property.Value, out observed)) return false;
                        break;
                    case "hostDisposition" when property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Null:
                        disposition = property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.GetString();
                        break;
                    default:
                        return false;
                }
            }
            if (string.IsNullOrWhiteSpace(verdict) || string.IsNullOrWhiteSpace(reason)
                || evidence is null || observed is null) return false;
            audit = new ContinualDeliveryAudit
            {
                Verdict = verdict,
                Reason = reason,
                EvidenceReferences = evidence,
                ObservedReceiptReferences = observed,
            };
            hostDispositionPresent = seen.Contains("hostDisposition");
            audit.HostDisposition = disposition;
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool TryReadStringArray(JsonElement element, out List<string> values)
    {
        values = [];
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                return false;
            values.Add(item.GetString()!);
        }
        return true;
    }
}
