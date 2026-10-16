using SwarmCore.Coding.Native;
using SwarmCore.Testing;
using Xunit;

namespace SwarmCore.Coding.Tests;

/// <summary>
/// An Intel-only Mach-O is refused by the shared launcher with <see cref="PlatformNotSupportedException"/>; the
/// capability probe must treat that as "flag not supported", not let it escape to the caller.
/// </summary>
public sealed class NativeCliCapabilitiesAppleSiliconTests
{
    private static string WriteExecutable(string directory, string name, byte[] content)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, content);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    // MH_MAGIC_64 (little-endian) followed by CPU_TYPE_X86_64.
    private static readonly byte[] X86MachO = [0xCF, 0xFA, 0xED, 0xFE, 0x07, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0, 0, 0];

    [AppleSiliconFact]
    public void An_intel_only_macho_is_reported_unsupported_instead_of_throwing()
    {
        var directory = Directory.CreateTempSubdirectory("native-cli-x86-").FullName;
        try
        {
            var x86 = WriteExecutable(directory, "cli-x86", X86MachO);
            var script = WriteExecutable(directory, "cli-script", "#!/bin/sh\necho '--restricted'\n"u8.ToArray());

            Assert.False(NativeCliCapabilities.Supports(x86, "--restricted"));
            // A working POSIX script candidate is still probed normally after the x86 one fails.
            Assert.True(NativeCliCapabilities.Supports(script, "--restricted"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
