using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace SwarmCore.Coding.Computer;

/// <summary>A browser-level connection. Listing targets never initializes or wakes their renderers.</summary>
internal sealed class ChromeDevToolsConnection : IDisposable
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
        { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    private readonly ClientWebSocket socket = new();
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pending = new();
    private long nextId;
    private int disposed;
    public bool Connected => socket.State == WebSocketState.Open && !lifetime.IsCancellationRequested;
    public event Action<JsonElement>? Notification;

    public async Task ConnectAsync(string endpoint, CancellationToken ct)
    {
        ChromeComputerSession.ValidateEndpoint(endpoint);
        var uri = new Uri(endpoint);
        if (uri.Scheme is "http" or "https")
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            using var response = await http.GetAsync(endpoint.TrimEnd('/') + "/json/version", ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var version = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            endpoint = version.RootElement.GetProperty("webSocketDebuggerUrl").GetString()!;
            ChromeComputerSession.ValidateEndpoint(endpoint);
            uri = new(endpoint);
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60)); // Includes Chrome's explicit consent prompt.
        await socket.ConnectAsync(uri, deadline.Token).ConfigureAwait(false);
        _ = ReceiveAsync();
        await SendAsync("Browser.getVersion", null, null, deadline.Token).ConfigureAwait(false);
    }

    public async Task<JsonElement> SendAsync(string method, object? parameters, string? sessionId, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var message = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters ?? new { }, sessionId }, JsonOptions);
            await sendGate.WaitAsync(deadline.Token).ConfigureAwait(false);
            try { await socket.SendAsync(message, WebSocketMessageType.Text, true, deadline.Token).ConfigureAwait(false); }
            finally { sendGate.Release(); }
            return await completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        finally { pending.TryRemove(id, out _); }
    }

    private async Task ReceiveAsync()
    {
        Exception failure = new IOException("Chrome disconnected. Connect again before continuing.");
        try
        {
            var buffer = new byte[64 * 1024];
            while (!lifetime.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult part;
                do
                {
                    part = await socket.ReceiveAsync(buffer, lifetime.Token).ConfigureAwait(false);
                    if (part.MessageType == WebSocketMessageType.Close) return;
                    if (message.Length + part.Count > 64 * 1024 * 1024) throw new IOException("Chrome response exceeded the connection limit.");
                    message.Write(buffer, 0, part.Count);
                } while (!part.EndOfMessage);
                using var json = JsonDocument.Parse(message.ToArray());
                var data = json.RootElement;
                if (data.TryGetProperty("id", out var id) && pending.TryRemove(id.GetInt64(), out var completion))
                {
                    if (data.TryGetProperty("error", out var error))
                        completion.TrySetException(new IOException("Chrome protocol error: " + error.GetProperty("message").GetString()));
                    else completion.TrySetResult(data.GetProperty("result").Clone());
                }
                else if (data.TryGetProperty("method", out _)) Notification?.Invoke(data.Clone());
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            lifetime.Cancel();
            foreach (var entry in pending.Values) entry.TrySetException(failure);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        socket.Abort();
        socket.Dispose();
    }
}
