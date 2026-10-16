using SwarmCore.Coding.Native;
using SwarmCore.Testing;

namespace SwarmCore.Coding.Tests;

public sealed class NativeCliExecutableSelectionTests
{
    [PosixFact]
    public void Broken_help_cannot_advertise_confinement_and_does_not_hide_a_working_cli()
    {
        var directory = Directory.CreateTempSubdirectory("native-cli-selection-");
        try
        {
            string Script(string name, int exit)
            {
                var path = Path.Combine(directory.FullName, name);
                File.WriteAllText(path, $"#!/bin/sh\nprintf '%s\\n' '--restricted'\nexit {exit}\n");
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                return path;
            }
            var broken = Script("stale-shim", 127);
            var working = Script("working-cli", 0);
            Assert.False(NativeCliCapabilities.Supports(broken, "--restricted"));
            Assert.Equal(working, NativeCliCapabilities.ResolveExecutable([broken, working]));
            Assert.True(NativeCliCapabilities.Supports(working, "--restricted"));
        }
        finally { directory.Delete(recursive: true); }
    }
}

