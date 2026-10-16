namespace SwarmCore.Coding.Computer;

/// <summary>Regular signed-in Chrome, an optional explicit connection, or a separate persistent host profile.</summary>
public sealed class CodingComputerOptions
{
    public const string SectionName = "Coding:Computer";
    public bool Enabled { get; set; } = true;
    /// <summary>Connect to Chrome's normal signed-in profile using Chrome 144+ remote debugging consent.</summary>
    public bool UsePersonalChrome { get; set; } = true;
    /// <summary>Chrome user data root, not an individual Default/Profile N subfolder. Empty discovers the OS default.</summary>
    public string? PersonalChromeUserDataDirectory { get; set; }
    public string BrowserProfileDirectory { get; set; } = "";
    public string? ChromeCdpEndpoint { get; set; }
    public bool Headless { get; set; }
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 800;
    public int ChallengeMaxActions { get; set; } = 12;
    public int ChallengeTimeoutSeconds { get; set; } = 90;
}
