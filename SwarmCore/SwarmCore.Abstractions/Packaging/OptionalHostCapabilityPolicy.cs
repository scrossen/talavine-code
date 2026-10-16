namespace SwarmCore.Abstractions.Packaging;

[Flags]
public enum OptionalHostCapability
{
    None = 0,
    LocalSidecars = 1,
    ExternalBrowserBridge = 2,
    ManagedBrowser = 4,
    ExternalMcpServer = 8,
    All = LocalSidecars | ExternalBrowserBridge | ManagedBrowser | ExternalMcpServer
}

/// <summary>
/// Host-supplied distribution limits, independent of saved user configuration.
/// Availability never grants permission or proves a runtime is connected.
/// </summary>
public sealed class OptionalHostCapabilityPolicy(
    OptionalHostCapability available = OptionalHostCapability.All,
    string unavailableReason = "This feature is unavailable in this edition.",
    bool requireBundledExecutables = false)
{
    public static OptionalHostCapabilityPolicy Unrestricted { get; } = new();

    public bool RequireBundledExecutables { get; } = requireBundledExecutables;

    public bool Allows(OptionalHostCapability required) => (available & required) == required;

    public string? GetUnavailableReason(OptionalHostCapability required) =>
        Allows(required) ? null : unavailableReason;

    public void Require(OptionalHostCapability required)
    {
        if (GetUnavailableReason(required) is { } reason)
            throw new NotSupportedException(reason);
    }
}
