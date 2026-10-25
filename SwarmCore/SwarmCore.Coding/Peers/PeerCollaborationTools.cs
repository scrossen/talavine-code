using SwarmCore.Abstractions.IO;
using SwarmCore.Agent.Catalog;

namespace SwarmCore.Coding.Peers;

public sealed record PeerMessageInput(string Peer, string Text);
public sealed record PeerInboxInput();
public sealed record PeerScreenInput(string Peer, string? Commands = null, string? RunId = null);

/// <summary>Peer resolution and screenshot handling shared by the collaboration tools.</summary>
public sealed class PeerCollaboration(IPeerWorkBridge peers, IPeerCollaborationBridge bridge, Func<string> captureDirectory, Action<string>? progress = null)
{
    private const int KeepCaptures = 20;
    private static readonly TimeSpan CaptureMaxAge = TimeSpan.FromHours(1);

    public async Task<PeerMessageHandle> MessageAsync(string conversationId, PeerMessageInput input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Text)) throw new ArgumentException("A message is required.");
        var peer = await ResolveAsync(input.Peer, ct).ConfigureAwait(false);
        return await bridge.SendMessageAsync(peer.DeviceId, input.Text.Trim(), conversationId, ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<PeerMessage>> InboxAsync(string conversationId, CancellationToken ct) => bridge.InboxAsync(conversationId, ct);

    private static readonly TimeSpan LostContactAfter = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Starts the commands (or reattaches to <see cref="PeerScreenInput.RunId"/>) and pulses the device until it reports a final
    /// state. There is no overall time limit: every pulse reports progress, and cancelling asks the device to stop after its
    /// current step. Losing contact for 90 s returns the run id so the work can be reattached instead of repeated.
    /// </summary>
    public async Task<object> ScreenAsync(PeerScreenInput input, CancellationToken ct)
    {
        var peer = await ResolveAsync(input.Peer, ct).ConfigureAwait(false);
        var steps = new List<PeerScreenStep>();
        var notes = new List<string>();
        PeerScreenResult snapshot;
        if (!string.IsNullOrWhiteSpace(input.RunId))
            snapshot = new PeerScreenResult(false, "", [], []) { RunId = input.RunId.Trim(), State = "running" };
        else
        {
            if (string.IsNullOrWhiteSpace(input.Commands)) throw new ArgumentException("At least one command is required. Send 'help' to list them.");
            snapshot = await bridge.StartScreenAsync(peer.DeviceId, input.Commands, ct).ConfigureAwait(false);
            steps.AddRange(snapshot.Steps); notes.AddRange(snapshot.Notes);
        }
        var runId = snapshot.RunId;
        var next = snapshot.NextAfter;
        var silentSince = (DateTime?)null;
        var cancelSent = false;
        while (!snapshot.IsFinal && !string.IsNullOrEmpty(runId))
        {
            var cancel = ct.IsCancellationRequested && !cancelSent;
            try
            {
                // After cancelling, keep following without the caller's token until the device confirms it stopped.
                snapshot = await bridge.PollScreenAsync(peer.DeviceId, runId, next, 15_000, cancel, cancelSent || cancel ? CancellationToken.None : ct).ConfigureAwait(false);
                cancelSent |= cancel;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested && !cancelSent) { continue; }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException)
            { snapshot = new PeerScreenResult(false, "no-response", [], []) { RunId = runId, State = "running" }; }
            if (snapshot.Detail == "no-response")
            {
                silentSince ??= DateTime.UtcNow;
                progress?.Invoke($"{peer.DisplayName}: no answer for {(int)(DateTime.UtcNow - silentSince.Value).TotalSeconds}s, still trying…");
                if (DateTime.UtcNow - silentSince > LostContactAfter)
                {
                    notes.Add($"Lost contact with {peer.DisplayName}. The run ({runId}) may still be going: call peers.screen with runId '{runId}' to reattach, or take a screenshot, before repeating any input.");
                    snapshot = new PeerScreenResult(false, "lost-contact", [], []) { RunId = runId, State = "lost-contact" };
                    break;
                }
                await Task.Delay(2000, CancellationToken.None).ConfigureAwait(false);
                continue;
            }
            silentSince = null;
            steps.AddRange(snapshot.Steps); notes.AddRange(snapshot.Notes);
            next = snapshot.NextAfter;
            progress?.Invoke(snapshot.Current is { } current
                ? $"{peer.DisplayName}: {snapshot.TotalStepsDone} step(s) done; running \"{Shorten(current)}\" for {snapshot.CurrentElapsedMs / 1000}s"
                : $"{peer.DisplayName}: {snapshot.TotalStepsDone} step(s) done ({snapshot.State})");
        }
        var directory = captureDirectory();
        var saved = 0;
        var rows = steps.Select(step => new
        {
            step.Command, step.Succeeded, step.Text, step.FailureCategory, step.ElapsedMs,
            images = step.Images.Select(image => SaveCapture(directory, peer.DeviceId, image, ref saved)).ToArray(),
        }).ToArray();
        if (saved > 0) Prune(directory);
        return new { peer = peer.DisplayName, runId, snapshot.State, snapshot.Ok, snapshot.Detail, snapshot.ElapsedMs, steps = rows, notes };
    }

    private static string Shorten(string text) => text.Length <= 80 ? text : text[..80] + "…";

    private async Task<PeerIdentity> ResolveAsync(string peer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(peer)) throw new ArgumentException("Name the peer by display name or device id (see peers.list).");
        var all = await peers.ListPeersAsync(ct).ConfigureAwait(false);
        var matches = all.Where(p => p.DeviceId == peer || string.Equals(p.DisplayName, peer, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException("No paired device matches that name. Use peers.list."),
            _ => throw new InvalidOperationException("Peer name is ambiguous; use the device id."),
        };
    }

    private static object SaveCapture(string directory, string deviceId, PeerScreenImage image, ref int saved)
    {
        Directory.CreateDirectory(directory);
        var safeDevice = new string(deviceId.Where(char.IsAsciiLetterOrDigit).Take(12).ToArray());
        var path = Path.Combine(directory, $"{safeDevice}-{DateTime.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, Convert.FromBase64String(image.PngBase64));
        saved++;
        return new { image.Subject, image.Width, image.Height, path };
    }

    private static void Prune(string directory)
    {
        try { ManagedScratch.PruneFiles(directory, "*.png", KeepCaptures, CaptureMaxAge); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* try again on the next capture */ }
    }
}

public static class PeerCollaborationTools
{
    public const string Guidance = "Collaboration: peers.message sends text into this conversation's shared thread with a peer, which is how you steer a "
        + "persistent or visible agent session you started there with peers.delegate (executionMode persistent or visible), ask it a question, or "
        + "hand it new information; collect replies with peers.inbox, which returns only this conversation's messages. Messages are conversation, "
        + "not proof of completion: confirm results through peers.work artifacts. peers.screen drives another computer's screen, windows, keyboard "
        + "and mouse with plain-text commands (send 'help' for the list; 'screens', 'windows', 'screenshot', 'controls in <window>', 'click', 'type', "
        + "'press'). It works on any paired device running a controllable SwarmSpan (not the sandboxed Mac Store edition). Observe before acting: screenshot or list controls, act, then screenshot to verify. Inputs are sent once and never "
        + "retried; after a timeout or no-response, look at the screen before repeating anything. Never type credentials, make purchases, or touch "
        + "windows the person did not ask about. Screenshot files are short-lived: read them now.";

    public static IReadOnlyList<ToolDescriptor> Create(PeerCollaboration collaboration, Func<string?> owner) =>
    [
        ToolDescriptor.Create<PeerMessageInput, string>("peers.message", "coding.peers",
            "Send a text message to a paired device in this conversation's thread. Use it to steer a persistent or visible peer session you started, or to ask its agent a question. "
            + "Returns a message id; collect the reply with peers.inbox.",
            ToolCostClass.Cheap, ToolSideEffect.ExternalSend,
            (input, ct) => Call(owner, id => collaboration.MessageAsync(id, input, ct)), keywords: ["peer", "message", "chat", "steer", "collaborate"]),
        ToolDescriptor.Create<PeerInboxInput, string>("peers.inbox", "coding.peers",
            "Read and clear messages that peers sent into this conversation's thread (replies to peers.message and to peers.delegate work). Other conversations' messages are untouched.",
            ToolCostClass.Trivial, ToolSideEffect.WritesLocal,
            (_, ct) => Call(owner, id => collaboration.InboxAsync(id, ct)), keywords: ["peer", "inbox", "replies"]),
        ToolDescriptor.Create<PeerScreenInput, string>("peers.screen", "coding.peers",
            "Control a paired computer's screen with plain-text commands, one per line (screens, windows, screenshot, controls in <window>, click, type, press, wait; send 'help' for all). "
            + "Works on any paired, controllable device. Runs as long as it needs, reporting progress; returns each step's result, with screenshots saved as short-lived PNG files whose paths are returned. "
            + "If it reports lost-contact, pass the returned runId (and no commands) to reattach. Observe, act, then verify with a screenshot. Never retried automatically.",
            ToolCostClass.Expensive, ToolSideEffect.Exec,
            (input, ct) => Call(owner, _ => collaboration.ScreenAsync(input, ct)), keywords: ["peer", "screen", "remote", "control", "screenshot", "click", "keyboard", "mouse"]),
    ];

    private static Task<string> Call<T>(Func<string?> owner, Func<string, Task<T>> action)
        => PeerTools.Result(() => owner() is { Length: > 0 } id ? action(id) : throw new InvalidOperationException("Peer collaboration requires a conversation turn."));
}
