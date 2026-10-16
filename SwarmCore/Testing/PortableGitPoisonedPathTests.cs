using System.Runtime.InteropServices;
using System.Text.Json;
using SwarmCore.Abstractions.Processes;
using Xunit;
using Xunit.Abstractions;

namespace SwarmCore.Testing;

public sealed class PortableGitPoisonedPathTests(ITestOutputHelper output)
{
    private sealed class PoisonedPathFactAttribute : FactAttribute
    {
        public PoisonedPathFactAttribute()
        {
            if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
                Skip = "Requires a native Apple Silicon macOS process.";
            else if (Environment.GetEnvironmentVariable("SWARMCODE_TEST_POISONED_GIT") is not { Length: > 0 })
                Skip = "Launch a fresh test process with an Intel-only git first on PATH and SWARMCODE_TEST_POISONED_GIT set.";
        }
    }

    [PoisonedPathFact]
    public async Task Real_git_operations_use_the_fallback_with_a_fresh_poisoned_path_cache()
    {
        var poison = Environment.GetEnvironmentVariable("SWARMCODE_TEST_POISONED_GIT")!;
        var failure = Assert.Throws<PlatformNotSupportedException>(() => AppleSiliconExecutable.ResolveCompatiblePath("git"));
        Assert.Contains(poison, failure.Message);
        Assert.True(TestGit.Available());
        Assert.Equal("/usr/bin/git", TestGit.Executable);
        var directory = Directory.CreateTempSubdirectory("portable-git-");
        try
        {
            var version = TestGit.Run(directory.FullName, "--version");
            Assert.True(version.ExitCode == 0, version.Error);
            Assert.StartsWith("git version ", version.Output);
            var init = TestGit.Run(directory.FullName, "init", "--template=", "-q");
            Assert.True(init.ExitCode == 0, init.Error);
            File.WriteAllText(Path.Combine(directory.FullName, "proof.txt"), "portable git proof\n");
            var hash = TestGit.Run(directory.FullName, "hash-object", "-w", "proof.txt");
            Assert.True(hash.ExitCode == 0, hash.Error);
            var read = await TestGit.RunAsync(directory.FullName, "cat-file", "-p", hash.Output.Trim());
            Assert.True(read.ExitCode == 0, read.Error);
            Assert.Equal("portable git proof\n", read.Output);
            output.WriteLine(JsonSerializer.Serialize(new
            {
                processId = Environment.ProcessId, assembly = GetType().Assembly.GetName().Name,
                rejected = poison, reason = failure.Message, resolved = TestGit.Executable,
                version = version.Output.Trim(), objectId = hash.Output.Trim(), roundTrip = true
            }));
        }
        finally
        {
            foreach (var file in directory.EnumerateFiles("*", SearchOption.AllDirectories)) file.Attributes = FileAttributes.Normal;
            directory.Delete(recursive: true);
        }
    }
}
