using System.Diagnostics;
using System.Runtime.InteropServices;
using SwarmCore.Abstractions.Processes;
using Xunit;

namespace SwarmCore.Testing;

internal sealed class AppleSiliconFactAttribute : FactAttribute
{
    public AppleSiliconFactAttribute()
    {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
            Skip = "Requires a native Apple Silicon macOS test process and the Xcode command-line compiler.";
    }
}

internal sealed class PosixFactAttribute : FactAttribute
{
    public PosixFactAttribute()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
            Skip = "Requires real POSIX executable shims.";
    }
}

/// <summary>A complete x86_64 Mach-O, compiled locally; never executed through Rosetta.</summary>
internal sealed class AppleSiliconProbeFixture : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("intel-only-probe-");
    public string IntelOnly { get; }
    public string WorkingScript { get; }

    public AppleSiliconProbeFixture()
    {
        IntelOnly = Path.Combine(_root.FullName, "intel-only");
        WorkingScript = Path.Combine(_root.FullName, "working-cli");
        var start = new ProcessStartInfo("/usr/bin/xcrun")
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "--sdk", "macosx", "clang", "-arch", "x86_64", "-x", "c", "-", "-o", IntelOnly })
            start.ArgumentList.Add(argument);
        using var compiler = Process.Start(start)!;
        var errors = compiler.StandardError.ReadToEndAsync();
        var output = compiler.StandardOutput.ReadToEndAsync();
        compiler.StandardInput.Write("#include <stdio.h>\nint main(void) { puts(\"--restricted\"); return 0; }\n");
        compiler.StandardInput.Close();
        Assert.True(compiler.WaitForExit(30000), "Intel fixture compilation timed out.");
        Assert.True(compiler.ExitCode == 0, errors.GetAwaiter().GetResult());
        _ = output.GetAwaiter().GetResult();
        Assert.Equal(AppleExecutableArchitecture.UnsupportedMachO, AppleSiliconExecutable.Inspect(IntelOnly));
        File.WriteAllText(WorkingScript, "#!/bin/sh\nprintf '%s\\n' '--restricted'\n");
        File.SetUnixFileMode(WorkingScript, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void Dispose() => _root.Delete(recursive: true);
}
