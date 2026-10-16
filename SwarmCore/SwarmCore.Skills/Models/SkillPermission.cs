namespace SwarmCore.Skills.Models;

/// <summary>
/// Permissions required by skills to perform operations.
/// </summary>
public class SkillPermission
{
    /// <summary>
    /// Unique identifier for this permission.
    /// </summary>
    public required string PermissionId { get; init; }

    /// <summary>
    /// Human-readable name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Description of what this permission allows.
    /// </summary>
    public string Description { get; init; } = "";

    /// <summary>
    /// The level/severity of this permission.
    /// </summary>
    public PermissionLevel Level { get; init; } = PermissionLevel.Standard;

    // Predefined permissions
    public static readonly SkillPermission ReadData = new()
    {
        PermissionId = "data.read",
        Name = "Read Data",
        Description = "Read application data",
        Level = PermissionLevel.Standard
    };

    public static readonly SkillPermission WriteData = new()
    {
        PermissionId = "data.write",
        Name = "Write Data",
        Description = "Modify application data",
        Level = PermissionLevel.Elevated
    };

    public static readonly SkillPermission SendEmail = new()
    {
        PermissionId = "email.send",
        Name = "Send Email",
        Description = "Send emails on behalf of user",
        Level = PermissionLevel.Elevated
    };

    public static readonly SkillPermission ReadEmail = new()
    {
        PermissionId = "email.read",
        Name = "Read Email",
        Description = "Read user emails",
        Level = PermissionLevel.Standard
    };

    public static readonly SkillPermission NetworkAccess = new()
    {
        PermissionId = "network.access",
        Name = "Network Access",
        Description = "Make external network requests",
        Level = PermissionLevel.Elevated
    };

    public static readonly SkillPermission FileSystemRead = new()
    {
        PermissionId = "filesystem.read",
        Name = "File System Read",
        Description = "Read files from the file system",
        Level = PermissionLevel.Elevated
    };

    public static readonly SkillPermission FileSystemWrite = new()
    {
        PermissionId = "filesystem.write",
        Name = "File System Write",
        Description = "Write files to the file system",
        Level = PermissionLevel.Restricted
    };

    public static readonly SkillPermission ExecuteCode = new()
    {
        PermissionId = "code.execute",
        Name = "Execute Code",
        Description = "Execute arbitrary code or commands",
        Level = PermissionLevel.Admin
    };

    /// <summary>
    /// Read local runtime health, routing history, and other diagnostic state.
    /// Diagnostic playbooks use this to describe their evidence needs without
    /// implying access to the user's messages or general application data.
    /// </summary>
    public static readonly SkillPermission ReadDiagnostics = new()
    {
        PermissionId = "diagnostics.read",
        Name = "Read Diagnostics",
        Description = "Read local health and diagnostic information",
        Level = PermissionLevel.Standard
    };

    /// <summary>
    /// Request a governed, reversible local change such as restarting a local
    /// runtime or changing a model route. The underlying tool still owns its
    /// action-time policy and receipt; activating a playbook never performs the
    /// write by itself.
    /// </summary>
    public static readonly SkillPermission WritesLocal = new()
    {
        PermissionId = "local.write",
        Name = "Local Changes",
        Description = "Request governed changes on this device",
        Level = PermissionLevel.Standard
    };

    public override bool Equals(object? obj) =>
        obj is SkillPermission other && PermissionId.Equals(other.PermissionId, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => PermissionId.ToLowerInvariant().GetHashCode();

    public override string ToString() => $"{Name} ({PermissionId})";
}

/// <summary>
/// Permission severity levels.
/// </summary>
public enum PermissionLevel
{
    /// <summary>
    /// Standard permission - generally safe operations.
    /// </summary>
    Standard = 0,

    /// <summary>
    /// Elevated permission - requires user awareness.
    /// </summary>
    Elevated = 1,

    /// <summary>
    /// Restricted permission - requires explicit approval.
    /// </summary>
    Restricted = 2,

    /// <summary>
    /// Admin permission - requires administrator approval.
    /// </summary>
    Admin = 3
}
