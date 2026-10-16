namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>Documented support is independent of measured proof and account access.</summary>
public enum DocumentedCapability { Unknown = 0, Unsupported = 1, Supported = 2 }

public sealed record ModelTransportCapabilities
{
    public required string Transport { get; init; }
    public DocumentedCapability Text { get; init; }
    public DocumentedCapability Streaming { get; init; }
    public DocumentedCapability Tools { get; init; }
    public DocumentedCapability Images { get; init; }
    public DocumentedCapability StructuredOutput { get; init; }

    public string? Reject(bool tools, bool images, bool structured, bool streaming = false)
    {
        foreach (var (required, state, name) in new[] {
            (true, Text, "text"), (tools, Tools, "function tools"), (images, Images, "images"),
            (structured, StructuredOutput, "structured output"), (streaming, Streaming, "streaming") })
            if (required && state != DocumentedCapability.Supported)
                return $"{Transport}: required {name} is {state.ToString().ToLowerInvariant()}; choose a verified compatible transport/model.";
        return null;
    }
}
