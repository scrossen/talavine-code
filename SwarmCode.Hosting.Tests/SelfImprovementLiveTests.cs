using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SwarmCode.Hosting.Improvement;
using SwarmCode.Hosting.Scheduling;
using SwarmCore.Coding.Agents;
using SwarmCore.Coding.SelfImprovement;
using Xunit.Abstractions;

namespace SwarmCode.Hosting.Tests;

/// <summary>
/// Opt-in live proof of the whole self-improvement loop: a real lane (the person's own SwarmCode data directory,
/// copied), a real temporary git repository with a planted bug and a failing test, the real worker, cycle host,
/// worktree, acceptance checks (dotnet build + dotnet test) and commit. Set <c>SWARMCODE_SELF_IMPROVEMENT_LIVE=1</c>.
/// </summary>
public class SelfImprovementLiveTests(ITestOutputHelper output) : TempDirTest
{
    private static bool Enabled => Environment.GetEnvironmentVariable("SWARMCODE_SELF_IMPROVEMENT_LIVE") == "1";

    private static async Task<(int Exit, string Out)> Run(string file, string workingDirectory, params string[] args)
    {
        var info = new ProcessStartInfo(file) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, (await stdout) + (await stderr));
    }

    private async Task<string> PlantedRepositoryAsync()
    {
        var repo = Path.Combine(Tmp, "calc");
        Directory.CreateDirectory(Path.Combine(repo, "Calc"));
        Directory.CreateDirectory(Path.Combine(repo, "Calc.Tests"));
        await File.WriteAllTextAsync(Path.Combine(repo, "Calc.slnx"), "<Solution>\n  <Project Path=\"Calc/Calc.csproj\" />\n  <Project Path=\"Calc.Tests/Calc.Tests.csproj\" />\n</Solution>\n");
        await File.WriteAllTextAsync(Path.Combine(repo, ".gitignore"), "bin/\nobj/\n");
        await File.WriteAllTextAsync(Path.Combine(repo, "AGENTS.md"), "# Calc\n\nA tiny calculator library. Run `dotnet test Calc.slnx` before finishing. Keep changes minimal.\n");
        await File.WriteAllTextAsync(Path.Combine(repo, "Calc", "Calc.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net11.0</TargetFramework>\n    <Nullable>enable</Nullable>\n    <ImplicitUsings>enable</ImplicitUsings>\n  </PropertyGroup>\n</Project>\n");
        await File.WriteAllTextAsync(Path.Combine(repo, "Calc", "Calculator.cs"),
            "namespace Calc;\n\npublic static class Calculator\n{\n    /// <summary>Returns the sum of two numbers.</summary>\n    public static int Add(int a, int b) => a - b;\n\n    public static int Multiply(int a, int b) => a * b;\n}\n");
        await File.WriteAllTextAsync(Path.Combine(repo, "Calc.Tests", "Calc.Tests.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net11.0</TargetFramework>\n    <Nullable>enable</Nullable>\n    <ImplicitUsings>enable</ImplicitUsings>\n    <IsPackable>false</IsPackable>\n  </PropertyGroup>\n  <ItemGroup>\n    <ProjectReference Include=\"../Calc/Calc.csproj\" />\n    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.13.0\" />\n    <PackageReference Include=\"xunit\" Version=\"2.9.3\" />\n    <PackageReference Include=\"xunit.runner.visualstudio\" Version=\"3.0.2\" />\n  </ItemGroup>\n</Project>\n");
        await File.WriteAllTextAsync(Path.Combine(repo, "Calc.Tests", "CalculatorTests.cs"),
            "using Calc;\nusing Xunit;\n\nnamespace Calc.Tests;\n\npublic class CalculatorTests\n{\n    [Fact]\n    public void Adds_two_numbers() => Assert.Equal(5, Calculator.Add(2, 3));\n\n    [Fact]\n    public void Multiplies_two_numbers() => Assert.Equal(6, Calculator.Multiply(2, 3));\n}\n");
        Assert.Equal(0, (await Run("git", repo, "init", "-q", "-b", "main")).Exit);
        Assert.Equal(0, (await Run("git", repo, "-c", "user.name=Test", "-c", "user.email=test@example.com", "add", "-A")).Exit);
        Assert.Equal(0, (await Run("git", repo, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-q", "-m", "calculator with a planted bug")).Exit);
        return repo;
    }

    [Fact]
    public async Task A_real_cycle_repairs_a_planted_bug_with_a_live_lane()
    {
        if (!Enabled) return;
        // The person's own lanes and keys: a copy of their SwarmCode data directory (DPAPI-protected vault, same user).
        var home = Environment.GetEnvironmentVariable(SwarmCodeDataPaths.HomeEnvironmentVariable) is { Length: > 0 } env ? env : SwarmCodeDataPaths.ResolveDefaultRoot();
        foreach (var name in new[] { "secrets.db", "preferences.json", "model-sources.json", "settings.json", "llm-settings.json" })
            if (File.Exists(Path.Combine(home, name))) File.Copy(Path.Combine(home, name), Path.Combine(Paths.RootDirectory, name), overwrite: true);

        var repo = await PlantedRepositoryAsync();
        var baseline = await Run("dotnet", repo, "test", "Calc.slnx", "--nologo", "-v", "q");
        output.WriteLine("Baseline dotnet test exit " + baseline.Exit);
        Assert.NotEqual(0, baseline.Exit); // the planted bug really fails the test before the cycle

        var sessions = new WorkspaceSessionCatalog(Paths);
        var preferences = new AppPreferencesStore(Paths);
        var overrides = new SessionWorkspaceOverrides();
        var backlog = new JsonFileSelfImprovementBacklog(Paths.SelfImprovementDirectory);
        var options = new SelfImprovementOptions { RepositoryRoot = repo, CycleTimeout = TimeSpan.FromMinutes(25), MaxAttemptsPerItem = 1 };
        var logPath = Path.Combine(Paths.LogsDirectory, "live-cycle.log");
        await using var workspaces = new ScheduledRunWorkspaces((_, _, _) => Task.FromResult<ConversationRunner?>(null), (root, ct) =>
        {
            var configuration = new Dictionary<string, string?> { ["Coding:RepositoryRoot"] = root, ["Coding:Agents:Enabled"] = "false" };
            Mcp.McpBridgeLocator.AddOverrides(configuration, Paths);
            return WorkspaceHost.CreateAsync(new SwarmCodeHostOptions
            {
                RepositoryRoot = root, DataPaths = Paths, SelfImprovement = true,
                Configuration = SwarmCodeHostComposer.BuildConfiguration(Paths, configuration),
                ConfigureServices = services =>
                {
                    services.AddSingleton(sessions);
                    services.AddSingleton(preferences);
                    services.AddSingleton(overrides);
                    services.AddSingleton<ISelfImprovementBacklog>(backlog);
                    services.AddSingleton<IAgentTeamStore>(new InMemoryAgentTeamStore());
                },
            }, logging => { logging.AddFilter("SwarmCore", LogLevel.Information); logging.AddProvider(new FileLoggerProvider(logPath)); }, ct);
        });
        var reporter = new SelfImprovementReporter(backlog, () => options);
        var item = (await reporter.ReportAsync(new SelfImprovementReport(SelfImprovementKind.Defect, "Calculator.Add returns the wrong sum",
            "Expected: Calculator.Add(2, 3) returns 5.\nActual: it returns -1.\nThe test Calc.Tests.CalculatorTests.Adds_two_numbers fails: Assert.Equal() Failure, Expected: 5, Actual: -1.",
            Source: SelfImprovementCapture.TurnErrorSource)))!;
        var gate = new SelfImprovementIdleGate(() => options);
        var host = new SwarmCodeSelfImprovementCycleHost(sessions, preferences, workspaces, overrides, Paths, () => options);
        await using var worker = new SelfImprovementWorker(backlog, host, gate, () => options);

        var start = await worker.RunNowAsync(item.Id);
        Assert.True(start.Started, start.Message);
        var deadline = DateTime.UtcNow.AddMinutes(26);
        while (worker.IsRunning)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The live cycle did not finish in time.");
            await Task.Delay(2000);
        }
        var stored = (await backlog.GetAsync(item.Id))!;
        output.WriteLine($"Stage: {stored.Stage}\nNote: {stored.Note}\nBranch: {stored.Branch} Commit: {stored.Commit}\nWorktree: {stored.WorktreePath}\nSession: {stored.SessionId}\n--- evidence ---\n{stored.Evidence}");
        if (File.Exists(logPath)) output.WriteLine("--- log tail ---\n" + string.Join('\n', (await File.ReadAllLinesAsync(logPath)).TakeLast(60)));

        Assert.Equal(SelfImprovementStage.Verified, stored.Stage);
        Assert.NotNull(stored.Branch);
        Assert.NotNull(stored.Commit);
        Assert.Equal(stored.Commit, (await Run("git", repo, "rev-parse", "refs/heads/" + stored.Branch)).Out.Trim());
        // Independent proof: the tests pass on the committed branch, and the source checkout itself is untouched.
        Assert.Equal(0, (await Run("git", repo, "worktree", "add", "--detach", "-q", Path.Combine(Tmp, "verify"), stored.Commit!)).Exit);
        var verified = await Run("dotnet", Path.Combine(Tmp, "verify"), "test", "Calc.slnx", "--nologo", "-v", "q");
        output.WriteLine("Verification dotnet test exit " + verified.Exit + "\n" + verified.Out);
        Assert.Equal(0, verified.Exit);
        Assert.Contains("a - b", await File.ReadAllTextAsync(Path.Combine(repo, "Calc", "Calculator.cs")));
        Assert.Equal("", (await Run("git", repo, "status", "--porcelain")).Out.Trim());
    }
}
