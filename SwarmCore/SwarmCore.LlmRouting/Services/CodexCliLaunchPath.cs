namespace SwarmCore.LlmRouting.Services;

/// <summary>
/// Resolves a Codex launcher shim to the executable it actually targets.
/// Codex locates <c>codex-code-mode-host</c> relative to its launch path, not
/// through PATH alone, so starting ~/.local/bin/codex leaves tool execution
/// looking beside the shim even when the companion host is installed beside
/// the versioned runtime.
/// </summary>
public static class CodexCliLaunchPath
{
    public static string? Resolve(string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable)
            || !Path.IsPathRooted(executable)
            || !File.Exists(executable)
            || !Path.GetFileNameWithoutExtension(executable).Equals("codex", StringComparison.OrdinalIgnoreCase))
        {
            return executable;
        }

        try
        {
            var target = File.ResolveLinkTarget(executable, returnFinalTarget: true);
            if (target is not null && File.Exists(target.FullName))
                return target.FullName;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (PlatformNotSupportedException) { }

        return executable;
    }
}
