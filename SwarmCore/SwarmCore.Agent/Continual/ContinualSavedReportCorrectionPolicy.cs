using System.Text.Json;

namespace SwarmCore.Agent.Continual;

/// <summary>A registered host adapter supplies kinds and canonical identities, never an auditor's prose.</summary>
public sealed record ContinualReportReference(string Kind, string RecordId, string SourceReceiptId);
public sealed record ContinualReportCorrectionResponse(string Report, IReadOnlyList<ContinualReportReference> References);

/// <summary>Host-neutral result-only protocol. Persistence, current authority and physical inference remain host owned.</summary>
public static class ContinualSavedReportCorrectionPolicy
{
    public const int MaximumReferences = 32;
    public const int MaximumReportCharacters = ContinualDeliveryAuditProtocol.MaximumAuditedOutputCharacters;
    public const int MaximumPhysicalCallsPerOriginalWork = 1;
    public const string Provenance = "saved-report-correction";

    public static bool IsPreAuditParent(ContinualObjectiveSnapshot snapshot)
        => snapshot.Status == ContinualObjectiveStatus.Uncertain && snapshot.NextAttemptAtUtc is null
            && snapshot.LeaseToken is null && snapshot.VerificationCandidatePending
            && snapshot.VerificationCandidate is { Provenance: "execution-checkpoint" } candidate
            && candidate.ExecutionAttempt == snapshot.AttemptCount
            && ContinualVerificationCandidatePolicy.Matches(snapshot, snapshot.LastOutput ?? "");

    // Only report shape/reference gaps qualify; the host must separately prove all effects.
    public static string? StrictReportGap(string output, IReadOnlyList<ContinualReportReference> required,
        IReadOnlyCollection<string> kinds)
        => !TryParseStrictResponse(output, out var parsed) ? "invalid-strict-report-format"
            : !ValidateReferences(parsed.References, required, kinds) ? "typed-canonical-reference-mismatch" : null;

    public static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    // Output/candidate/version/whitespace must not create another allowance for the same original work.
    public static string ReservationIdentity(string surface, string partitionKey, Guid originalWorkId)
    {
        if (string.IsNullOrWhiteSpace(surface) || string.IsNullOrWhiteSpace(partitionKey) || originalWorkId == Guid.Empty)
            throw new ArgumentException("A scoped original work identity is required.");
        return ContinualVerificationCandidatePolicy.Hash(JsonSerializer.Serialize(new { surface, partitionKey, originalWorkId }));
    }

    public static bool ValidateReferences(IReadOnlyList<ContinualReportReference>? returned,
        IReadOnlyList<ContinualReportReference> required, IReadOnlyCollection<string> registeredKinds)
    {
        if (returned is null || required.Count is < 1 or > MaximumReferences || returned.Count != required.Count
            || registeredKinds.Count == 0 || !Valid(required) || !Valid(returned)) return false;
        return returned.ToHashSet().SetEquals(required);

        bool Valid(IReadOnlyList<ContinualReportReference> references) => references.Distinct().Count() == references.Count
            && references.All(r => r is not null && registeredKinds.Contains(r.Kind, StringComparer.Ordinal)
                && Bounded(r.Kind, 64) && Bounded(r.RecordId, 256) && Bounded(r.SourceReceiptId, 256));
    }

    public static string ReferencesHash(IReadOnlyList<ContinualReportReference> references)
        => ContinualVerificationCandidatePolicy.Hash(JsonSerializer.Serialize(references
            .OrderBy(r => r.Kind, StringComparer.Ordinal).ThenBy(r => r.RecordId, StringComparer.Ordinal)
            .ThenBy(r => r.SourceReceiptId, StringComparer.Ordinal).ToArray()));

    public static bool ValidateResult(ContinualReportCorrectionResponse? response, string originalOutput,
        IReadOnlyList<ContinualReportReference> required, IReadOnlyCollection<string> registeredKinds)
        => response is not null && Bounded(response.Report, MaximumReportCharacters)
            && !string.Equals(response.Report.Trim(), originalOutput.Trim(), StringComparison.Ordinal)
            && ValidateReferences(response.References, required, registeredKinds);

    /// <summary>Parse the original physical response without normalizing invalid reference roles, duplicates or extra fields.</summary>
    /// <summary>
    /// A host may unwrap a final answer that is exactly one Markdown code fence around one JSON
    /// object before saving it as the registered report. Anything else (text around the fence,
    /// several fences, a non-object body) is left unchanged so the strict parser still rejects it.
    /// The raw model text remains in the transcript and physical receipt; this never edits content.
    /// </summary>
    public static bool TryUnwrapFencedJsonObject(string? text, out string unwrapped)
    {
        unwrapped = text ?? "";
        if (text is null) return false;
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal) || !trimmed.EndsWith("```", StringComparison.Ordinal) || trimmed.Length < 7) return false;
        var firstLineEnd = trimmed.IndexOf('\n');
        if (firstLineEnd < 0) return false;
        var fenceLabel = trimmed[3..firstLineEnd].Trim();
        if (fenceLabel.Length > 0 && !fenceLabel.Equals("json", StringComparison.OrdinalIgnoreCase)) return false;
        var body = trimmed[(firstLineEnd + 1)..^3].Trim();
        if (body.Contains("```", StringComparison.Ordinal) || !body.StartsWith('{') || !body.EndsWith('}')) return false;
        try { using var document = JsonDocument.Parse(body); if (document.RootElement.ValueKind != JsonValueKind.Object) return false; }
        catch (JsonException) { return false; }
        unwrapped = body;
        return true;
    }

    public static bool TryParseStrictResponse(string? physicalResponse, out ContinualReportCorrectionResponse response)
    {
        response = null!;
        if (physicalResponse is null || physicalResponse.Length > MaximumReportCharacters + MaximumReferences * 700) return false;
        try
        {
            using var document = JsonDocument.Parse(physicalResponse);
            var root = document.RootElement;
            if (!ExactKeys(root, "report", "references") || root.GetProperty("report").ValueKind != JsonValueKind.String
                || root.GetProperty("references").ValueKind != JsonValueKind.Array) return false;
            var references = new List<ContinualReportReference>();
            foreach (var item in root.GetProperty("references").EnumerateArray())
            {
                if (references.Count == MaximumReferences || !ExactKeys(item, "kind", "recordId", "sourceReceiptId")
                    || item.EnumerateObject().Any(p => p.Value.ValueKind != JsonValueKind.String)) return false;
                var reference = new ContinualReportReference(item.GetProperty("kind").GetString()!,
                    item.GetProperty("recordId").GetString()!, item.GetProperty("sourceReceiptId").GetString()!);
                if (!Bounded(reference.Kind, 64) || !Bounded(reference.RecordId, 256) || !Bounded(reference.SourceReceiptId, 256)) return false;
                references.Add(reference);
            }
            var report = root.GetProperty("report").GetString()!;
            if (!Bounded(report, MaximumReportCharacters)) return false;
            response = new(report, references);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool Bounded(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum
        && value == value.Trim() && !value.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t');
    private static bool ExactKeys(JsonElement value, params string[] required)
        => value.ValueKind == JsonValueKind.Object
            && value.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).SequenceEqual(required.Order(StringComparer.Ordinal));
}
