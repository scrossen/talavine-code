using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SwarmCore.Coding.Peers;

namespace SwarmCode.Hosting.Peers;

/// <summary>In-process, authenticated loopback adapter: no MCP subprocess, second pairing store, or LAN credentials.</summary>
public sealed class SwarmSpanPeerBridge(HttpClient http, Func<string?> tokenPath) : IPeerWorkBridge, IPeerFileBridge, IPeerCollaborationBridge, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Setup = "Start SwarmSpan on this computer and enable its MCP bridge. Pair the devices in SwarmSpan. If installed in a custom location, set Peers:SwarmSpanDataDirectory.";

    public Task<IReadOnlyList<PeerIdentity>> ListPeersAsync(CancellationToken ct) => GetList<PeerIdentity>("/v1/peers", ct);
    public Task<PeerCapabilities> CapabilitiesAsync(string deviceId, CancellationToken ct)
        => Send<PeerCapabilities>(HttpMethod.Post, "/v1/remote/agent-capabilities", new { peerDeviceId = deviceId }, ct);
    public Task<PeerWorkHandle> AssignAsync(PeerAssignment assignment, CancellationToken ct)
        => Send<PeerWorkHandle>(HttpMethod.Post, "/v1/work/assign", assignment, ct);
    public Task<IReadOnlyList<PeerWork>> ListWorkAsync(CancellationToken ct) => GetList<PeerWork>("/v1/work", ct);
    public Task<PeerWork> WorkAsync(string workId, CancellationToken ct) => Send<PeerWork>(HttpMethod.Get, WorkPath(workId), null, ct);
    public Task<PeerWork> WaitAsync(string workId, long since, int timeoutSeconds, CancellationToken ct)
        => Send<PeerWork>(HttpMethod.Get, WorkPath(workId) + $"/wait?since={since}&timeout={Math.Clamp(timeoutSeconds, 1, 50)}", null, ct);
    public Task<PeerWork> CancelAsync(string workId, CancellationToken ct) => Send<PeerWork>(HttpMethod.Post, WorkPath(workId) + "/cancel", new { }, ct);
    public Task<JsonElement> FilesAsync(string peer, string path, CancellationToken ct)
        => Send<JsonElement>(HttpMethod.Post, "/v1/remote/list", new { peerDeviceId = peer, path }, ct);
    public Task<JsonElement> SendFileAsync(string peer, string localPath, CancellationToken ct)
        => Send<JsonElement>(HttpMethod.Post, "/v1/send", new { peerDeviceId = peer, localPath }, ct);
    public Task<JsonElement> PullFileAsync(string peer, string remotePath, string localDest, CancellationToken ct)
        => Send<JsonElement>(HttpMethod.Post, "/v1/pull", new { peerDeviceId = peer, remotePath, localDest }, ct);
    public Task<JsonElement> TransferAsync(string transferId, CancellationToken ct)
        => Send<JsonElement>(HttpMethod.Get, "/v1/transfers/" + Uri.EscapeDataString(transferId), null, ct);
    public async Task<PeerMessageHandle> SendMessageAsync(string peerDeviceId, string text, string conversationId, CancellationToken ct)
        => await Send<PeerMessageHandle>(HttpMethod.Post, "/v1/messages/send", new { peerDeviceId, text, conversationId }, ct).ConfigureAwait(false);
    public Task<IReadOnlyList<PeerMessage>> InboxAsync(string conversationId, CancellationToken ct)
        => GetList<PeerMessage>("/v1/messages/inbox?conversationId=" + Uri.EscapeDataString(conversationId), ct);
    public Task<PeerScreenResult> StartScreenAsync(string peerDeviceId, string commands, CancellationToken ct)
        => Send<PeerScreenResult>(HttpMethod.Post, "/v1/remote-control/start", new { peerDeviceId, script = commands }, ct);
    public Task<PeerScreenResult> PollScreenAsync(string peerDeviceId, string runId, int after, int waitMs, bool cancel, CancellationToken ct)
        => Send<PeerScreenResult>(HttpMethod.Post, "/v1/remote-control/poll", new { peerDeviceId, runId, after, waitMs, cancel }, ct);
    private static string WorkPath(string id) => "/v1/work/" + Uri.EscapeDataString(id);
    private async Task<IReadOnlyList<T>> GetList<T>(string path, CancellationToken ct)
        => await Send<List<T>>(HttpMethod.Get, path, null, ct).ConfigureAwait(false);

    internal async Task<T> Send<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        // Resolve on every request; a bridge restart rotates both token and port. Never log either.
        ControlToken? token;
        try
        {
            var file = tokenPath();
            if (file is null) throw new InvalidOperationException(Setup);
            token = JsonSerializer.Deserialize<ControlToken>(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false), Json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { throw new InvalidOperationException(Setup); }
        if (token is null || token.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(token.Token)) throw new InvalidOperationException(Setup);
        using var request = new HttpRequestMessage(method, $"http://127.0.0.1:{token.Port}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            // No automatic retries: an assignment can outlive a lost HTTP reply.
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"SwarmSpan returned HTTP {(int)response.StatusCode}. Check its diagnostics; for a dispatch, inspect peers.work before retrying.");
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false), cancellationToken: timeout.Token).ConfigureAwait(false);
            // ErrorDto has only an error field. Transfer status also has an error
            // field (usually null); its state and failure detail belong to the caller.
            if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String && json.RootElement.EnumerateObject().Count() == 1)
                throw new InvalidOperationException("SwarmSpan rejected the operation. Inspect its diagnostics and peers.work before retrying work.");
            return json.RootElement.Deserialize<T>(Json) ?? throw new InvalidOperationException("SwarmSpan returned an empty response.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new InvalidOperationException("SwarmSpan timed out. A dispatched task or screen action may still be running; inspect peers.work or take a screenshot before retrying."); }
        catch (JsonException) { throw new InvalidOperationException("SwarmSpan returned an incompatible response. Update SwarmSpan and inspect peers.work before retrying work."); }
    }

    private sealed record ControlToken(string Token, int Port);
    public void Dispose() => http.Dispose();

    internal static string? FindToken(string? directory = null)
    {
        if (!string.IsNullOrWhiteSpace(directory)) return Path.Combine(Path.GetFullPath(directory), "control-token.json");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Path.Combine(home, "Library", "Application Support", "SwarmSpan", "control-token.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwarmSpan", "control-token.json"),
            Path.Combine(home, ".local", "share", "SwarmSpan", "control-token.json"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
