// Namespace retained for source compatibility; wire records have no dependency on agent policy or hosts.
namespace SwarmCore.Agent.BusinessDispatch;

public sealed record BusinessProviderEnvelope(string Channel, string AccountId, string OwnerUserId,
    string Sender, string Target, string Subject, string Body, string MessageId,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    long? RecipientContactId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<BusinessMessageMedia>? Media = null);
public sealed record BusinessAdapterIdentity(string ReceiverId, string AccountId, string Sender, string Channel,
    bool Authenticated, bool CanDispatch);
public sealed record BusinessAdapterOperation(string OperationId, string Fingerprint, BusinessProviderEnvelope Action);
public sealed record BusinessAdapterReceipt(string OperationId, string Fingerprint, string AccountId,
    string ReceiverId, bool Accepted, bool Confirmed, string ReceiptId);

public static class BusinessAdapterWire
{
    public static string Fingerprint(BusinessProviderEnvelope action) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(action))));
}
