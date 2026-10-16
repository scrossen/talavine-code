using SwarmCore.Abstractions.Compute;
using SwarmCore.Coding.Peers;

namespace SwarmCore.Coding.Tests;

public class PeerRoutingTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    internal static PeerCapabilities Caps(string id = "device") => new()
    {
        DeviceId = id, DisplayName = id, Platform = "windows", Architecture = "arm64", AgentBridgeEnabled = true,
        GeneratedUtcMs = Now.ToUnixTimeMilliseconds(), LaneVersion = 2, DefaultProviderId = "cloud",
        Hardware = new() { MeasuredUtcMs = Now.ToUnixTimeMilliseconds(), LogicalProcessors = 16, TotalMemoryBytes = 64L << 30,
            Gpus = [new("NVIDIA Example GPU", 24L << 30)] },
        Providers = [new("cloud", "Cloud", "command-line", true, "ready", "cloud-model", ["cloud-model"], ["headless", "persistent"]),
            new("local", "Local", "ollama", true, "ready", "coder:30b", ["coder:30b"], ["headless"], true, ["coder:30b"])],
        Jobs = [new("build", true, "ready")],
    };
    private static PeerCandidate Match(PeerRequirements requirements, PeerCapabilities? caps = null)
        => PeerRouting.Match(new("device", "Device", "windows", false), caps ?? Caps(), requirements, Now);

    [Fact]
    public void Combined_hardware_and_local_model_constraints_select_the_local_runtime_even_with_a_stale_discovery_beacon()
    {
        var result = Match(new(Platform: "Windows", Architecture: "arm64", MinimumLogicalProcessors: 8,
            MinimumMemoryBytes: 32L << 30, RequireGpu: true, GpuName: "NVIDIA", MinimumGpuMemoryBytes: 16L << 30,
            RequireLocalInference: true, Model: "coder:30b"));
        Assert.True(result.Matches, result.Reason);
        Assert.Equal("local", result.Provider);
        Assert.Equal("coder:30b", result.Model);
    }

    [Theory]
    [InlineData("os")]
    [InlineData("architecture")]
    [InlineData("memory")]
    [InlineData("cores")]
    [InlineData("gpu")]
    [InlineData("vram")]
    [InlineData("model")]
    [InlineData("provider")]
    [InlineData("mode")]
    public void Incompatible_constraints_are_explained_without_fallback(string mismatch)
    {
        var r = mismatch switch
        {
            "os" => new PeerRequirements(Platform: "macos"),
            "architecture" => new(Architecture: "x64"),
            "memory" => new(MinimumMemoryBytes: 128L << 30),
            "cores" => new(MinimumLogicalProcessors: 32),
            "gpu" => new(GpuName: "AMD"),
            "vram" => new(MinimumGpuMemoryBytes: 32L << 30),
            "model" => new(Model: "missing"),
            "provider" => new(Provider: "missing"),
            _ => new(ExecutionMode: "visible"),
        };
        var result = Match(r);
        Assert.False(result.Matches);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    [Fact]
    public void Old_peers_can_run_unconstrained_work_but_cannot_claim_unknown_hardware_or_local_execution()
    {
        var caps = Caps() with { Hardware = null, Providers = [Caps().Providers[0] with { Kind = "ollama", LocalInference = false }] };
        Assert.True(Match(new(), caps).Matches);
        Assert.False(Match(new(RequireGpu: true), caps).Matches);
        Assert.False(Match(new(MinimumMemoryBytes: 1), caps).Matches);
        Assert.False(Match(new(RequireLocalInference: true), caps).Matches);
    }

    [Fact]
    public void Unified_memory_does_not_satisfy_dedicated_vram()
    {
        var caps = Caps();
        caps = caps with { Hardware = caps.Hardware! with { Gpus = [new("Apple Example", UnifiedMemory: true)] } };
        Assert.True(Match(new(RequireGpu: true), caps).Matches);
        Assert.False(Match(new(MinimumGpuMemoryBytes: 1), caps).Matches);
    }

    [Fact]
    public void Localhost_runtime_with_cloud_models_only_routes_confirmed_on_device_models_for_local_work()
    {
        var local = Caps().Providers[1] with { DefaultModel = "alias", Models = ["alias", "coder:30b"] };
        var caps = Caps() with { Providers = [local] };
        Assert.Equal("coder:30b", Match(new(RequireLocalInference: true), caps).Model);
        Assert.False(Match(new(RequireLocalInference: true, Model: "alias"), caps).Matches);
    }

    [Fact]
    public void Stale_identity_disabled_bridge_and_stale_hardware_cannot_route()
    {
        Assert.False(Match(new(), Caps() with { DeviceId = "other" }).Matches);
        Assert.False(Match(new(), Caps() with { GeneratedUtcMs = Now.AddMinutes(-3).ToUnixTimeMilliseconds() }).Matches);
        Assert.False(Match(new(), Caps() with { AgentBridgeEnabled = false }).Matches);
        Assert.False(Match(new(), Caps() with { LaneVersion = 0 }).Matches);
        Assert.False(Match(new(RequireGpu: true), Caps() with { Hardware = Caps().Hardware! with { MeasuredUtcMs = 1 } }).Matches);
    }

    [Fact]
    public void Jobs_use_only_receiver_advertised_availability_and_need_no_provider()
    {
        Assert.True(Match(new(ExecutionMode: "job", Job: "build"), Caps() with { Providers = [] }).Matches);
        Assert.False(Match(new(ExecutionMode: "job", Job: "restricted"), Caps() with { Jobs = [new("restricted", false, "peer-not-allowed")] }).Matches);
        Assert.False(Match(new(ExecutionMode: "job", Job: "build", Model: "coder:30b")).Matches);
    }

    [Fact]
    public async Task Coordinator_balances_work_rechecks_capabilities_and_scopes_status_and_cancellation()
    {
        var bridge = new Bridge();
        bridge.Work.Add(new() { WorkId = "busy", ConversationId = "other", PeerDeviceId = "a" });
        var coordinator = new PeerCoordinator(bridge);
        var handle = await coordinator.DelegateAsync("parent", new("Test", "Run checks", ["Checks pass"]));
        Assert.Equal("b", bridge.Assignment!.PeerDeviceId);
        Assert.Equal("parent", bridge.Assignment.ConversationId);
        Assert.Equal(2, bridge.Queries["b"]);
        Assert.Single(await coordinator.WorkAsync("parent"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ObserveAsync("parent", new("busy"), false, true));
        Assert.Equal(0, bridge.Cancels);
        bridge.Work[1] = bridge.Work[1] with { UpdatedUtcMs = 42 };
        await coordinator.ObserveAsync("parent", new(handle.WorkId, TimeoutSeconds: 999), true, false);
        Assert.Equal((42L, 50), bridge.LastWait);
        bridge.Work[1] = bridge.Work[1] with { IsTerminal = true, LastReplyText = "verified" };
        Assert.Equal("verified", (await coordinator.ObserveAsync("parent", new(handle.WorkId), true, false)).LastReplyText);
    }

    [Fact]
    public async Task Changed_capability_before_dispatch_stops_the_assignment()
    {
        var bridge = new Bridge { ChangeOnRecheck = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PeerCoordinator(bridge).DelegateAsync("parent", new("Test", "Run checks", ["Pass"])));
        Assert.Null(bridge.Assignment);
    }

    [Fact]
    public async Task Lost_dispatch_reply_is_not_retried()
    {
        var bridge = new Bridge { LoseReply = true };
        var coordinator = new PeerCoordinator(bridge);
        await Assert.ThrowsAsync<HttpRequestException>(() => coordinator.DelegateAsync("parent", new("Test", "Run checks", ["Pass"])));
        Assert.Equal(1, bridge.Dispatches);
        Assert.Single(await coordinator.WorkAsync("parent"));
    }

    private sealed class Bridge : IPeerWorkBridge
    {
        public Dictionary<string, int> Queries = [];
        public PeerAssignment? Assignment;
        public List<PeerWork> Work = [];
        public int Cancels, Dispatches;
        public bool ChangeOnRecheck, LoseReply;
        public (long, int) LastWait;
        public Task<IReadOnlyList<PeerIdentity>> ListPeersAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<PeerIdentity>>([new("a", "A", "windows", true), new("b", "B", "windows", true)]);
        public Task<PeerCapabilities> CapabilitiesAsync(string id, CancellationToken ct)
        {
            Queries[id] = Queries.GetValueOrDefault(id) + 1;
            return Task.FromResult(Caps(id) with { AgentBridgeEnabled = !ChangeOnRecheck || Queries[id] == 1 });
        }
        public Task<PeerWorkHandle> AssignAsync(PeerAssignment a, CancellationToken ct)
        {
            Dispatches++; Assignment = a;
            Work.Add(new() { WorkId = "assigned", ConversationId = a.ConversationId, PeerDeviceId = a.PeerDeviceId });
            if (LoseReply) throw new HttpRequestException("Connection lost after commit");
            return Task.FromResult(new PeerWorkHandle("assigned", a.ConversationId, "message"));
        }
        public Task<IReadOnlyList<PeerWork>> ListWorkAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<PeerWork>>(Work);
        public Task<PeerWork> WorkAsync(string id, CancellationToken ct) => Task.FromResult(Work.Single(w => w.WorkId == id));
        public Task<PeerWork> WaitAsync(string id, long since, int seconds, CancellationToken ct) { LastWait = (since, seconds); return WorkAsync(id, ct); }
        public Task<PeerWork> CancelAsync(string id, CancellationToken ct) { Cancels++; return WorkAsync(id, ct); }
    }
}
