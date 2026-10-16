using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SwarmCore.Agent.Continual;

/// <summary>Structural evidence checks shared by host delivery auditors.</summary>
public static partial class ContinualDeliveryEvidence
{
    public static bool ContainsUnexecutedToolFragment(string output) => ToolFragment().IsMatch(output);

    public static string? NormalizeVerdict(string? verdict) => verdict?.Trim().ToLowerInvariant();

    public static string OutputDigest(string output)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(output)));

    public static bool HasValidReferences(IReadOnlyList<string>? references, IReadOnlyList<DeliveryAuditReceipt> receipts)
        => HasValidReferences(references, [], receipts);

    public static bool HasDistinctReferences(IReadOnlyList<string>? references, IReadOnlyList<string>? observations)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return (references ?? []).Concat(observations ?? [])
            .All(reference => !string.IsNullOrWhiteSpace(reference)
                && !reference.StartsWith("capability-admission:", StringComparison.Ordinal) && seen.Add(reference));
    }

    public static bool HasValidReferences(IReadOnlyList<string>? references, IReadOnlyList<string>? observations,
        IReadOnlyList<DeliveryAuditReceipt> receipts)
    {
        if (!HasDistinctReferences(references, observations)) return false;
        // The observation role does not waive successful support for tool-based delivery.
        if (receipts.Count == 0) return (references is null or { Count: 0 }) && (observations is null or { Count: 0 });
        if (references is not { Count: > 0 }) return false;
        return references.All(reference => receipts.Count(receipt => receipt.Reference == reference) == 1
                && receipts.Any(receipt => receipt.Reference == reference && receipt.CompletedSuccessfully))
            && (observations ?? []).All(reference =>
                // This namespace belongs to host-confirmed storage, never a tool-supplied ID.
                !reference.StartsWith("credential-intake:", StringComparison.Ordinal)
                && receipts.Count(receipt => receipt.Reference == reference) == 1
                && receipts.Any(receipt => receipt.Reference == reference && receipt.CompletedObservation));
    }

    public const string ReferenceRoleInstructions = """
        Include "evidenceReferences": exact toolCallId values for successful supporting receipts only. Tool-based
        delivery still requires at least one successful completed support receipt, and every support reference must be successful.
        Include "observedReceiptReferences": exact additional, completed and dispatched tool receipt IDs for returned
        outcomes described in the report, including ERROR outcomes. Keep the two roles disjoint and each ID unique.
        If the only receipt supports the result and there is no separate observed outcome, use [] for observedReceiptReferences;
        never repeat a supporting ID in the observed array.
        A returned error observation is not proof of successful action, not proof that side effects were absent,
        and never permission to replay. Host credential-intake IDs cannot be tool observations. Host
        capability-admission IDs cannot support either action or observation roles. Observations
        cannot replace successful action, activation, live-probe or credential-storage support. Verify the full
        objective and each claim against the cited result; successful discovery plus a failed send does not prove sending.
        """;

    // Ordinary explanations containing "tool call" are valid answers. Require
    // actual invocation/assignment syntax, not the name of the mechanism.
    [GeneratedRegex(@"(?:^|\s)(?:\.name\s*=|tool[_ -]?call\s*(?:\(|[:=]\s*[\{\[])|input\s*=\s*\{\s*""query""|<tool_use>)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ToolFragment();
}

public sealed record DeliveryAuditReceipt(string Reference, bool CompletedSuccessfully, bool CompletedObservation = false);
