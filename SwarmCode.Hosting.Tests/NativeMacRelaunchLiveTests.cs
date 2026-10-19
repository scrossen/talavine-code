using System.Diagnostics;
using SwarmCode.Hosting.Improvement;
using SwarmCore.Coding.SelfImprovement;
using Xunit.Abstractions;

namespace SwarmCode.Hosting.Tests;

public sealed class NativeMacRelaunchLiveFactAttribute : FactAttribute
{
    public NativeMacRelaunchLiveFactAttribute()
    {
        // xUnit 2 requires the opt-in skip during discovery; a runtime
        // SkipException.ForSkip is reported as a failure by its runner.
        if (Environment.GetEnvironmentVariable("SWARMCODE_RUN_NATIVE_RELAUNCH") != "1")
            Skip = "Set SWARMCODE_RUN_NATIVE_RELAUNCH=1 on the native Mac with an isolated test app.";
    }
}

/// <summary>Opt-in real signed AppKit rebuild/relaunch. The operator closes only the isolated test app through native UI automation.</summary>
public sealed class NativeMacRelaunchLiveTests(ITestOutputHelper output)
{
    [Fact]
    public void Relaunch_opt_in_is_evaluated_during_test_discovery()
    {
        var method = typeof(NativeMacRelaunchLiveTests).GetMethod(nameof(Signed_native_app_rebuilds_and_relaunches_with_its_profile))!;
        var fact = Assert.Single(method.GetCustomAttributes(typeof(FactAttribute), inherit: true).Cast<FactAttribute>());
        if (Environment.GetEnvironmentVariable("SWARMCODE_RUN_NATIVE_RELAUNCH") == "1")
            Assert.Null(fact.Skip);
        else
            Assert.Contains("SWARMCODE_RUN_NATIVE_RELAUNCH=1", fact.Skip);
    }

    [NativeMacRelaunchLiveFact]
    public async Task Signed_native_app_rebuilds_and_relaunches_with_its_profile()
    {
        Assert.True(OperatingSystem.IsMacOS());
        var repo = Environment.GetEnvironmentVariable("SWARMCODE_NATIVE_RELAUNCH_REPO")!;
        var artifacts = Environment.GetEnvironmentVariable("SWARMCODE_NATIVE_RELAUNCH_ARTIFACTS")!;
        var executable = Environment.GetEnvironmentVariable("SWARMCODE_NATIVE_RELAUNCH_EXECUTABLE")!;
        var profile = Environment.GetEnvironmentVariable("SWARMCODE_NATIVE_RELAUNCH_PROFILE")!;
        var parentPid = int.Parse(Environment.GetEnvironmentVariable("SWARMCODE_NATIVE_RELAUNCH_PARENT_PID")!);
        Assert.True(SwarmCodeSourceRepository.IsSwarmCodeRepository(repo));
        Assert.True(File.Exists(executable));
        using var parent = Process.GetProcessById(parentPid);
        Assert.Equal(Path.GetFullPath(executable), parent.MainModule!.FileName);
        var paths = new SwarmCodeDataPaths(profile);
        var rebuild = new SwarmCodeRebuild(paths, () => new() { RepositoryRoot = repo }, new InMemorySelfImprovementBacklog());
        // Same signed build wrapper and helper, isolated output/profile so an
        // unrelated real-profile cycle can continue throughout this test.
        var plan = new RebuildPlan(true, "isolated native rebuild verification", repo, executable, "macos-appkit", "/usr/bin/env",
            [$"SWARMCODE_ARTIFACTS_PATH={artifacts}", "/bin/bash", Path.Combine(repo, "scripts", "build-swarmcode-macos.sh")]);
        var script = rebuild.WriteRelaunchScript(plan, parentPid, []);
        var helperPid = rebuild.StartRelaunch(script);
        await File.WriteAllTextAsync(Path.Combine(profile, "relaunch-helper-ready.txt"), helperPid.ToString());
        output.WriteLine($"Relaunch helper {helperPid} is waiting for isolated test app {parentPid} to close.");
        var deadline = DateTime.UtcNow.AddMinutes(4);
        RelaunchStatus? status;
        do
        {
            await Task.Delay(1000);
            status = rebuild.ReadStatus();
        } while (status?.InProgress == true && DateTime.UtcNow < deadline);
        Assert.NotNull(status);
        Assert.False(status.InProgress);
        Assert.Equal(0, status.BuildExitCode);
        var launched = false;
        for (var attempt = 0; attempt < 30 && !launched; attempt++)
        {
            launched = Process.GetProcessesByName("SwarmCode.App").Any(process =>
            {
                using (process)
                {
                    try { return process.Id != parentPid && process.MainModule?.FileName == Path.GetFullPath(executable); }
                    catch (InvalidOperationException) { return false; }
                }
            });
            if (!launched) await Task.Delay(500);
        }
        Assert.True(launched, "The rebuilt native executable did not relaunch.");
        output.WriteLine("Signed build and relaunch passed; native UI readiness remains the operator's final check.");
    }
}
