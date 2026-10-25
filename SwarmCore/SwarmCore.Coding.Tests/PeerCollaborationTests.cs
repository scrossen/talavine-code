using System.Text.Json;
using SwarmCore.Coding.Peers;

namespace SwarmCore.Coding.Tests;

public sealed class PeerCollaborationTests : IDisposable
{
    private readonly string _captures = Path.Combine(Path.GetTempPath(), "peer-collab-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_captures)) Directory.Delete(_captures, true); }

    private sealed class Peers(params PeerIdentity[] list) : IPeerWorkBridge
    {
        public Task<IReadOnlyList<PeerIdentity>> ListPeersAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<PeerIdentity>>(list);
        public Task<PeerCapabilities> CapabilitiesAsync(string deviceId, CancellationToken ct) => throw new NotSupportedException();
        public Task<PeerWorkHandle> AssignAsync(PeerAssignment assignment, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<PeerWork>> ListWorkAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<PeerWork> WorkAsync(string workId, CancellationToken ct) => throw new NotSupportedException();
        public Task<PeerWork> WaitAsync(string workId, long since, int timeoutSeconds, CancellationToken ct) => throw new NotSupportedException();
        public Task<PeerWork> CancelAsync(string workId, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Collab : IPeerCollaborationBridge
    {
        public (string Peer, string Text, string Conversation)? Sent;
        public string? InboxConversation;
        public (string Peer, string Commands)? Screen;
        public PeerScreenResult ScreenResult = new(true, "ran", [], []) { RunId = "run1", State = "completed" };
        public Queue<PeerScreenResult> Polls = new();
        public List<(string Run, int After, bool Cancel)> PollLog = [];
        public Task<PeerMessageHandle> SendMessageAsync(string peerDeviceId, string text, string conversationId, CancellationToken ct)
        { Sent = (peerDeviceId, text, conversationId); return Task.FromResult(new PeerMessageHandle("m1", conversationId)); }
        public Task<IReadOnlyList<PeerMessage>> InboxAsync(string conversationId, CancellationToken ct)
        { InboxConversation = conversationId; return Task.FromResult<IReadOnlyList<PeerMessage>>([new("pc", "PC", "m2", conversationId, true, "reply", "done", 1)]); }
        public Task<PeerScreenResult> StartScreenAsync(string peerDeviceId, string commands, CancellationToken ct)
        { Screen = (peerDeviceId, commands); return Task.FromResult(ScreenResult); }
        public Task<PeerScreenResult> PollScreenAsync(string peerDeviceId, string runId, int after, int waitMs, bool cancel, CancellationToken ct)
        {
            PollLog.Add((runId, after, cancel));
            return Polls.Count > 0 ? Task.FromResult(Polls.Dequeue()) : throw new InvalidOperationException("SwarmSpan connection failed");
        }
    }

    private readonly List<string> _progress = [];
    private (Collab bridge, Func<string, object, Task<JsonElement>> call) Tools(string? owner = "conv-1")
    {
        var bridge = new Collab();
        var collaboration = new PeerCollaboration(new Peers(new("pc", "Office PC", "windows", true), new("mac", "Mac", "macos", true), new("mac2", "mac", "macos", true)), bridge, () => _captures, _progress.Add);
        var tools = PeerCollaborationTools.Create(collaboration, () => owner);
        return (bridge, async (id, input) =>
        {
            var text = (string)(await tools.Single(t => t.ToolId == id).Invoke(input, default))!;
            return JsonDocument.Parse(text).RootElement.Clone();
        });
    }

    [Fact]
    public async Task Messages_go_to_the_resolved_peer_in_the_host_scoped_conversation_and_inbox_is_scoped_to_it()
    {
        var (bridge, call) = Tools();
        var sent = await call("peers.message", new PeerMessageInput("office pc", " use the staging branch "));
        Assert.True(sent.GetProperty("success").GetBoolean(), sent.ToString());
        Assert.Equal(("pc", "use the staging branch", "conv-1"), bridge.Sent);
        var inbox = await call("peers.inbox", new PeerInboxInput());
        Assert.Equal("conv-1", bridge.InboxConversation);
        Assert.Equal("done", inbox.GetProperty("result")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Ambiguous_and_unknown_peers_are_refused_without_sending()
    {
        var (bridge, call) = Tools();
        Assert.Contains("ambiguous", (await call("peers.message", new PeerMessageInput("MAC", "hi"))).GetProperty("error").GetString());
        Assert.Contains("No paired device", (await call("peers.screen", new PeerScreenInput("nobody", "screens"))).GetProperty("error").GetString());
        Assert.Null(bridge.Sent);
        Assert.Null(bridge.Screen);
    }

    [Fact]
    public async Task Tools_need_a_conversation_turn_but_no_extra_authorization()
    {
        var (_, noTurn) = Tools(owner: null);
        Assert.False((await noTurn("peers.message", new PeerMessageInput("pc", "x"))).GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task Screenshots_are_saved_to_short_lived_files_and_never_returned_as_base64()
    {
        var (bridge, call) = Tools();
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 9 };
        bridge.ScreenResult = new(true, "ran", [new("screenshot", true, "saved", null, 12, [new("screen 1", 10, 10, Convert.ToBase64String(png))])], ["note"]) { RunId = "run1", State = "completed" };
        var result = await call("peers.screen", new PeerScreenInput("pc", "screenshot"));
        Assert.Equal(("pc", "screenshot"), bridge.Screen);
        Assert.DoesNotContain(Convert.ToBase64String(png), result.ToString());
        var path = result.GetProperty("result").GetProperty("steps")[0].GetProperty("images")[0].GetProperty("path").GetString()!;
        Assert.Equal(png, await File.ReadAllBytesAsync(path));
        Assert.StartsWith(_captures, path);
    }

    [Fact]
    public async Task A_remote_failure_is_returned_as_data_not_hidden()
    {
        var (bridge, call) = Tools();
        bridge.ScreenResult = new(false, "unsupported", [], ["This SwarmSpan build cannot be remote controlled."]) { State = "failed" };
        var result = (await call("peers.screen", new PeerScreenInput("pc", "screens"))).GetProperty("result");
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal("unsupported", result.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task A_long_run_is_pulsed_to_the_end_reporting_progress_with_no_overall_limit()
    {
        var (bridge, call) = Tools();
        var step = (string c) => new PeerScreenStep(c, true, "ok", null, 1, []);
        bridge.ScreenResult = new(false, "", [], []) { RunId = "run1", State = "running" };
        bridge.Polls.Enqueue(new(false, "", [step("a")], []) { RunId = "run1", State = "running", NextAfter = 1, TotalStepsDone = 1, Current = "click save", CurrentElapsedMs = 42_000 });
        bridge.Polls.Enqueue(new(true, "ran", [step("b")], []) { RunId = "run1", State = "completed", NextAfter = 2, TotalStepsDone = 2 });
        var result = (await call("peers.screen", new PeerScreenInput("pc", "a\nb"))).GetProperty("result");
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal(2, result.GetProperty("steps").GetArrayLength());
        Assert.Equal([("run1", 0, false), ("run1", 1, false)], bridge.PollLog);
        Assert.Contains(_progress, p => p.Contains("click save") && p.Contains("42s"));
    }

    [Fact]
    public async Task Reattaching_by_run_id_collects_the_run_without_resending_any_input()
    {
        var (bridge, call) = Tools();
        bridge.Polls.Enqueue(new(true, "ran", [new("x", true, "ok", null, 1, [])], []) { RunId = "run9", State = "completed", NextAfter = 1, TotalStepsDone = 1 });
        var result = (await call("peers.screen", new PeerScreenInput("pc", RunId: "run9"))).GetProperty("result");
        Assert.Null(bridge.Screen);
        Assert.Equal("run9", result.GetProperty("runId").GetString());
        Assert.Equal(1, result.GetProperty("steps").GetArrayLength());
    }

    [Fact]
    public async Task Cancelling_asks_the_device_to_stop_and_waits_for_it_to_confirm()
    {
        var (bridge, call) = Tools();
        bridge.ScreenResult = new(false, "", [], []) { RunId = "run1", State = "running" };
        bridge.Polls.Enqueue(new(false, "canceled", [], []) { RunId = "run1", State = "canceled", Detail = "canceled" });
        var collaboration = new PeerCollaboration(new Peers(new PeerIdentity("pc", "Office PC", "windows", true)), bridge, () => _captures);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = await collaboration.ScreenAsync(new PeerScreenInput("pc", "wait 60"), cts.Token);
        Assert.Contains(("run1", 0, true), bridge.PollLog);
        Assert.Contains("canceled", System.Text.Json.JsonSerializer.Serialize(result));
    }
}
