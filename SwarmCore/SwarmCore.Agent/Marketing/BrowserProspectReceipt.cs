using System.Security.Cryptography;
using System.Text.Json;

namespace SwarmCore.Agent.Marketing;

/// <summary>Browser-observed research/action evidence, never a contact permission or CRM command.</summary>
public sealed record BrowserProspectReceipt(Guid AssignmentId, Guid ConnectionId, Guid EntryId,
    string AccountHandle, SocialProspectEvidence Evidence, SocialFollowState State, string Detail)
{
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
        new { AssignmentId, ConnectionId, EntryId, AccountHandle, Evidence, State, Detail })));
    public void Validate()
    {
        if (AssignmentId == Guid.Empty || ConnectionId == Guid.Empty || EntryId == Guid.Empty
            || !SocialProspectingPlan.ValidHandle(AccountHandle) || Evidence?.Person is not { } p
            || !SocialProspectingPlan.ValidHandle(p.Handle) || p.Id != "handle:" + p.Handle.ToLowerInvariant()
            || p.Name is null || p.Name.Length > 300 || p.Bio is null || p.Bio.Length > 4000
            || string.IsNullOrWhiteSpace(Evidence.Query) || Evidence.Query.Length > 200
            || Evidence.MatchedPhrases is not { Length: >= 1 and <= 8 }
            || Evidence.MatchedPhrases.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 100
                || !p.Bio.Contains(x, StringComparison.OrdinalIgnoreCase))
            || Evidence.ObservedAt > DateTimeOffset.UtcNow.AddMinutes(5) || Evidence.ObservedAt.Year < 2020
            || !Enum.IsDefined(State) || Detail is null || Detail.Length > 1000)
            throw new ArgumentException("Invalid bounded browser prospect receipt.");
    }
}
public sealed record BrowserReceiptAck(bool Success, string? Fingerprint = null);
