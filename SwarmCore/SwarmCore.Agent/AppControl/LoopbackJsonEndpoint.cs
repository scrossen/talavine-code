using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace SwarmCore.Agent.AppControl;

/// <summary>What a <see cref="LoopbackJsonEndpoint"/> handler answers: an HTTP status and a JSON body.</summary>
public sealed record LoopbackJsonResult(int Status, JsonObject Body)
{
    public static LoopbackJsonResult Ok(JsonObject? body = null) => new(200, body ?? new JsonObject { ["ok"] = true });

    public static LoopbackJsonResult Fail(int status, string message) => new(status, new JsonObject { ["ok"] = false, ["error"] = message });
}

/// <summary>
/// One authenticated JSON POST route served on <see cref="IPAddress.Loopback"/> inside the app's own process, for
/// a local companion (an editor extension, a script) that read the app's user-private discovery record. Same
/// boundary as <see cref="LoopbackMcpServer"/>: loopback only, a bearer token compared in constant time, Host and
/// Origin must name this loopback endpoint (DNS-rebinding protection), and bodies above <c>maxBodyBytes</c> are
/// refused with 413 before they are read. The handler owns validation of the parsed object.
/// </summary>
public sealed class LoopbackJsonEndpoint : IAsyncDisposable
{
    private readonly string _path;
    private readonly int _maxBodyBytes;
    private readonly byte[] _token;
    private readonly Func<JsonObject, CancellationToken, Task<LoopbackJsonResult>> _handler;
    private readonly ILogger _logger;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public LoopbackJsonEndpoint(string path, string token, Func<JsonObject, CancellationToken, Task<LoopbackJsonResult>> handler,
        ILogger logger, int maxBodyBytes = 256 * 1024)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length < 32) throw new ArgumentException("A strong bearer token is required.", nameof(token));
        if (string.IsNullOrEmpty(path) || path[0] != '/') throw new ArgumentException("The route must start with '/'.", nameof(path));
        if (maxBodyBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBodyBytes));
        _path = path;
        _token = Encoding.UTF8.GetBytes(token);
        _handler = handler;
        _logger = logger;
        _maxBodyBytes = maxBodyBytes;
    }

    public int Port { get; private set; }

    public string Url => $"http://127.0.0.1:{Port}{_path}";

    /// <summary>Starts listening; port 0 (the default) picks a free port.</summary>
    public void Start(int port = 0)
    {
        if (_listener is not null) return;
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _cts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException ex) { _logger.LogDebug(ex, "Loopback endpoint accept failed"); continue; }
            _ = Task.Run(() => ServeConnectionAsync(client, ct), CancellationToken.None);
        }
    }

    private async Task ServeConnectionAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            while (!ct.IsCancellationRequested)
            {
                LoopbackHttpRequest? request;
                try { request = await LoopbackHttpRequest.ReadAsync(stream, _maxBodyBytes, ct).ConfigureAwait(false); }
                catch (LoopbackRequestTooLargeException ex)
                {
                    await Json(LoopbackJsonResult.Fail(413, ex.Message)).WriteAsync(stream, keepAlive: false, ct).ConfigureAwait(false);
                    return;
                }
                if (request is null) return;
                var keepAlive = request.KeepAlive;
                var response = await HandleAsync(request, ct).ConfigureAwait(false);
                await response.WriteAsync(stream, keepAlive, ct).ConfigureAwait(false);
                if (!keepAlive) return;
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (Exception ex) { _logger.LogDebug(ex, "Loopback endpoint connection ended"); }
    }

    private async Task<LoopbackHttpResponse> HandleAsync(LoopbackHttpRequest request, CancellationToken ct)
    {
        if (!LoopbackAuthority.IsLoopbackHost(request.Header("host"), Port))
            return Json(LoopbackJsonResult.Fail(421, "Misdirected request."));
        var origin = request.Header("origin");
        if (!string.IsNullOrEmpty(origin) && !LoopbackAuthority.IsLoopbackOrigin(origin, Port))
            return Json(LoopbackJsonResult.Fail(403, "Cross-origin requests are not accepted."));
        if (!string.Equals(request.Path, _path, StringComparison.Ordinal))
            return Json(LoopbackJsonResult.Fail(404, "Not found."));
        if (!LoopbackAuthority.BearerMatches(request.Header("authorization"), _token))
            return LoopbackHttpResponse.Json(401, LoopbackJsonResult.Fail(401, "A valid bearer token is required.").Body, ("WWW-Authenticate", "Bearer"));
        if (request.Method != "POST")
            return LoopbackHttpResponse.Json(405, LoopbackJsonResult.Fail(405, "Method not allowed.").Body, ("Allow", "POST"));
        if (request.Header("content-type") is { } type && !type.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            return Json(LoopbackJsonResult.Fail(415, "Send application/json."));

        JsonObject? body;
        try { body = JsonNode.Parse(request.Body) as JsonObject; }
        catch (JsonException) { body = null; }
        if (body is null) return Json(LoopbackJsonResult.Fail(400, "The body must be a JSON object."));
        try
        {
            return Json(await _handler(body, ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Loopback endpoint handler failed");
            return Json(LoopbackJsonResult.Fail(500, ex is ArgumentException or InvalidOperationException ? ex.Message : "The request failed."));
        }
    }

    private static LoopbackHttpResponse Json(LoopbackJsonResult result) => LoopbackHttpResponse.Json(result.Status, result.Body);

    public async ValueTask DisposeAsync()
    {
        if (_cts is null) return;
        _cts.Cancel();
        try { _listener?.Stop(); } catch (SocketException) { }
        if (_acceptLoop is not null) { try { await _acceptLoop.ConfigureAwait(false); } catch { } }
        _cts.Dispose();
        _cts = null;
    }
}
