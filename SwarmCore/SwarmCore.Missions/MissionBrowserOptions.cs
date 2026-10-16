namespace SwarmCore.Missions;

/// <summary>Host-owned browser installation and profile location. Each host must
/// supply its own writable profile root; no product-specific paths are assumed.</summary>
public sealed class MissionBrowserOptions
{
    public string? BrowserChannel { get; set; }
    public string UserDataDirectory { get; set; } = "";
    public bool Headless { get; set; }
    public int ViewportWidth { get; set; } = 1280;
    public int ViewportHeight { get; set; } = 800;
    public float DeviceScaleFactor { get; set; } = 1;
    public TimeSpan ActionTimeout { get; set; } = TimeSpan.FromSeconds(15);
}
