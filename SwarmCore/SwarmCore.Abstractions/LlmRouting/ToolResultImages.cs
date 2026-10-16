namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>Transient pixels returned out of band by a host tool. Implementations must
/// exclude this property from JSON and redact pixels from ToString. Tool receipts remain metadata only.</summary>
public interface IToolResultImages
{
    IReadOnlyList<ImagePart> Images { get; }
}

/// <summary>Typed internal transport value; never serialize into execution settings on the wire.</summary>
public sealed record CliImageAttachments([property: System.Text.Json.Serialization.JsonIgnore] IReadOnlyList<ImagePart> Images)
{
    public const string ExecutionSetting = "__swarmcore_cli_images";
    public override string ToString() => $"CliImageAttachments({Images.Count} images)";
}
