using System.Text;
using System.Text.Json;

namespace SwarmCore.Agent.Continual;

/// <summary>
/// Lossless, bounded evidence for delivery verification. Source text is data;
/// clipping a JSON prefix can discard the actual records and their citations.
/// Hosts redact secrets before persisting receipts, but retain their contents.
/// </summary>
public static class ContinualDeliveryAuditEvidence
{
    // This is a payload ceiling, not a provider context override. Normal router
    // admission still applies. Overflow stays verification-only, never replay.
    public const int MaximumCharacters = 256_000;

    public static bool TryFormat(IEnumerable<DeliveryAuditEvidenceReceipt> receipts, out string evidence)
        => TryFormat(receipts, r => r.Input, r => r.Result, out evidence);

    public static bool TryFormatWorkerObservations(IEnumerable<WorkerToolObservation> observations, out string evidence)
        => TryFormat(observations, r => r.InputJson, r => r.Result, out evidence);

    private static bool TryFormat<T>(IEnumerable<T> receipts, Func<T, string?> input,
        Func<T, string?> result, out string evidence)
    {
        var builder = new StringBuilder("[");
        foreach (var receipt in receipts)
        {
            // Avoid constructing an oversized serialization, and recognize old
            // persisted receipts whose trailing evidence was already discarded.
            if ((long)(input(receipt)?.Length ?? 0) + (result(receipt)?.Length ?? 0) > MaximumCharacters
                || input(receipt)?.EndsWith(" …[truncated]", StringComparison.Ordinal) == true
                || result(receipt)?.EndsWith(" …[truncated]", StringComparison.Ordinal) == true)
            {
                evidence = string.Empty;
                return false;
            }
            var serialized = JsonSerializer.Serialize(receipt);
            if (builder.Length + (long)serialized.Length + 2 > MaximumCharacters)
            {
                evidence = string.Empty;
                return false;
            }
            if (builder.Length > 1) builder.Append(',');
            builder.Append(serialized);
        }
        evidence = builder.Append(']').ToString();
        return true;
    }

    public const string UnavailableReason =
        "The complete saved tool evidence cannot fit in one delivery audit, or an older receipt was truncated. "
        + "The saved result remains unverified; preserve completed actions and verify their evidence without repeating them.";
}

public sealed record DeliveryAuditEvidenceReceipt(
    string Reference, string Tool, string Status, string SideEffect, string? Input, string? Result)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ContinualReceiptResolution? HostResolution { get; init; }
}
