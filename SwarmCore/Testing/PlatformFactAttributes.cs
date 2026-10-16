using Xunit;

namespace SwarmCore.Testing;

/// <summary>
/// Platform-conditional facts. A test that cannot run on this machine reports
/// Skipped with a reason instead of returning early and reporting Passed with
/// nothing asserted.
/// </summary>
internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Requires Windows.";
    }
}

internal sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Requires Windows.";
    }
}

internal sealed class MacOSFactAttribute : FactAttribute
{
    public MacOSFactAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = "Requires macOS.";
    }
}

internal sealed class WindowsOrMacOSFactAttribute : FactAttribute
{
    public WindowsOrMacOSFactAttribute()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()) Skip = "Requires Windows or macOS.";
    }
}

/// <summary>
/// A fact that creates symbolic links. Windows allows that only with Developer Mode or elevation; without it the test
/// reports Skipped instead of failing on the environment.
/// </summary>
internal sealed class SymbolicLinkFactAttribute : FactAttribute
{
    public SymbolicLinkFactAttribute()
    {
        if (!SymbolicLinkSupport.Available) Skip = SymbolicLinkSupport.SkipReason;
    }
}

internal sealed class SymbolicLinkTheoryAttribute : TheoryAttribute
{
    public SymbolicLinkTheoryAttribute()
    {
        if (!SymbolicLinkSupport.Available) Skip = SymbolicLinkSupport.SkipReason;
    }
}

internal static class SymbolicLinkSupport
{
    public const string SkipReason = "Requires permission to create symbolic links (on Windows, Developer Mode or elevation).";

    private static readonly Lazy<bool> Probe = new(() =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "symlink-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, "target.txt");
            File.WriteAllText(target, "");
            File.CreateSymbolicLink(Path.Combine(directory, "link.txt"), target);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return false;
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    });

    /// <summary>True when this process can create a symbolic link (probed once).</summary>
    public static bool Available => Probe.Value;
}
