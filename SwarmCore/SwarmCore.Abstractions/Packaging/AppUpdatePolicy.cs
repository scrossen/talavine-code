namespace SwarmCore.Abstractions.Packaging;

/// <summary>Defines which distribution channel owns application replacement.</summary>
public sealed class AppUpdatePolicy
{
    private AppUpdatePolicy(string? managedUpdateMessage) => ManagedUpdateMessage = managedUpdateMessage;

    public static AppUpdatePolicy ApplicationManaged { get; } = new(null);

    public static AppUpdatePolicy DistributionManaged(string distributionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distributionName);
        return new($"Updates are managed by {distributionName}. Check for updates there.");
    }

    public bool AllowsExternalInstallers => ManagedUpdateMessage is null;
    public string? ManagedUpdateMessage { get; }

    public void EnsureExternalInstallerAllowed()
    {
        if (!AllowsExternalInstallers)
            throw new InvalidOperationException(ManagedUpdateMessage);
    }
}
