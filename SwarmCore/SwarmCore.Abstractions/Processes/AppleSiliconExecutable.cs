using System.Buffers.Binary;

namespace SwarmCore.Abstractions.Processes;

/// <summary>
/// Classifies native Apple executables without launching them. Scripts and
/// other non-Mach-O files are intentionally allowed; Mach-O files must contain
/// an ARM64 slice before an Apple Silicon Talavine product may execute them.
/// </summary>
public static class AppleSiliconExecutable
{
    private const uint CpuTypeArm64 = 0x0100000c;
    private const uint MachMagic32 = 0xfeedface;
    private const uint MachMagic64 = 0xfeedfacf;
    private const uint MachCigam32 = 0xcefaedfe;
    private const uint MachCigam64 = 0xcffaedfe;
    private const uint FatMagic32 = 0xcafebabe;
    private const uint FatMagic64 = 0xcafebabf;
    private const uint FatCigam32 = 0xbebafeca;
    private const uint FatCigam64 = 0xbfbafeca;
    private const int MaximumFatArchitectureCount = 128;

    /// <summary>
    /// Returns true for scripts/non-Mach-O files and Mach-O files with an ARM64
    /// slice. Missing, unreadable, malformed, and Intel-only Mach-O files are
    /// rejected.
    /// </summary>
    public static bool IsCompatible(string path)
        => Inspect(path) is AppleExecutableArchitecture.NonMachO
            or AppleExecutableArchitecture.Arm64
            or AppleExecutableArchitecture.UniversalArm64;

    /// <summary>
    /// On an Apple host, resolves an executable the same way a normal command
    /// launch would and rejects a resolved Mach-O without an ARM64 slice.
    /// Other platforms, scripts, and unresolved command names are unchanged.
    /// </summary>
    public static string ResolveCompatiblePath(
        string path,
        string? workingDirectory = null,
        string? searchPath = null)
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsMacCatalyst())
        {
            return path;
        }

        var resolved = TryResolvePath(path, workingDirectory, searchPath);
        if (resolved is null)
        {
            return path;
        }

        var architecture = Inspect(resolved);
        if (architecture is AppleExecutableArchitecture.UnsupportedMachO
            or AppleExecutableArchitecture.Unreadable)
        {
            throw new PlatformNotSupportedException(
                $"Apple Silicon requires an arm64 executable; refusing to launch '{resolved}' "
                + $"({architecture}).");
        }

        return resolved;
    }

    public static AppleExecutableArchitecture Inspect(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return AppleExecutableArchitecture.Unreadable;
        }

        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length < sizeof(uint))
            {
                return AppleExecutableArchitecture.NonMachO;
            }

            Span<byte> header = stackalloc byte[8];
            if (!TryReadExactly(stream, header[..4]))
            {
                return AppleExecutableArchitecture.Unreadable;
            }

            var magicLittleEndian = BinaryPrimitives.ReadUInt32LittleEndian(header);
            var magicBigEndian = BinaryPrimitives.ReadUInt32BigEndian(header);

            if (magicLittleEndian is MachMagic32 or MachMagic64)
            {
                return InspectThin(stream, littleEndian: true, header);
            }

            if (magicLittleEndian is MachCigam32 or MachCigam64)
            {
                return InspectThin(stream, littleEndian: false, header);
            }

            if (magicBigEndian is FatMagic32 or FatMagic64)
            {
                return InspectFat(
                    stream,
                    littleEndian: false,
                    is64Bit: magicBigEndian == FatMagic64,
                    header);
            }

            if (magicBigEndian is FatCigam32 or FatCigam64)
            {
                return InspectFat(
                    stream,
                    littleEndian: true,
                    is64Bit: magicBigEndian == FatCigam64,
                    header);
            }

            return AppleExecutableArchitecture.NonMachO;
        }
        catch
        {
            return AppleExecutableArchitecture.Unreadable;
        }
    }

    private static string? TryResolvePath(
        string path,
        string? workingDirectory,
        string? searchPath)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (Path.IsPathFullyQualified(path))
        {
            return File.Exists(path) ? path : null;
        }

        var launchDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(workingDirectory);

        if (path.Contains(Path.DirectorySeparatorChar)
            || path.Contains(Path.AltDirectorySeparatorChar))
        {
            var candidate = Path.GetFullPath(path, launchDirectory);
            return File.Exists(candidate) ? candidate : null;
        }

        var effectivePath = searchPath ?? Environment.GetEnvironmentVariable("PATH");
        if (effectivePath is null)
        {
            return null;
        }

        foreach (var pathEntry in effectivePath.Split(Path.PathSeparator))
        {
            var trimmedEntry = pathEntry.Trim();
            var directory = string.IsNullOrEmpty(trimmedEntry)
                ? launchDirectory
                : Path.IsPathFullyQualified(trimmedEntry)
                    ? trimmedEntry
                    : Path.GetFullPath(trimmedEntry, launchDirectory);
            var candidate = Path.Combine(directory, path);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static AppleExecutableArchitecture InspectThin(
        Stream stream,
        bool littleEndian,
        Span<byte> header)
    {
        if (!TryReadExactly(stream, header[4..8]))
        {
            return AppleExecutableArchitecture.UnsupportedMachO;
        }

        var cpuType = ReadUInt32(header[4..8], littleEndian);
        return cpuType == CpuTypeArm64
            ? AppleExecutableArchitecture.Arm64
            : AppleExecutableArchitecture.UnsupportedMachO;
    }

    private static AppleExecutableArchitecture InspectFat(
        Stream stream,
        bool littleEndian,
        bool is64Bit,
        Span<byte> header)
    {
        if (!TryReadExactly(stream, header[4..8]))
        {
            return AppleExecutableArchitecture.UnsupportedMachO;
        }

        var architectureCount = ReadUInt32(header[4..8], littleEndian);
        if (architectureCount is 0 or > MaximumFatArchitectureCount)
        {
            return AppleExecutableArchitecture.UnsupportedMachO;
        }

        var entrySize = is64Bit ? 32 : 20;
        Span<byte> cpuTypeBytes = stackalloc byte[sizeof(uint)];
        for (var index = 0; index < architectureCount; index++)
        {
            var entryOffset = 8L + (index * (long)entrySize);
            if (entryOffset + sizeof(uint) > stream.Length)
            {
                return AppleExecutableArchitecture.UnsupportedMachO;
            }

            stream.Position = entryOffset;
            if (!TryReadExactly(stream, cpuTypeBytes))
            {
                return AppleExecutableArchitecture.UnsupportedMachO;
            }

            if (ReadUInt32(cpuTypeBytes, littleEndian) == CpuTypeArm64)
            {
                return AppleExecutableArchitecture.UniversalArm64;
            }
        }

        return AppleExecutableArchitecture.UnsupportedMachO;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, bool littleEndian)
        => littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(bytes)
            : BinaryPrimitives.ReadUInt32BigEndian(bytes);

    private static bool TryReadExactly(Stream stream, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = stream.Read(buffer[read..]);
            if (count == 0)
            {
                return false;
            }

            read += count;
        }

        return true;
    }
}

public enum AppleExecutableArchitecture
{
    NonMachO,
    Arm64,
    UniversalArm64,
    UnsupportedMachO,
    Unreadable
}
