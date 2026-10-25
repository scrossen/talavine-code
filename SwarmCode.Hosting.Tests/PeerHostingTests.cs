using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SwarmCode.Hosting.Peers;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Native;
using SwarmCore.Coding.Peers;

namespace SwarmCode.Hosting.Tests;

public class PeerHostingTests : TempDirTest
{
    [Fact]
    public async Task Native_peer_tools_dispatch_follow_and_collect_under_the_trusted_parent()
    {
        var transport = new AssignmentBridge();
        await using var host = await BridgedWorkspace.StartAsync(Paths, Path.Combine(Tmp, "repo"),
            configure: services => services.AddSingleton<IPeerWorkBridge>(transport));
        var native = host.Turn("parent");
        var dispatched = await native.CallAsync("peers_delegate", BridgedWorkspace.Args(new
        {
            title = "Platform checks", goal = "Run the Windows checks on the prepared source revision", successCriteria = new[] { "All checks pass" },
            requirements = new { platform = "windows", model = "coder" },
        }), default);
        using var assigned = JsonDocument.Parse(dispatched.Text);
        Assert.True(assigned.RootElement.GetProperty("success").GetBoolean(), dispatched.Text);
        var workId = assigned.RootElement.GetProperty("result").GetProperty("workId").GetString()!;
        Assert.StartsWith("swarmcode-", transport.Assignment!.ConversationId);
        Assert.NotEqual("parent", transport.Assignment.ConversationId);
        Assert.Equal("local", transport.Assignment.Provider);
        Assert.Equal("coder", transport.Assignment.Model);

        var foreign = await host.Turn("other").CallAsync("peers_wait", BridgedWorkspace.Args(new { workId }), default);
        Assert.Contains("another conversation", foreign.Text);
        Assert.Equal(0, transport.Waits);
        var completed = await native.CallAsync("peers_wait", BridgedWorkspace.Args(new { workId }), default);
        Assert.Contains("Checks passed", completed.Text);
        Assert.Contains("checks.json", completed.Text);
        Assert.Equal(1, transport.Waits);
        var recovered = await native.CallAsync("peers_work", BridgedWorkspace.Args(new { }), default);
        Assert.Contains(workId, recovered.Text);
        Assert.Contains("Checks passed", recovered.Text);
    }

    [Fact]
    public async Task Both_lane_types_receive_peer_tools_with_host_authority_and_plan_mode_filters()
    {
        var services = new ServiceCollection().AddLogging().AddSwarmCodeHost(new() { RepositoryRoot = Tmp, DataPaths = Paths });
        await using var sp = services.BuildServiceProvider();
        foreach (var service in sp.GetServices<IHostedService>().Where(s => s is PeerToolRegistration || s.GetType().Name is "PeerFilesRegistration" or "PeerCollaborationRegistration")) await service.StartAsync(default);
        var catalog = sp.GetRequiredService<ICapabilityCatalog>();
        var profile = await sp.GetRequiredService<IProfileResolver>().ResolveAsync("parent");
        var tools = profile.Tools.Where(t => t.ToolId.StartsWith("peers.")).ToArray();
        Assert.Equal(12, tools.Length);
        Assert.Contains("peers_delegate", HostToolBridgeCatalog.Select(tools, HostToolProfile.Full).Keys);
        Assert.DoesNotContain("peers_delegate", HostToolBridgeCatalog.Select(tools, HostToolProfile.Plan).Keys);
        Assert.DoesNotContain("peers_send_file", HostToolBridgeCatalog.Select(tools, HostToolProfile.Plan).Keys);
        Assert.DoesNotContain("peers_screen", HostToolBridgeCatalog.Select(tools, HostToolProfile.Plan).Keys);
        Assert.DoesNotContain("peers_message", HostToolBridgeCatalog.Select(tools, HostToolProfile.Plan).Keys);
        Assert.DoesNotContain("peers_inbox", HostToolBridgeCatalog.Select(tools, HostToolProfile.Plan).Keys);
        Assert.Contains("peers_screen", HostToolBridgeCatalog.Select(tools, HostToolProfile.Full).Keys);
        var scope = sp.GetRequiredService<PeerToolScope>();
        var turn = new CurrentTurnContext("parent", null, null, CommunicationScopeUserInitiated: true);
        Assert.True(scope.CanDelegate(turn));
        Assert.False(scope.CanDelegate(turn with { UnattendedAgentTurn = true }));
        Assert.False(scope.CanDelegate(turn with { CommunicationScopeUserInitiated = false }));
        using (sp.GetRequiredService<SwarmCore.Coding.ActiveCodingWorkspaces>().Enter("parent", new() { RepositoryRoot = Tmp }, unattended: true))
            Assert.False(scope.CanDelegate(turn)); // Native bridge ambient state cannot erase a scheduled origin.
        Assert.NotEqual(scope.ConversationId(turn), scope.ConversationId(turn with { ConversationId = "other" }));
        var denied = await tools.Single(t => t.ToolId == "peers.delegate").Invoke(new PeerDelegateInput("Test", "Test", ["Pass"]), default);
        Assert.Contains("requires a conversation", denied?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Bridge_reads_current_token_each_call_and_deserializes_real_wire_names_without_exposing_secrets()
    {
        var tokenPath = Path.Combine(Tmp, "control-token.json");
        var handler = new Handler();
        using var http = new HttpClient(handler);
        var bridge = new SwarmSpanPeerBridge(http, () => tokenPath);
        await File.WriteAllTextAsync(tokenPath, "{\"port\":12345,\"token\":\"first-token\"}");
        handler.Response = "{\"deviceId\":\"peer\",\"hardware\":{\"totalMemoryBytes\":68719476736,\"gpus\":[{\"name\":\"GPU\",\"dedicatedMemoryBytes\":25769803776}]},\"providers\":[{\"providerId\":\"local\",\"localInference\":true}],\"jobs\":[{\"jobId\":\"build\",\"available\":true}]}";
        var caps = await bridge.CapabilitiesAsync("peer", default);
        Assert.Equal(64L << 30, caps.Hardware!.TotalMemoryBytes);
        Assert.Equal("build", caps.Jobs.Single().JobId);
        Assert.True(caps.Providers.Single().LocalInference);
        Assert.Equal("first-token", handler.Bearer);
        Assert.Equal("127.0.0.1", handler.Uri!.Host);
        await File.WriteAllTextAsync(tokenPath, "{\"port\":12346,\"token\":\"second-token\"}");
        await bridge.CapabilitiesAsync("peer", default);
        Assert.Equal("second-token", handler.Bearer);
        Assert.Equal(12346, handler.Uri!.Port);
        handler.Response = "{\"workId\":\"build\",\"status\":\"Completed\",\"isTerminal\":true,\"goal\":\"Build the project\",\"successCriteria\":[\"Native tests pass\"],\"expectedArtifacts\":[\"installer.exe\"],\"lastReplyText\":\"The detached build is still running.\",\"artifacts\":[]}";
        var work = await bridge.WorkAsync("build", default);
        Assert.Equal("Build the project", work.Goal);
        Assert.Contains("Native tests pass", work.SuccessCriteria);
        Assert.Contains("installer.exe", work.ExpectedArtifacts);
        Assert.NotNull(work.CompletionNotice);
        Assert.Contains("detached process", work.CompletionNotice);
        handler.Response = "{\"error\":\"second-token\"}";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.CapabilitiesAsync("peer", default));
        Assert.DoesNotContain("second-token", error.Message);
    }

    [Fact]
    public async Task Collaboration_calls_use_the_real_control_routes_and_wire_names()
    {
        var tokenPath = Path.Combine(Tmp, "control-token.json");
        await File.WriteAllTextAsync(tokenPath, "{\"port\":12345,\"token\":\"private-token\"}");
        var handler = new Handler { Response = "{\"messageId\":\"m1\",\"conversationId\":\"c1\"}" };
        using var bridge = new SwarmSpanPeerBridge(new HttpClient(handler), () => tokenPath);
        var handle = await bridge.SendMessageAsync("peer", "hello", "c1", default);
        Assert.Equal("m1", handle.MessageId);
        Assert.Equal("/v1/messages/send", handler.Uri!.AbsolutePath);

        handler.Response = "[{\"peerDeviceId\":\"peer\",\"peerDisplayName\":\"PC\",\"messageId\":\"m2\",\"conversationId\":\"c1\",\"isReply\":true,\"kind\":\"reply\",\"text\":\"done\",\"receivedUtcMs\":5}]";
        var inbox = await bridge.InboxAsync("c 1", default);
        Assert.Equal("done", inbox.Single().Text);
        Assert.Equal("/v1/messages/inbox", handler.Uri!.AbsolutePath);
        Assert.Equal("?conversationId=c%201", handler.Uri.Query);

        handler.Response = "{\"requestId\":\"r\",\"runId\":\"run1\",\"state\":\"running\",\"steps\":[],\"nextAfter\":0,\"current\":\"click save\",\"currentElapsedMs\":5000}";
        var started = await bridge.StartScreenAsync("peer", "screenshot", default);
        Assert.Equal("/v1/remote-control/start", handler.Uri!.AbsolutePath);
        Assert.Equal("run1", started.RunId);
        Assert.False(started.IsFinal);
        Assert.Equal("click save", started.Current);

        handler.Response = "{\"requestId\":\"r\",\"runId\":\"run1\",\"state\":\"completed\",\"ok\":true,\"detail\":\"ran\",\"nextAfter\":1,\"steps\":[{\"command\":\"screenshot\",\"succeeded\":true,\"text\":\"ok\",\"elapsedMs\":3,\"images\":[{\"subject\":\"screen 1\",\"width\":2,\"height\":3,\"pngBase64\":\"AQID\"}]}],\"notes\":[]}";
        var screen = await bridge.PollScreenAsync("peer", "run1", 0, 15000, false, default);
        Assert.True(screen.Ok);
        Assert.True(screen.IsFinal);
        Assert.Equal("AQID", screen.Steps.Single().Images.Single().PngBase64);
        Assert.Equal("/v1/remote-control/poll", handler.Uri!.AbsolutePath);
    }

    [Fact]
    public async Task Transfer_status_retains_completion_and_receiver_failure_details()
    {
        var tokenPath = Path.Combine(Tmp, "control-token.json");
        await File.WriteAllTextAsync(tokenPath, "{\"port\":12345,\"token\":\"private-token\"}");
        var handler = new Handler { Response = "{\"transferId\":\"source\",\"state\":\"Completed\",\"error\":null,\"transferredBytes\":42}" };
        using var bridge = new SwarmSpanPeerBridge(new HttpClient(handler), () => tokenPath);
        var completed = await bridge.TransferAsync("source", default);
        Assert.Equal("Completed", completed.GetProperty("state").GetString());
        Assert.Equal(42, completed.GetProperty("transferredBytes").GetInt32());
        handler.Response = "{\"transferId\":\"source\",\"state\":\"Rejected\",\"error\":\"Busy\"}";
        var rejected = await bridge.TransferAsync("source", default);
        Assert.Equal("Rejected", rejected.GetProperty("state").GetString());
        Assert.Equal("Busy", rejected.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Http_dispatch_is_not_retried_on_lost_reply_or_authentication_error()
    {
        var tokenPath = Path.Combine(Tmp, "control-token.json");
        await File.WriteAllTextAsync(tokenPath, "{\"port\":12345,\"token\":\"private-token\"}");
        var handler = new Handler { Fail = true };
        using var http = new HttpClient(handler);
        var bridge = new SwarmSpanPeerBridge(http, () => tokenPath);
        var request = new PeerAssignment("peer", "Task", "Goal", null, ["Pass"], [], "parent", null, null, "job", "build", []);
        await Assert.ThrowsAsync<HttpRequestException>(() => bridge.AssignAsync(request, default));
        Assert.Equal(1, handler.Calls);
        handler.Fail = false; handler.Status = HttpStatusCode.Unauthorized;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.AssignAsync(request, default));
        Assert.Equal(2, handler.Calls);
        Assert.Contains("inspect peers.work", error.Message);
        Assert.DoesNotContain("private-token", error.Message);
    }

    [Fact]
    public void Custom_bridge_directory_is_respected_and_peer_disable_setting_removes_registration()
    {
        Assert.Equal(Path.Combine(Tmp, "control-token.json"), SwarmSpanPeerBridge.FindToken(Tmp));
        var services = new ServiceCollection().AddLogging().AddSwarmCodeHost(new() { RepositoryRoot = Tmp, DataPaths = Paths,
            Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Peers:Enabled"] = "false" }).Build() });
        using var sp = services.BuildServiceProvider();
        Assert.Null(sp.GetService<PeerCoordinator>());
    }

    private sealed class Handler : HttpMessageHandler
    {
        public string Response = "[]";
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string? Bearer;
        public Uri? Uri;
        public int Calls;
        public bool Fail;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Bearer = request.Headers.Authorization?.Parameter; Uri = request.RequestUri;
            if (Fail) throw new HttpRequestException("Disconnected");
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Response, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class AssignmentBridge : IPeerWorkBridge
    {
        public PeerAssignment? Assignment;
        public int Waits;
        private PeerWork? _work;
        public Task<IReadOnlyList<PeerIdentity>> ListPeersAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<PeerIdentity>>([new("device", "Windows worker", "windows", true)]);
        public Task<PeerCapabilities> CapabilitiesAsync(string deviceId, CancellationToken ct) => Task.FromResult(new PeerCapabilities
        {
            DeviceId = deviceId, Platform = "windows", Architecture = "x64", AgentBridgeEnabled = true, LaneVersion = 2,
            GeneratedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Providers = [new("local", "Local worker", "ollama", true, "ready", "coder", ["coder"], ["headless"], true, ["coder"])],
        });
        public Task<PeerWorkHandle> AssignAsync(PeerAssignment assignment, CancellationToken ct)
        {
            Assignment = assignment;
            _work = new() { WorkId = "work-1", ConversationId = assignment.ConversationId, PeerDeviceId = assignment.PeerDeviceId, Status = "Running" };
            return Task.FromResult(new PeerWorkHandle(_work.WorkId, assignment.ConversationId, "message-1"));
        }
        public Task<IReadOnlyList<PeerWork>> ListWorkAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<PeerWork>>(_work is null ? [] : [_work]);
        public Task<PeerWork> WorkAsync(string workId, CancellationToken ct) => Task.FromResult(_work!);
        public Task<PeerWork> WaitAsync(string workId, long since, int timeoutSeconds, CancellationToken ct)
        {
            Waits++;
            _work = _work! with { IsTerminal = true, Status = "Completed", LastReplyText = "Checks passed", Artifacts = ["Agent Handoff/checks/checks.json"] };
            return Task.FromResult(_work);
        }
        public Task<PeerWork> CancelAsync(string workId, CancellationToken ct) => throw new NotSupportedException();
    }
}
