using System.Diagnostics;
using SwarmCore.Abstractions.Processes;

namespace SwarmCode.Hosting.Tests;

public sealed class ClaudeGoalHookCliTests : TempDirTest
{
    [Fact]
    public void Private_hook_mode_requires_endpoint_and_keeps_regular_parsing()
    {
        Assert.NotNull(CliOptions.Parse(["--goal-message-hook"]).Error);
        var parsed = CliOptions.Parse(["--goal-message-hook", "protocol", "--repo", "absent"]);
        Assert.Null(parsed.Error); Assert.Equal("protocol", parsed.GoalMessageHook); Assert.Equal("absent", parsed.Repo);
    }

    [Theory]
    [InlineData("protocol", "swarmcode-goal-hook-v1")]
    [InlineData("invalid-endpoint", "")]
    public async Task Helper_exits_before_repository_profile_or_provider_bootstrap(string endpoint, string expected)
    {
        var isolatedHome = Path.Combine(Tmp, "must-not-exist");
        var baseDir = AppContext.BaseDirectory;
        var testAssembly = Path.GetFileNameWithoutExtension(typeof(ClaudeGoalHookCliTests).Assembly.Location);
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet",
            WorkingDirectory = baseDir, UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in new[] { "exec", "--depsfile", Path.Combine(baseDir, testAssembly + ".deps.json"),
            "--runtimeconfig", Path.Combine(baseDir, testAssembly + ".runtimeconfig.json"), typeof(CliOptions).Assembly.Location,
            "--goal-message-hook", endpoint, "--repo", Path.Combine(Tmp, "missing-repository") }) start.ArgumentList.Add(argument);
        start.Environment[SwarmCode.Hosting.SwarmCodeDataPaths.HomeEnvironmentVariable] = isolatedHome;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var process = ChildProcessLauncher.Start(start);
        using var stop = timeout.Token.Register(() => { try { process.Kill(true); } catch { } });
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        Assert.Equal(0, process.ExitCode); Assert.Equal(expected, (await output).Trim()); Assert.Empty(await error);
        Assert.False(Directory.Exists(isolatedHome));
    }
}
