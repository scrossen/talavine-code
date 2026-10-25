using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Continual;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Native;
using SwarmCore.Coding.Tools;
using SwarmCore.Missions;

namespace SwarmCore.Coding.Tests;

public sealed class ReadJsonToolTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("structured-json-read-");
    private SandboxPathResolver Sandbox => new(Options.Create(new SandboxOptions { Root = _root.FullName }));
    public void Dispose() { try { _root.Delete(true); } catch { } }
    private Task Write(string json) => File.WriteAllTextAsync(Path.Combine(_root.FullName, "data.json"), json);
    private async Task<ReadJsonTool.Output> Read(ReadJsonTool.Input input)
        => Assert.IsType<ReadJsonTool.Output>(await new ReadJsonTool(Sandbox).Descriptor.Invoke(input, default));

    [Fact]
    public async Task Mixed_array_projection_is_shape_safe_and_preserves_escaped_pointer_and_missing_field_identity()
    {
        await Write("""{"items":[{"title":"one","a/b":{"~tag":3}},"two",null,4,[7],{"title":"last"}]}""");
        var result = await Read(new() { Path = "data.json", Pointer = "/items", Fields = ["/title", "/a~1b/~0tag"] });
        Assert.True(result.Succeeded); Assert.False(result.Truncated); Assert.Equal(6, result.ReturnedItems);
        Assert.Equal("one", result.Values[0]!["fields"]!["/title"]!.GetValue<string>());
        Assert.Equal(3, result.Values[0]!["fields"]!["/a~1b/~0tag"]!.GetValue<int>());
        Assert.Equal("string", result.Values[1]!["sourceKind"]!.GetValue<string>());
        Assert.Equal(2, result.Values[1]!["missingFields"]!.AsArray().Count);
        Assert.Equal("null", result.Values[2]!["sourceKind"]!.GetValue<string>());
        Assert.Equal("last", result.Values[5]!["fields"]!["/title"]!.GetValue<string>());
        var bytes = await File.ReadAllBytesAsync(Path.Combine(_root.FullName, "data.json"));
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), result.Sha256);
        Assert.Equal("last", (await Read(new() { Path = "data.json", Pointer = "/items/5/title" })).Values[0]!.GetValue<string>());
    }

    [Fact]
    public async Task Output_item_byte_and_depth_bounds_return_valid_small_json_and_do_not_modify_the_file()
    {
        var source = JsonSerializer.Serialize(Enumerable.Range(0, 100).Select(n => new { number = n, text = new string('☃', 1000) }));
        await Write(source);
        var result = await Read(new() { Path = "data.json", MaxItems = 3, MaxOutputChars = 2048 });
        Assert.True(result.Succeeded); Assert.True(result.Truncated);
        Assert.True(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length <= 2048);
        result = await Read(new() { Path = "data.json", MaxBytes = 4 });
        Assert.False(result.Succeeded); Assert.Equal("file-too-large", result.Error); Assert.Null(result.Sha256);
        Assert.Equal(source, await File.ReadAllTextAsync(Path.Combine(_root.FullName, "data.json")));
        await Write(new string('[', 49) + "0" + new string(']', 49));
        Assert.Equal("invalid-json", (await Read(new() { Path = "data.json" })).Error);
    }

    [Theory]
    [InlineData("[", "", "invalid-json")]
    [InlineData("{\"items\":[\"one\"]}", "/items/0/title", "pointer-not-found")]
    [InlineData("{\"items\":[1]}", "/items/01", "pointer-not-found")]
    [InlineData("{}", "/bad~2escape", "invalid-projection")]
    [InlineData("{}", "items", "invalid-projection")]
    public async Task Invalid_json_and_shape_or_pointer_mismatches_are_typed_read_failures(string source, string pointer, string error)
    {
        await Write(source); var result = await Read(new() { Path = "data.json", Pointer = pointer });
        Assert.False(result.Succeeded); Assert.Equal(error, result.Error);
        Assert.Equal(source, await File.ReadAllTextAsync(Path.Combine(_root.FullName, "data.json")));
    }

    [Fact]
    public async Task Existing_workspace_and_physical_symlink_boundaries_apply_before_reading()
    {
        using var outside = new DisposableDirectory();
        var target = Path.Combine(outside.Path, "private.json"); await File.WriteAllTextAsync(target, "{\"private\":true}");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Read(new() { Path = target }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Read(new() { Path = "../private.json" }));
        if (!OperatingSystem.IsWindows())
        {
            File.CreateSymbolicLink(Path.Combine(_root.FullName, "jump.json"), target);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Read(new() { Path = "jump.json" }));
        }
        Assert.Equal("{\"private\":true}", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task Missing_invalid_path_encoding_and_cancellation_have_no_mutation_or_unclassified_execution()
    {
        Assert.Equal("file-not-found", (await Read(new() { Path = "missing.json" })).Error);
        Assert.Equal("invalid-path", (await Read(new() { Path = null! })).Error);
        var longPath = new string('☃', 5000);
        var invalid = await Read(new() { Path = longPath, MaxOutputChars = 2048 });
        Assert.Equal("invalid-path", invalid.Error);
        Assert.True(JsonSerializer.Serialize(invalid, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length <= 2048);
        await File.WriteAllBytesAsync(Path.Combine(_root.FullName, "data.json"), [0xff, 0xff, 0xff, 0xff]);
        Assert.Equal("unsupported-encoding", (await Read(new() { Path = "data.json" })).Error);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ReadJsonTool(Sandbox).Descriptor.Invoke(
            new ReadJsonTool.Input { Path = "data.json" }, stop.Token));
        Assert.Equal([0xff, 0xff, 0xff, 0xff], await File.ReadAllBytesAsync(Path.Combine(_root.FullName, "data.json")));
    }

    [Fact]
    public async Task Current_mission_resource_denial_and_revoked_execution_fence_prevent_reads()
    {
        await Write("{\"allowed\":true}"); using var outside = new DisposableDirectory();
        var context = new AsyncLocalCurrentTurnContextAccessor(); var profile = Guid.NewGuid();
        context.Current = new("json-fenced", Guid.NewGuid(), profile, CommunicationScopeUserInitiated: true)
        {
            MissionObjectiveId = Guid.NewGuid(), ValidateMissionExecution = _ => Task.CompletedTask,
            MissionResources = MissionResourceAuthority.LocalWorkspace(profile, "owner", "fixture", outside.Path),
        };
        try
        {
            await Assert.ThrowsAsync<MissionActionSkippedException>(() => Read(new() { Path = "data.json" }));
            context.Current = context.Current with
            {
                MissionResources = MissionResourceAuthority.LocalWorkspace(profile, "owner", "fixture", _root.FullName),
                ValidateMissionExecution = _ => throw new OperationCanceledException("Synthetic lease revoked."),
            };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read(new() { Path = "data.json" }));
        }
        finally { context.Current = null; }
    }

    private sealed class DisposableDirectory : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("structured-json-outside-");
        public string Path => _directory.FullName;
        public void Dispose() { try { _directory.Delete(true); } catch { } }
    }

    private sealed class BridgeHost(IReadOnlyList<ToolDescriptor> tools) : IHostToolBridgeHost, IHostToolBridgeAttemptHost
    {
        public ToolSideEffect? GuardEffect;
        public HostToolProfile? Admit(string id) => HostToolProfile.Plan;
        public Task<IReadOnlyList<ToolDescriptor>> ToolsAsync(string id, CancellationToken ct) => Task.FromResult(tools);
        public Task<ToolGuardDecision> InspectAsync(ToolInvocationRequest request, CancellationToken ct)
        { GuardEffect = request.EffectiveSideEffect; return Task.FromResult(ToolGuardDecision.Allow()); }
        public IDisposable BeginCall(string id) => new Scope();
        public IDisposable? BeginAttempt(string id, string? capability) => capability == "fixture-attempt" ? new Scope() : null;
        public bool IsAttemptCurrent(string id) => true;
        public CancellationToken AttemptCancellation(string id) => default;
        private sealed class Scope : IDisposable { public void Dispose() { } }
    }

    [Fact]
    public async Task Actual_native_bridge_and_owning_receipt_evidence_keep_malformed_json_read_only()
    {
        await Write("{ malformed JSON");
        var read = new ReadJsonTool(Sandbox).Descriptor;
        var tools = new[] { read, new ReadFileTool(Sandbox).Descriptor, new ListFilesTool(Sandbox).Descriptor,
            new GrepTool(Sandbox).Descriptor, new GlobTool(Sandbox).Descriptor };
        var selected = HostToolBridgeCatalog.Select(tools, HostToolProfile.Plan);
        Assert.Equal(["code_glob", "code_grep", "fs_list_files", "fs_read_file", "fs_read_json"], selected.Keys.Order());
        var host = new BridgeHost(tools); await using var broker = new HostToolBridgeBroker(host);
        var id = Guid.NewGuid().ToString("N"); const string capability = "fixture-attempt";
        using var journal = NativeHostReceiptJournal.Begin(id, capability);
        var input = new JsonObject { ["path"] = "data.json" };
        var result = await new HostToolBridgeProxy(broker.GetEndpoint(), id, capability)
            .CallAsync("fs_read_json", JsonSerializer.SerializeToElement(input), default);
        Assert.True(result.IsError); Assert.True(result.WasDispatched);
        Assert.Equal(ToolSideEffect.ReadOnly, host.GuardEffect);
        Assert.Equal("invalid-json", JsonSerializer.Deserialize<ReadJsonTool.Output>(result.Text,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Error);
        var wire = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = result.Text }), ["isError"] = true };
        Assert.True(NativeHostReceiptJournal.TryTake(id, capability, "fs_read_json", input, wire, out var evidence));
        Assert.Equal(ToolSideEffect.ReadOnly, evidence!.SideEffect);
        var catalog = new InMemoryCapabilityCatalog(); foreach (var tool in tools) catalog.Register(tool);
        var classifier = new NativeHostToolReceiptClassifier(catalog);
        var nativeInput = new JsonObject { ["server"] = "swarmcode", ["tool"] = "fs_read_json", ["arguments"] = input.DeepClone() };
        Assert.Equal(ToolSideEffect.ReadOnly, classifier.Resolve(CodexNativeLaneRunner.McpToolName, nativeInput));
        Assert.Equal(ToolSideEffect.ReadOnly, classifier.Resolve("mcp__swarmcode__code_grep", new JsonObject { ["pattern"] = "missing", ["path"] = "missing.txt" }));
        Assert.Equal(ToolSideEffect.ReadOnly, classifier.Resolve("mcp__swarmcode__code_glob", new JsonObject { ["pattern"] = "**/*.json" }));
        var receipt = new ContinualToolReceipt(Guid.NewGuid(), 1, 1, "read-failed", CodexNativeLaneRunner.McpToolName,
            ContinualToolReceiptState.Completed, ContinualSideEffectClass.ReadOnly, nativeInput.ToJsonString(), result.Text,
            true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Assert.False(MissionTransitions.NeedsInspection(receipt));
        // A foreign/raw command is never inferred to be read-only from its text or failed exit.
        Assert.Null(classifier.Resolve("exec", new JsonObject { ["cmd"] = "python3 data-inspection.py" }));
    }
}
