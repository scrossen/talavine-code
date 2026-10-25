using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Continual;
using SwarmCore.Agent.ToolUseEngine;
using SwarmCore.Coding.Goals;
using SwarmCore.Coding.Native;
using SwarmCore.Missions;

namespace SwarmCore.Coding.Tests;

/// <summary>
/// The attempt-53 hold: five own-host <c>shell_exec</c> calls relayed by the selected native lane exited
/// non-zero, the bridge marked them errors without <c>LocalProcessCompleted</c>, and the goal parked in
/// Uncertain until an operator wrote proofs by hand. The host inspector settles exactly that shape from
/// the bridge's own completion envelope and nothing else.
/// </summary>
public sealed class OwnHostProcessCompletionInspectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 20, 47, 50, TimeSpan.Zero);
    private const string Attempt53Input = """{"server":"swarmcode","tool":"shell_exec","arguments":{"fileName":"git","argumentList":["check-ignore","-v","--non-matching","--","SwarmCode.Hosting/StarInvitationStore.cs","SwarmCode.App/Components/Shared/GitHubStarInvitation.razor"],"timeoutSeconds":15}}""";
    private const string Attempt53Stdout = "::\tSwarmCode.Hosting/StarInvitationStore.cs\n::\tSwarmCode.App/Components/Shared/GitHubStarInvitation.razor\n";

    /// <summary>The bridge serializes the tool's own output object; the test binds to that exact contract.</summary>
    internal static string Envelope(int exitCode = 1, string stdout = Attempt53Stdout, string stderr = "", bool timedOut = false)
        => JsonSerializer.Serialize(new ShellExecTool.Output { ExitCode = exitCode, Stdout = stdout, Stderr = stderr, TimedOut = timedOut },
            ToolUseAgentEngine.CreateToolJsonOptions());

    /// <summary>The codex app-server projection of the MCP result, exactly as the live receipt stored it.</summary>
    internal static string Relayed(string text, JsonNode? isError = null)
    {
        var wrapper = new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["structuredContent"] = null, ["_meta"] = null,
        };
        if (isError is not null) wrapper["isError"] = isError;
        return wrapper.ToJsonString();
    }

    internal static ContinualToolReceipt Receipt(string toolName = CodexNativeLaneRunner.McpToolName, string? inputJson = Attempt53Input,
        string? result = null, ContinualSideEffectClass effect = ContinualSideEffectClass.Exec, bool isError = true,
        ContinualToolReceiptState state = ContinualToolReceiptState.Completed, bool wasDispatched = true)
        => new(Guid.NewGuid(), 53, 8016, "exec-" + Guid.NewGuid().ToString("N"), toolName, state, effect, inputJson,
            result ?? Relayed(Envelope()), isError, Now.AddSeconds(-1), state == ContinualToolReceiptState.Completed ? Now : null)
        { WasDispatched = wasDispatched };

    [Fact]
    public void Attempt53_failed_own_host_shell_exec_relayed_by_the_native_lane_is_a_known_outcome()
    {
        var receipt = Receipt();
        // Without host proof this exact receipt holds the goal: dispatched, physical, error, no local completion claim.
        Assert.True(MissionTransitions.NeedsInspection(receipt));
        var inspector = new OwnHostProcessCompletionInspector();
        var outcome = inspector.Inspect(receipt);
        Assert.NotNull(outcome);
        Assert.Equal(ContinualReceiptResolutionOutcome.KnownOutcome, outcome.Outcome);
        Assert.Equal(OwnHostProcessCompletionInspector.InspectorIdentity, inspector.Identity);
        Assert.Contains("exit code 1", outcome.Evidence);
        Assert.Contains("timed out: false", outcome.Evidence);
        Assert.Contains($"recorded stdout {Attempt53Stdout.Length} characters", outcome.Evidence);
        Assert.Contains(ShellExecTool.ToolId, outcome.Evidence);
        // Evidence is host metadata: it never repeats command output or claims the command had no effect.
        Assert.DoesNotContain("StarInvitationStore", outcome.Evidence);
        Assert.DoesNotContain("no effect", outcome.Evidence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_inspector_never_dispatches_anything_and_honors_cancellation()
    {
        var inspector = new OwnHostProcessCompletionInspector();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inspector.InspectAsync(null!, Receipt(), cancelled.Token));
        var outcome = await inspector.InspectAsync(null!, Receipt());
        Assert.Equal(ContinualReceiptResolutionOutcome.KnownOutcome, outcome?.Outcome);
    }

    [Theory]
    [InlineData("mcp__swarmcode__shell_exec")]
    [InlineData(ShellExecTool.ToolId)]
    public void Claude_bridge_and_in_process_transports_store_the_bare_envelope(string toolName)
    {
        var receipt = Receipt(toolName, """{"command":"git check-ignore -v --non-matching -- a.cs","timeoutSeconds":15}""", Envelope());
        Assert.True(MissionTransitions.NeedsInspection(receipt));
        Assert.Equal(ContinualReceiptResolutionOutcome.KnownOutcome, new OwnHostProcessCompletionInspector().Inspect(receipt)?.Outcome);
        // The same transports never accept the relayed wrapper as their text, and the relay never accepts the bare envelope.
        Assert.Null(new OwnHostProcessCompletionInspector().Inspect(Receipt(toolName, "{}", Relayed(Envelope()))));
        Assert.Null(new OwnHostProcessCompletionInspector().Inspect(Receipt(result: Envelope())));
    }

    public static TheoryData<string, ContinualToolReceipt> Unresolvable => new()
    {
        { "timed-out", Receipt(result: Relayed(Envelope(timedOut: true))) },
        { "unobserved-exit", Receipt(result: Relayed(Envelope(exitCode: -1))) },
        { "foreign-server", Receipt(inputJson: Attempt53Input.Replace("\"swarmcode\"", "\"other\"")) },
        { "server-case", Receipt(inputJson: Attempt53Input.Replace("\"swarmcode\"", "\"SWARMCODE\"")) },
        { "foreign-tool", Receipt(inputJson: Attempt53Input.Replace("shell_exec", "http_request")) },
        { "no-arguments", Receipt(inputJson: """{"server":"swarmcode","tool":"shell_exec"}""") },
        { "model-authored-lookalike", Receipt("codex.command", """{"command":"git check-ignore -v -- a.cs"}""", Relayed(Envelope())) },
        { "claude-native-bash-lookalike", Receipt("Bash", """{"command":"git check-ignore -v -- a.cs"}""", Envelope()) },
        { "prose-around-envelope", Receipt(result: "The command failed: " + Relayed(Envelope())) },
        { "external-send", Receipt(effect: ContinualSideEffectClass.ExternalSend) },
        { "destructive", Receipt(effect: ContinualSideEffectClass.Destructive) },
        { "unclassified", Receipt(effect: ContinualSideEffectClass.Unknown) },
        { "started-without-result", Receipt(result: "", state: ContinualToolReceiptState.Started) },
        { "not-dispatched", Receipt(wasDispatched: false) },
        { "extra-envelope-member", Receipt(result: Relayed("""{"ExitCode":1,"Stdout":"","Stderr":"","TimedOut":false,"LocalProcessCompleted":true}""")) },
        { "missing-timed-out", Receipt(result: Relayed("""{"ExitCode":1,"Stdout":"","Stderr":""}""")) },
        { "camel-case-envelope", Receipt(result: Relayed("""{"exitCode":1,"stdout":"","stderr":"","timedOut":false}""")) },
        { "string-exit-code", Receipt(result: Relayed("""{"ExitCode":"1","Stdout":"","Stderr":"","TimedOut":false}""")) },
        { "string-timed-out", Receipt(result: Relayed("""{"ExitCode":1,"Stdout":"","Stderr":"","TimedOut":"false"}""")) },
        { "two-content-parts", Receipt(result: """{"content":[{"type":"text","text":"x"},{"type":"text","text":"y"}]}""") },
        { "image-content", Receipt(result: """{"content":[{"type":"image","data":"AA==","mimeType":"image/png"}]}""") },
        { "string-error-flag", Receipt(result: Relayed(Envelope(), JsonValue.Create("true"))) },
        { "error-object", Receipt(result: """{"error":{"code":-32000,"message":"{\"ExitCode\":1,\"Stdout\":\"\",\"Stderr\":\"\",\"TimedOut\":false}"}}""") },
        { "not-json", Receipt(result: "ExitCode 1 TimedOut false") },
        { "relayed-refusal-text", Receipt(result: Relayed("Unknown Talavine Code tool shell_exec.")) },
        { "relayed-pre-launch-exception", Receipt(result: Relayed("ArgumentException: Invalid or reserved command environment override.")) },
        { "empty-input", Receipt(inputJson: null) },
    };

    [Theory]
    [MemberData(nameof(Unresolvable))]
    public void Missing_host_evidence_leaves_the_receipt_unresolved(string scenario, ContinualToolReceipt receipt)
    {
        Assert.NotNull(scenario);
        Assert.Null(new OwnHostProcessCompletionInspector().Inspect(receipt));
    }

    public static TheoryData<string, string, int, string> Legitimate => new()
    {
        { "error-flag-true", Relayed(Envelope(), JsonValue.Create(true)), 1, "recorded stdout " + Attempt53Stdout.Length },
        { "error-flag-null", Relayed(Envelope()).Replace("\"_meta\":null", "\"_meta\":null,\"isError\":null"), 1, "exit code 1" },
        { "quotes-backslashes-unicode", Relayed(Envelope(stdout: "say \"hi\" C:\\tmp é — 日本\n")), 1, "recorded stdout " + "say \"hi\" C:\\tmp é — 日本\n".Length },
        { "windows-ntstatus-exit", Relayed(Envelope(exitCode: -1073741510)), -1073741510, "exit code -1073741510" },
    };

    [Theory]
    [MemberData(nameof(Legitimate))]
    public void Projection_variants_the_bridge_can_produce_are_still_recognized(string scenario, string result, int exitCode, string expectedEvidence)
    {
        Assert.NotNull(scenario);
        var outcome = new OwnHostProcessCompletionInspector().Inspect(Receipt(result: result));
        Assert.Equal(ContinualReceiptResolutionOutcome.KnownOutcome, outcome?.Outcome);
        Assert.Contains(expectedEvidence, outcome!.Evidence);
        Assert.Contains($"exit code {exitCode}", outcome.Evidence);
    }

    [Fact]
    public void Identity_needs_the_tool_served_uniquely_by_the_current_catalog()
    {
        var relayed = Receipt();
        var bridged = Receipt("mcp__swarmcode__shell_exec", """{"command":"true"}""", Envelope());
        var inProcess = Receipt(ShellExecTool.ToolId, """{"command":"true"}""", Envelope());
        var collision = new InMemoryCapabilityCatalog();
        collision.Register(ToolDescriptor.Create<ShellExecTool.Input, ShellExecTool.Output>(ShellExecTool.ToolId, "primitive", "Own shell tool",
            ToolCostClass.External, ToolSideEffect.Exec, (_, _) => throw new InvalidOperationException("never invoked")));
        collision.Register(ToolDescriptor.Create<ShellExecTool.Input, string>("shell-exec", "other", "Collides after MCP normalization",
            ToolCostClass.Cheap, ToolSideEffect.Exec, (_, _) => Task.FromResult("not invoked")));
        Assert.Null(new OwnHostProcessCompletionInspector(collision).Inspect(relayed));
        Assert.Null(new OwnHostProcessCompletionInspector(collision).Inspect(bridged));

        var own = new InMemoryCapabilityCatalog();
        own.Register(ToolDescriptor.Create<ShellExecTool.Input, ShellExecTool.Output>(ShellExecTool.ToolId, "primitive", "Own shell tool",
            ToolCostClass.External, ToolSideEffect.Exec, (_, _) => throw new InvalidOperationException("never invoked")));
        Assert.Equal(ContinualReceiptResolutionOutcome.KnownOutcome, new OwnHostProcessCompletionInspector(own).Inspect(relayed)?.Outcome);
        Assert.Equal(ContinualReceiptResolutionOutcome.KnownOutcome, new OwnHostProcessCompletionInspector(own).Inspect(bridged)?.Outcome);

        // With no catalog the transport-reserved names stand alone; with one, a bridge name the host does not serve cannot have come
        // from this host's bridge. The in-process engine names the tool by its exact id, which needs no catalog mapping.
        var empty = new InMemoryCapabilityCatalog();
        Assert.Equal(ContinualReceiptResolutionOutcome.KnownOutcome, new OwnHostProcessCompletionInspector().Inspect(relayed)?.Outcome);
        Assert.Null(new OwnHostProcessCompletionInspector(empty).Inspect(relayed));
        Assert.Null(new OwnHostProcessCompletionInspector(empty).Inspect(bridged));
        Assert.Equal(ContinualReceiptResolutionOutcome.KnownOutcome, new OwnHostProcessCompletionInspector(empty).Inspect(inProcess)?.Outcome);
    }

    [Fact]
    public async Task The_real_shell_tool_outside_a_mission_fence_still_produces_the_held_shape_that_only_this_inspector_settles()
    {
        // Decision record: ShellExecTool keeps LocalProcessCompleted false outside a mission fence because that flag
        // promises replay safety an unfenced command cannot give. The hold therefore still exists at the source and
        // the host inspector, not a relaxed receipt, is what clears it.
        var root = Path.Combine(Path.GetTempPath(), "own-host-process-inspector-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var services = new ServiceCollection().BuildServiceProvider();
            var tool = new ShellExecTool(new SandboxPathResolver(Options.Create(new SandboxOptions { Root = root })), services);
            var input = OperatingSystem.IsWindows()
                ? new ShellExecTool.Input { Command = "echo held & exit /b 3", TimeoutSeconds = 30 }
                : new ShellExecTool.Input { Command = "echo held; exit 3", TimeoutSeconds = 30 };
            var output = Assert.IsType<ShellExecTool.Output>(await tool.Descriptor.Invoke(input, CancellationToken.None));
            Assert.Equal(3, output.ExitCode);
            Assert.False(output.TimedOut);
            Assert.False(output.LocalProcessCompleted);
            var text = JsonSerializer.Serialize(output, ToolUseAgentEngine.CreateToolJsonOptions());
            var receipt = Receipt(result: Relayed(text));
            Assert.True(MissionTransitions.NeedsInspection(receipt));
            var outcome = new OwnHostProcessCompletionInspector().Inspect(receipt);
            Assert.Equal(ContinualReceiptResolutionOutcome.KnownOutcome, outcome?.Outcome);
            Assert.Contains("exit code 3", outcome!.Evidence);
            Assert.DoesNotContain("held", outcome.Evidence);
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}
