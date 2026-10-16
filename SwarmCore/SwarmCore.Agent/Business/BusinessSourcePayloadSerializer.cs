using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SwarmCore.Abstractions.AgentWork;

namespace SwarmCore.Agent.Business;

public sealed record BusinessSourceEvidence(string SourceId, string SourceType, string? SourceUri,
    DateTimeOffset ObservedAtUtc, JsonElement Evidence);

/// <summary>Immutable source serialization. This is content identity, never authentication.</summary>
public sealed class BusinessSourcePayload
{
    internal BusinessSourcePayload(BusinessSourceEvidence source, string payload, string sourceJson,
        OperatorWorkContract? work = null)
    {
        Source = source; PayloadJson = payload; SourceJson = sourceJson;
        PayloadSha256 = Hash(payload); SourceSha256 = Hash(sourceJson);
        Work = work;
    }
    public BusinessSourceEvidence Source { get; }
    public string PayloadJson { get; }
    public string SourceJson { get; }
    public string PayloadSha256 { get; }
    public string SourceSha256 { get; }
    public OperatorWorkContract? Work { get; }
    public string? WorkSha256 => Work?.Sha256;
    // Return fresh bytes: a consumer cannot alter retained content or its fingerprint.
    public ReadOnlyMemory<byte> PayloadBytes => Encoding.UTF8.GetBytes(PayloadJson);
    public ReadOnlyMemory<byte> SourceBytes => Encoding.UTF8.GetBytes(SourceJson);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public static class BusinessSourcePayloadSerializer
{
    public const string SourceContentAuthority = "untrusted-evidence-only";
    public const string WorkAuthority = "host-bound-operator-work-v2";
    public static BusinessSourcePayload Create(string operationId, string operatorId, BusinessSourceEvidence source,
        DateTimeOffset observedCutoffUtc)
    {
        ArgumentNullException.ThrowIfNull(source);
        var frozen = source with { Evidence = source.Evidence.Clone() };
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 150
            || operationId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ':'))
            || string.IsNullOrWhiteSpace(operatorId) || operatorId.Length > 450 || operatorId.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(frozen.SourceId) || frozen.SourceId.Length > 200
            || string.IsNullOrWhiteSpace(frozen.SourceType) || frozen.SourceType.Length > 100
            || frozen.SourceUri?.Length > 2000 || frozen.ObservedAtUtc > observedCutoffUtc
            || frozen.Evidence.ValueKind != JsonValueKind.Object || frozen.Evidence.GetRawText().Length > 16000)
            throw new ArgumentException("Supply bounded stable source identity, operator identity and one already-observed evidence object.");
        // Preserve the existing default serializer's casing, field order, escaping
        // and DateTimeOffset representation exactly. No normalization of evidence.
        var sourceObject = new { frozen.SourceId, frozen.SourceType, frozen.SourceUri, frozen.ObservedAtUtc, frozen.Evidence };
        var sourceJson = JsonSerializer.Serialize(sourceObject);
        var payload = JsonSerializer.Serialize(new { operationId, operatorId, source = sourceObject,
            sourceContentAuthority = SourceContentAuthority });
        return new(frozen, payload, sourceJson);
    }

    /// <summary>V2 binds frozen work to the operation payload without rewriting legacy source identity.</summary>
    public static BusinessSourcePayload CreateV2(string operationId, string operatorId, BusinessSourceEvidence source,
        DateTimeOffset observedCutoffUtc, OperatorWorkContract work)
    {
        ArgumentNullException.ThrowIfNull(work);
        var legacy = Create(operationId, operatorId, source, observedCutoffUtc);
        if (!string.Equals(work.SourceId, legacy.Source.SourceId, StringComparison.Ordinal))
            throw new ArgumentException("Operator work source does not match the observed source.", nameof(work));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("operationId", operationId);
            writer.WriteString("operatorId", operatorId);
            writer.WritePropertyName("source");
            writer.WriteRawValue(legacy.SourceJson);
            writer.WriteString("sourceContentAuthority", SourceContentAuthority);
            writer.WriteString("workAuthority", WorkAuthority);
            writer.WritePropertyName("work");
            writer.WriteRawValue(work.CanonicalJson);
            writer.WriteString("workSha256", work.Sha256);
            writer.WriteEndObject();
        }
        var payload = Encoding.UTF8.GetString(stream.ToArray());
        return new(legacy.Source, payload, legacy.SourceJson, work);
    }

    /// <summary>
    /// Read retained V2 work against host-owned operation, operator and source identity.
    /// The host must separately bind tenant, employee, trigger, current grants and operation CAS.
    /// </summary>
    public static OperatorWorkContract ReadV2Work(string payloadJson, string expectedOperationId,
        string expectedOperatorId, string expectedSourceSha256)
    {
        if (string.IsNullOrWhiteSpace(payloadJson) || Encoding.UTF8.GetByteCount(payloadJson) > 40000)
            throw new ArgumentException("V2 payload is empty or oversized.", nameof(payloadJson));
        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        RequireFields(root, "operationId", "operatorId", "source", "sourceContentAuthority",
            "workAuthority", "work", "workSha256");
        if (root.GetProperty("operationId").GetString() != expectedOperationId
            || root.GetProperty("operatorId").GetString() != expectedOperatorId
            || root.GetProperty("sourceContentAuthority").GetString() != SourceContentAuthority
            || root.GetProperty("workAuthority").GetString() != WorkAuthority)
            throw new ArgumentException("V2 payload host identity or authority marker differs.", nameof(payloadJson));
        var source = root.GetProperty("source");
        RequireFields(source, "SourceId", "SourceType", "SourceUri", "ObservedAtUtc", "Evidence");
        var sourceJson = source.GetRawText();
        var sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceJson)));
        if (!string.Equals(sourceHash, expectedSourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("V2 payload source identity differs.", nameof(payloadJson));
        var rawWork = root.GetProperty("work").GetRawText();
        var work = OperatorWorkContract.Parse(rawWork);
        if (rawWork != work.CanonicalJson || root.GetProperty("workSha256").GetString() != work.Sha256
            || work.SourceId != source.GetProperty("SourceId").GetString())
            throw new ArgumentException("V2 payload work identity differs.", nameof(payloadJson));
        return work;
    }

    private static void RequireFields(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("V2 payload element must be an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
            if (!names.Contains(field.Name, StringComparer.Ordinal) || !seen.Add(field.Name))
                throw new ArgumentException("V2 payload has an unknown or duplicate field.");
        if (seen.Count != names.Length)
            throw new ArgumentException("V2 payload is missing a required field.");
    }
}
