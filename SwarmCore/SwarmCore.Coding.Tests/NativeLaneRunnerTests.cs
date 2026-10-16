using SwarmCore.Coding.Native;
using SwarmCore.Coding.Routing;
using SwarmCore.Testing;
using Xunit;

namespace SwarmCore.Coding.Tests;

public sealed class NativeLaneRunnerTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "native-lane-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly CodingLane Lane = new("claude", ClaudeCodeNativeLaneRunner.ProviderId, "claude-code:default");

    public NativeLaneRunnerTests() => Directory.CreateDirectory(_repo);

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); } catch { /* best effort */ }
    }

    private NativeLaneTurn Turn(string message = "add tests", bool continuation = false, CodingLane? lane = null)
        => new("conv", message, lane ?? Lane, _repo, "swarmcode-cli", continuation);

    [Fact]
    public void Start_info_runs_claude_headless_with_stream_json_and_bypassed_permissions_in_the_repo()
    {
        var psi = ClaudeCodeNativeLaneRunner.BuildStartInfo(Path.Combine(_repo, "claude.exe"), Turn(), sessionId: null);
        var args = psi.ArgumentList.ToList();

        Assert.Equal(_repo, psi.WorkingDirectory);
        Assert.Contains("-p", args);
        Assert.Equal("stream-json", args[args.IndexOf("--output-format") + 1]);
        Assert.Equal("bypassPermissions", args[args.IndexOf("--permission-mode") + 1]);
        Assert.Contains("--verbose", args);
        Assert.DoesNotContain("--resume", args);
        Assert.DoesNotContain("--model", args); // claude-code:default uses the CLI's own default
        Assert.True(psi.RedirectStandardInput && psi.RedirectStandardOutput && psi.RedirectStandardError);
        Assert.False(psi.Environment.ContainsKey("ANTHROPIC_API_KEY"));
    }

    [Fact]
    public void Start_info_resumes_the_session_and_pins_a_model_alias()
    {
        var lane = Lane with { Model = "claude-code:opus" };
        var psi = ClaudeCodeNativeLaneRunner.BuildStartInfo(Path.Combine(_repo, "claude"), Turn(lane: lane), sessionId: "sess-123");
        var args = psi.ArgumentList.ToList();
        Assert.Equal("sess-123", args[args.IndexOf("--resume") + 1]);
        Assert.Equal("opus", args[args.IndexOf("--model") + 1]);
    }

    [WindowsFact]
    public async Task Windows_batch_shims_receive_every_argument_intact_when_the_shim_and_repository_paths_contain_spaces()
    {
        // An npm shim under a profile with a space (C:\Users\John Smith\AppData\Roaming\npm) and a repository with one.
        var npm = Path.Combine(_repo, "John Smith", "npm");
        var repository = Path.Combine(_repo, "my repo");
        Directory.CreateDirectory(npm);
        Directory.CreateDirectory(repository);
        // Like an npm shim it hands its arguments on; this one prints each, one per line, and answers --help.
        const string printArguments = "@echo off\r\nif \"%~1\"==\"--help\" echo usage: --restricted\r\n:next\r\nif \"%~1\"==\"\" goto :eof\r\necho [%~1]\r\nshift\r\ngoto next\r\n";
        var claude = Path.Combine(npm, "claude.cmd");
        var codex = Path.Combine(npm, "codex.cmd");
        File.WriteAllText(claude, printArguments);
        File.WriteAllText(codex, printArguments);
        var turn = new NativeLaneTurn("conv", "add tests", Lane, repository, "swarmcode-cli", false);
        var mcpConfig = Path.Combine(_repo, "John Smith", "mcp config.json");

        var claudeOutput = await RunAsync(ClaudeCodeNativeLaneRunner.BuildStartInfo(claude, turn, null, mcpConfig,
            NativeLaneContainment.WorkspaceConfined, restrictedSupported: true));
        Assert.Contains($"[{repository}]", claudeOutput);
        Assert.Contains($"[{mcpConfig}]", claudeOutput);
        Assert.Contains("[Bash,PowerShell,WebFetch,WebSearch,mcp__*]", claudeOutput);

        // An older CLI's inline settings JSON cannot pass through a shim (its quotes would be cmd syntax): it is
        // refused, and the runner hands the same settings over as a file instead.
        Assert.Throws<ArgumentException>(() => NativeLaneSupport.ForLaunch(ClaudeCodeNativeLaneRunner.BuildStartInfo(claude, turn, null, null,
            NativeLaneContainment.WorkspaceConfined, restrictedSupported: false)));
        var settings = Path.Combine(_repo, "John Smith", "settings file.json");
        var olderOutput = await RunAsync(ClaudeCodeNativeLaneRunner.BuildStartInfo(claude, turn, null, null,
            NativeLaneContainment.WorkspaceConfined, restrictedSupported: false, settingsPath: settings));
        Assert.Contains($"[--settings]{Environment.NewLine}[{settings}]", olderOutput);

        var codexTurn = turn with { Lane = new("codex", CodexNativeLaneRunner.ProviderId, "codex-cli:gpt-5.6-terra@high") };
        var codexOutput = await RunAsync(CodexNativeLaneRunner.BuildStartInfo(codex, codexTurn, null,
            new NativeAgentOptions { DefaultContainment = NativeLaneContainment.WorkspaceConfined },
            ignoreUserConfigSupported: true, ignoreRulesSupported: true, windows: true));
        Assert.Contains($"[{repository}]", codexOutput);
        Assert.Contains("[approval_policy='never']", codexOutput);
        Assert.Contains("[model_reasoning_effort='high']", codexOutput);

        // The capability probe launches the shim the same way.
        Assert.True(NativeCliCapabilities.Supports(claude, ClaudeCodeNativeLaneRunner.RestrictedFlag));
    }

    /// <summary>Starts a lane's launch the way the runners do (the batch bridge is applied at launch).</summary>
    private static async Task<string> RunAsync(System.Diagnostics.ProcessStartInfo psi)
    {
        using var process = System.Diagnostics.Process.Start(NativeLaneSupport.ForLaunch(psi))!;
        process.StandardInput.Close();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(process.ExitCode == 0, error);
        return output;
    }

    [Theory]
    [InlineData("claude-code:default", null)]
    [InlineData("claude-code:opus", "opus")]
    [InlineData("sonnet", "sonnet")]
    [InlineData("claude-code:", null)]
    public void Model_normalization(string model, string? expected) => Assert.Equal(expected, ClaudeCodeNativeLaneRunner.NormalizeModel(model));

    [Fact]
    public void First_turn_prompt_carries_harness_notes_and_agents_md_pointer_later_turns_are_plain()
    {
        File.WriteAllText(Path.Combine(_repo, "AGENTS.md"), "# rules");
        var first = ClaudeCodeNativeLaneRunner.BuildPrompt(Turn("do X"), isFirstTurn: true);
        Assert.StartsWith("Operating notes from the Talavine Code harness", first);
        Assert.Contains("AGENTS.md", first);
        Assert.Contains("without asking for confirmation", first);
        Assert.DoesNotContain("policy", first, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("do X", first);

        Assert.Equal("do X", ClaudeCodeNativeLaneRunner.BuildPrompt(Turn("do X"), isFirstTurn: false));
        Assert.StartsWith("Operating notes", ClaudeCodeNativeLaneRunner.BuildPrompt(Turn("do X", continuation: true), isFirstTurn: false));

        File.WriteAllText(Path.Combine(_repo, "CLAUDE.md"), "# claude rules");
        Assert.DoesNotContain("AGENTS.md", ClaudeCodeNativeLaneRunner.BuildPrompt(Turn("do X"), isFirstTurn: true));
    }

    [Fact]
    public void Native_lane_is_recognized_and_supported()
    {
        Assert.True(Lane.IsNative);
        var runner = new ClaudeCodeNativeLaneRunner(
            null!,
            null!,
            Microsoft.Extensions.Options.Options.Create(new CodingOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ClaudeCodeNativeLaneRunner>.Instance);
        Assert.True(runner.Supports(Lane));
        Assert.False(runner.Supports(new CodingLane("codex", "CodexCli", "codex-cli:default")));
    }
}
