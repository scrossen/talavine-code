namespace SwarmCore.Agent.BusinessDispatch;

/// <summary>Host selected receiver protocol. A provider must persist the effect identity and serve read-only receipts.</summary>

public static class BusinessAdapterProtocol
{
    public static readonly string[] RoutedChannels = ["shared-email", "sms", "social-follow", "social-post", "social-message", "browser", "device", "carrier"];
    public static string CapabilityFor(string channel) => channel switch
    {
        // "invoice" is an issued-invoice email whose body the host transport rebuilds from the immutable record.
        "email" or "shared-email" or "sms" or "invoice" => BusinessDispatchContract.SendCapability,
        "calendar" => "business.book", "proposal" => "business.propose",
        "onboarding" => BusinessDispatchContract.SendCapability, // H4 client welcome; its transport appends the intake link.
        "social-post" => "business.publish", "social-message" => "business.message",
        "social-follow" => "business.follow",
        "browser" => "business.browser", "device" => "business.delegate", "carrier" => "business.ship",
        _ => throw new ArgumentException("Unsupported business channel.")
    };
    public static BusinessAdapterOperation Operation(BusinessProviderEnvelope envelope)
        => new(envelope.MessageId, BusinessAdapterWire.Fingerprint(envelope), envelope);
    public static bool Matches(BusinessAdapterReceipt receipt, BusinessProviderEnvelope envelope, string receiver)
        => receipt.OperationId == envelope.MessageId && receipt.Fingerprint == BusinessAdapterWire.Fingerprint(envelope)
            && receipt.AccountId == envelope.AccountId && receipt.ReceiverId == receiver
            && !string.IsNullOrWhiteSpace(receipt.ReceiptId) && receipt.ReceiptId.Length <= 320;
}
