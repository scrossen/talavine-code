using System.Security.Cryptography;
using System.Text;

namespace SwarmCore.Agent.Business;

/// <summary>Typed source attestations, never parsed customer prose. A host verifies the signature,
/// source and customer mapping before applying any exact commercial or fulfillment decision.</summary>
public sealed record BusinessTrustedInbound(string EventId, string Kind, DateTimeOffset ObservedAtUtc,
    string AuthenticatedSubject, Guid ProposalId, int ProposalVersion, string ProposalHash,
    Guid IssueActionId, string AccountId, bool ExplicitAcceptance,
    Guid? FulfillmentId = null, string? ProviderReceiptId = null, string? Carrier = null, string? TrackingNumber = null);

public static class BusinessTrustedInboundProtocol
{
    public const int MaximumBytes = 16384;
    public static bool Verify(ReadOnlySpan<byte> body, string timestamp, string signature, string keyBase64, DateTimeOffset now, Guid sourceId)
    {
        if (body.Length is < 1 or > MaximumBytes || !long.TryParse(timestamp, out var seconds) || signature.Length != 64) return false;
        DateTimeOffset at;
        try { at = DateTimeOffset.FromUnixTimeSeconds(seconds); } catch (ArgumentOutOfRangeException) { return false; }
        if (at < now.AddMinutes(-5) || at > now.AddMinutes(1)) return false;
        byte[] key, claimed;
        try { key = Convert.FromBase64String(keyBase64); claimed = Convert.FromHexString(signature); }
        catch (FormatException) { return false; }
        if (key.Length < 32) return false;
        if (sourceId == Guid.Empty) return false;
        var prefix = Encoding.UTF8.GetBytes("business-inbound-v1:" + sourceId.ToString("N") + ":" + timestamp + ".");
        var signed = new byte[prefix.Length + body.Length]; prefix.CopyTo(signed, 0); body.CopyTo(signed.AsSpan(prefix.Length));
        try { return CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(key, signed), claimed); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}

public sealed record BusinessCarrierRequest(Guid FulfillmentId, long OrderId, int FulfillmentRevision,
    string Destination, string Service, string Parcel, string MaximumCost, string Currency);
public sealed record BusinessCarrierStatus(string EventId, Guid FulfillmentId, Guid ActionId, string AccountId,
    string ReceiverId, string ProviderReceiptId, string State, string Carrier, string TrackingNumber, DateTimeOffset ObservedAtUtc);
