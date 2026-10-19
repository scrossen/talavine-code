using System.Diagnostics;

namespace SwarmCode.Hosting.Tests;

public sealed class NativeMacPlaywrightTests
{
    [NativeMacPlaywrightTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Signed_native_app_discovers_its_driver_and_preserves_explicit_search_paths(bool explicitOverride)
    {
        var executable = Environment.GetEnvironmentVariable("SWARMCODE_NATIVE_PLAYWRIGHT_EXECUTABLE");
        Assert.True(OperatingSystem.IsMacOS());
        Assert.True(File.Exists(executable));
        var start = new ProcessStartInfo(executable!)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Discovery must not depend on launching from the build output.
            WorkingDirectory = Path.GetPathRoot(Path.GetFullPath(executable!))!
        };
        start.ArgumentList.Add("--verify-playwright-driver");
        start.Environment.Remove("PLAYWRIGHT_DRIVER_SEARCH_PATH");
        start.Environment.Remove("PLAYWRIGHT_NODEJS_PATH");
        if (explicitOverride)
            start.Environment["PLAYWRIGHT_DRIVER_SEARCH_PATH"] = Path.Combine(Path.GetDirectoryName(executable!)!, "missing-playwright-driver");
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var result = await stdout;
            var error = await stderr;
            if (explicitOverride)
            {
                Assert.Equal(1, process.ExitCode);
                Assert.Contains("Couldn't find driver in \"PLAYWRIGHT_DRIVER_SEARCH_PATH\"", error);
            }
            else
            {
                Assert.True(process.ExitCode == 0, $"Native driver probe exited {process.ExitCode}: {error}");
                Assert.Contains("PASS: bundled Playwright driver started.", result);
            }
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }
}

public sealed class NativeMacPlaywrightTheoryAttribute : TheoryAttribute
{
    public NativeMacPlaywrightTheoryAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SWARMCODE_NATIVE_PLAYWRIGHT_EXECUTABLE")))
            Skip = "Set SWARMCODE_NATIVE_PLAYWRIGHT_EXECUTABLE to the built native Mac app executable.";
    }
}
