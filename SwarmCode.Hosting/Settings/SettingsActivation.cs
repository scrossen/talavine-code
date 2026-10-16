namespace SwarmCode.Hosting.Settings;

/// <summary>The desktop supplies this above its workspaces; the active request completes before runtime replacement.</summary>
public sealed class SettingsActivation
{
    public bool AppliesWhenIdle { get; set; }
    public event Action? ReloadRequested;
    public void RequestReload() => ReloadRequested?.Invoke();
    public string Message => AppliesWhenIdle
        ? "Saved. Runtime changes apply automatically after active work and queued messages finish."
        : "Saved. Runtime changes apply when the repository is reopened (or the console restarts).";
}
