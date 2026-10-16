using System.Runtime.InteropServices;

namespace SwarmCore.LlmRouting;

/// <summary>
/// Compiled platform containment for local model artifacts. Model names remain
/// discovery data, but known accelerator-specific formats must never be pulled
/// or executed on an incompatible host.
/// </summary>
public static class LocalModelArtifactCompatibility
{
    public static bool IsCompatible(string? modelName, bool isAppleSilicon)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return false;
        if (isAppleSilicon) return true;

        var normalized = modelName.Trim().ToLowerInvariant();
        return !normalized.Contains("-mlx", StringComparison.Ordinal)
               && !normalized.Contains("-mxfp8", StringComparison.Ordinal)
               && !normalized.Contains("-nvfp4", StringComparison.Ordinal);
    }

    public static bool IsCompatibleWithCurrentProcess(string? modelName)
    {
        var isAppleSilicon = (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
                             && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        return IsCompatible(modelName, isAppleSilicon);
    }
}
