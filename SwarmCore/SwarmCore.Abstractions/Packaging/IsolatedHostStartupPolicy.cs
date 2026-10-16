namespace SwarmCore.Abstractions.Packaging;

/// <summary>Explicit host startup authority; isolation disables machine integrations and automatic maintenance.</summary>
public sealed record IsolatedHostStartupPolicy(bool Isolated)
{
    public bool AutomaticMaintenanceAllowed => !Isolated;
    public static IsolatedHostStartupPolicy Normal { get; } = new(false);
    public static IsolatedHostStartupPolicy FromExplicitValue(string? value)
        => new(string.Equals(value?.Trim(), "1", StringComparison.Ordinal)
            || string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase));
}
