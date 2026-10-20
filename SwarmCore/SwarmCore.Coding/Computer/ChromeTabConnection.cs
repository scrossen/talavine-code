using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;

namespace SwarmCore.Coding.Computer;

/// <summary>Adapts one selected target to Playwright without auto-attaching every tab in personal Chrome.
/// The existing Playwright surface continues to own observation freshness, input and uncertainty.</summary>
internal sealed class ChromeTabConnection : IDisposable
{
    private readonly ChromeDevToolsConnection browser;
    private readonly string targetId;
    private readonly HttpListener listener = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<byte[]> outgoing = Channel.CreateBounded<byte[]>(256);
    private readonly HashSet<string> sessions = new(StringComparer.Ordinal);
    private readonly object sync = new();
    private string? rootSession;
    private WebSocket? socket;
    private int disposed;
    public string Endpoint { get; }

    public ChromeTabConnection(ChromeDevToolsConnection browser, string targetId)
    {
        this.browser = browser;
        this.targetId = targetId;
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var path = Guid.NewGuid().ToString("N");
        listener.Prefixes.Add($"http://127.0.0.1:{port}/{path}/");
        listener.Start();
        Endpoint = $"ws://127.0.0.1:{port}/{path}/";
        browser.Notification += OnNotification;
        _ = ServeAsync();
    }

    private void Queue(object message)
    {
        if (!outgoing.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(message, ChromeDevToolsConnection.JsonOptions))) Dispose();
    }

    private void OnNotification(JsonElement message)
    {
        var method = message.GetProperty("method").GetString();
        var parent = message.TryGetProperty("sessionId", out var parentValue) ? parentValue.GetString() : null;
        lock (sync)
        {
            if (method == "Target.attachedToTarget")
            {
                var parameters = message.GetProperty("params");
                var target = parameters.GetProperty("targetInfo").GetProperty("targetId").GetString();
                if (parent is null ? target != targetId : !sessions.Contains(parent)) return;
                var id = parameters.GetProperty("sessionId").GetString()!;
                sessions.Add(id);
                if (parent is null) rootSession = id;
            }
            else if (parent is null)
            {
                if (method != "Target.detachedFromTarget" || !message.GetProperty("params").TryGetProperty("sessionId", out var detached)
                    || !sessions.Remove(detached.GetString()!)) return;
            }
            else if (!sessions.Contains(parent)) return;
            Queue(message);
        }
    }

    private async Task ServeAsync()
    {
        try
        {
            var request = await listener.GetContextAsync().WaitAsync(lifetime.Token).ConfigureAwait(false);
            if (!request.Request.IsWebSocketRequest || !IPAddress.IsLoopback(request.Request.RemoteEndPoint.Address))
            {
                request.Response.StatusCode = 403;
                request.Response.Close();
                return;
            }
            socket = (await request.AcceptWebSocketAsync(null).ConfigureAwait(false)).WebSocket;
            // Keep the listener alive: Stop also tears down upgraded WebSockets on macOS.
            // Only this one nonce-bearing loopback request is accepted by the adapter.
            _ = WriteAsync(socket);
            var buffer = new byte[64 * 1024];
            while (!lifetime.IsCancellationRequested)
            {
                using var payload = new MemoryStream();
                WebSocketReceiveResult part;
                do
                {
                    part = await socket.ReceiveAsync(buffer, lifetime.Token).ConfigureAwait(false);
                    if (part.MessageType == WebSocketMessageType.Close) return;
                    if (payload.Length + part.Count > 16 * 1024 * 1024) throw new IOException("Browser command exceeded the connection limit.");
                    payload.Write(buffer, 0, part.Count);
                } while (!part.EndOfMessage);
                using var json = JsonDocument.Parse(payload.ToArray());
                _ = ForwardAsync(json.RootElement.Clone());
            }
        }
        catch (Exception) when (lifetime.IsCancellationRequested) { }
        catch (Exception) { /* The local socket closes; Playwright reports a bounded connection failure. */ }
        finally { Dispose(); }
    }

    private async Task WriteAsync(WebSocket destination)
    {
        try
        {
            await foreach (var message in outgoing.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
                await destination.SendAsync(message, WebSocketMessageType.Text, true, lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception) { Dispose(); }
    }

    internal static bool IsBlockedBrowserCommand(string method) => method is
        "Browser.close" or "Target.createBrowserContext" or "Target.disposeBrowserContext" or "Target.createTarget";

    private async Task ForwardAsync(JsonElement message)
    {
        var id = message.GetProperty("id").GetInt64();
        try
        {
            var method = message.GetProperty("method").GetString()!;
            var session = message.TryGetProperty("sessionId", out var sid) ? sid.GetString() : null;
            var parameters = message.TryGetProperty("params", out var p) ? p : JsonSerializer.SerializeToElement(new { });
            if (session is not null)
                lock (sync) { if (!sessions.Contains(session)) throw new IOException("Unknown selected-tab session."); }
            if (IsBlockedBrowserCommand(method)) throw new IOException("This connection controls only the selected tab.");
            if (session is null && method is "Target.attachToTarget" or "Target.closeTarget" or "Target.activateTarget"
                && parameters.GetProperty("targetId").GetString() != targetId)
                throw new IOException("Cannot control an unrelated tab.");
            JsonElement result;
            if (method is "Browser.setDownloadBehavior" or "Emulation.setFocusEmulationEnabled" or "Emulation.setEmulatedMedia")
                result = JsonSerializer.SerializeToElement(new { }); // Preserve the person's browser defaults.
            else if (session is null && method == "Target.setAutoAttach")
            {
                if (parameters.GetProperty("autoAttach").GetBoolean())
                {
                    if (rootSession is null)
                        await browser.SendAsync("Target.attachToTarget", new { targetId, flatten = true }, null, lifetime.Token).ConfigureAwait(false);
                }
                result = JsonSerializer.SerializeToElement(new { });
            }
            else if (session is null && method == "Target.getTargets")
            {
                var targets = await browser.SendAsync(method, parameters, null, lifetime.Token).ConfigureAwait(false);
                result = JsonSerializer.SerializeToElement(new { targetInfos = targets.GetProperty("targetInfos").EnumerateArray()
                    .Where(t => t.GetProperty("targetId").GetString() == targetId).ToArray() });
            }
            else result = await browser.SendAsync(method, parameters, session, lifetime.Token).ConfigureAwait(false);
            Queue(new { id, result, sessionId = session });
        }
        catch (Exception ex)
        {
            Queue(new { id, error = new { code = -32000, message = ex is IOException ? ex.Message : "Selected tab connection failed." } });
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        browser.Notification -= OnNotification;
        lifetime.Cancel();
        outgoing.Writer.TryComplete();
        socket?.Abort();
        socket?.Dispose();
        listener.Close();
        if (rootSession is { } session) _ = DetachAsync(session);
    }

    private async Task DetachAsync(string session)
    {
        try { await browser.SendAsync("Target.detachFromTarget", new { sessionId = session }, null, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { }
    }
}
