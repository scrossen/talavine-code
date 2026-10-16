using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace SwarmCore.Agent.AppControl;

/// <summary>One tool a <see cref="LoopbackMcpServer"/> offers.</summary>
public sealed record LoopbackMcpTool(
    string Name,
    string Description,
    JsonObject InputSchema,
    bool ReadOnly,
    Func<JsonObject, CancellationToken, Task<LoopbackMcpToolResult>> Invoke);

/// <summary>Text content returned to the MCP client; <see cref="IsError"/> maps to the MCP <c>isError</c> flag.</summary>
public sealed record LoopbackMcpToolResult(string Text, bool IsError = false);

/// <summary>
/// A small MCP server (Streamable HTTP, JSON responses, no server-sent stream) that an app hosts inside its own
/// process so a local agent the person connected can drive it. It listens on <see cref="IPAddress.Loopback"/> only
/// (never the LAN, so no firewall prompt), requires a bearer token the app writes to a user-private discovery file,
/// and rejects requests whose Host or Origin is not this loopback endpoint (DNS-rebinding protection).
/// It is hand-rolled over <see cref="TcpListener"/> like SwarmSpan's local control server: HttpListener needs a URL
/// ACL on Windows and Kestrel is not available on every MAUI head. Authorization of each tool call stays with the
/// host's own tool runner; this class only transports.
/// </summary>
public sealed class LoopbackMcpServer : IAsyncDisposable
{
    public const string ProtocolVersion = "2025-06-18";
    private static readonly string[] SupportedProtocolVersions = ["2025-06-18", "2025-03-26", "2024-11-05"];
    private const int MaxBodyBytes = 8 * 1024 * 1024;

    private readonly string _name;
    private readonly string _version;
    private readonly string? _instructions;
    private readonly Func<IReadOnlyList<LoopbackMcpTool>> _tools;
    private readonly ILogger _logger;
    private readonly byte[] _token;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public LoopbackMcpServer(string name, string version, string token, Func<IReadOnlyList<LoopbackMcpTool>> tools,
        ILogger logger, string? instructions = null)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length < 32) throw new ArgumentException("A strong bearer token is required.", nameof(token));
        _name = name;
        _version = version;
        _instructions = instructions;
        _tools = tools;
        _logger = logger;
        _token = Encoding.UTF8.GetBytes(token);
    }

    public int Port { get; private set; }

    public string Url => $"http://127.0.0.1:{Port}/mcp";

    /// <summary>A random token suitable for <see cref="LoopbackMcpServer"/>.</summary>
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    /// <summary>Starts listening. Port 0 picks a free port; a busy preferred port falls back to a free one.</summary>
    public void Start(int preferredPort = 0)
    {
        if (_listener is not null) return;
        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, preferredPort);
            listener.Start();
        }
        catch (SocketException) when (preferredPort != 0)
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
        }
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
            catch (SocketException ex) { _logger.LogDebug(ex, "Loopback MCP accept failed"); continue; }
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
                try { request = await LoopbackHttpRequest.ReadAsync(stream, MaxBodyBytes, ct).ConfigureAwait(false); }
                catch (LoopbackRequestTooLargeException ex)
                {
                    await LoopbackHttpResponse.Text(413, ex.Message).WriteAsync(stream, keepAlive: false, ct).ConfigureAwait(false);
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
        catch (Exception ex) { _logger.LogDebug(ex, "Loopback MCP connection ended"); }
    }

    private async Task<LoopbackHttpResponse> HandleAsync(LoopbackHttpRequest request, CancellationToken ct)
    {
        if (!IsLoopbackAuthority(request.Header("host")))
            return LoopbackHttpResponse.Text(421, "Misdirected request.");
        var origin = request.Header("origin");
        if (!string.IsNullOrEmpty(origin) && !IsLoopbackOrigin(origin))
            return LoopbackHttpResponse.Text(403, "Cross-origin requests are not accepted.");
        if (!string.Equals(request.Path, "/mcp", StringComparison.Ordinal))
            return LoopbackHttpResponse.Text(404, "Not found.");
        if (!Authorized(request.Header("authorization")))
            return LoopbackHttpResponse.Text(401, "A valid bearer token is required.", ("WWW-Authenticate", "Bearer"));
        if (request.Method == "DELETE") return LoopbackHttpResponse.Empty(200);
        if (request.Method == "GET") return LoopbackHttpResponse.Text(405, "This server does not open a server-sent event stream.", ("Allow", "POST, DELETE"));
        if (request.Method != "POST") return LoopbackHttpResponse.Text(405, "Method not allowed.", ("Allow", "POST, DELETE"));

        JsonNode? body;
        try { body = JsonNode.Parse(request.Body); }
        catch (JsonException) { return LoopbackHttpResponse.Json(400, Error(null, -32700, "Parse error.")); }

        if (body is JsonArray batch)
        {
            var replies = new JsonArray();
            foreach (var item in batch)
                if (item is JsonObject message && await DispatchAsync(message, ct).ConfigureAwait(false) is { } reply)
                    replies.Add(reply);
            return replies.Count == 0 ? LoopbackHttpResponse.Empty(202) : LoopbackHttpResponse.Json(200, replies);
        }
        if (body is not JsonObject single) return LoopbackHttpResponse.Json(400, Error(null, -32600, "Invalid request."));
        var result = await DispatchAsync(single, ct).ConfigureAwait(false);
        if (result is null) return LoopbackHttpResponse.Empty(202);
        // Stateless server: the session id is informational and never required on later requests.
        var isInitialize = single["method"] is JsonValue m && m.TryGetValue<string>(out var name) && name == "initialize";
        return isInitialize
            ? LoopbackHttpResponse.Json(200, result, ("Mcp-Session-Id", Guid.NewGuid().ToString("N")))
            : LoopbackHttpResponse.Json(200, result);
    }

    private async Task<JsonNode?> DispatchAsync(JsonObject message, CancellationToken ct)
    {
        var id = message["id"]?.DeepClone();
        var method = message["method"] is JsonValue methodValue && methodValue.TryGetValue<string>(out var methodName) ? methodName : null;
        if (method is null && message["method"] is not null)
            return id is null ? null : Error(id, -32600, "method must be a string.");
        if (method is null) return null; // a response from the client: nothing to answer
        if (id is null) return null;      // a notification
        var parameters = message["params"] as JsonObject ?? new JsonObject();
        try
        {
            JsonNode result = method switch
            {
                "initialize" => Initialize(parameters),
                "ping" => new JsonObject(),
                "tools/list" => ListTools(),
                "tools/call" => await CallToolAsync(parameters, ct).ConfigureAwait(false),
                "resources/list" => new JsonObject { ["resources"] = new JsonArray() },
                "prompts/list" => new JsonObject { ["prompts"] = new JsonArray() },
                _ => throw new MethodNotFound(),
            };
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        }
        catch (OperationCanceledException) { throw; }
        catch (MethodNotFound) { return Error(id, -32601, "Method not found: " + method); }
        catch (ArgumentException ex) { return Error(id, -32602, ex.Message); }
        // A wrongly typed parameter (for example a numeric tool name) is the client's error, answered on the
        // connection rather than dropping it.
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or FormatException)
        { return Error(id, -32602, "Invalid params: " + ex.Message); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Loopback MCP request {Method} failed", method);
            return Error(id, -32603, "Internal error.");
        }
    }

    private JsonObject Initialize(JsonObject parameters)
    {
        var requested = parameters["protocolVersion"]?.GetValue<string>();
        var version = requested is not null && SupportedProtocolVersions.Contains(requested) ? requested : ProtocolVersion;
        var result = new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = _name, ["version"] = _version },
        };
        if (!string.IsNullOrWhiteSpace(_instructions)) result["instructions"] = _instructions;
        return result;
    }

    private JsonObject ListTools()
    {
        var tools = new JsonArray();
        foreach (var tool in _tools())
            tools.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.InputSchema.DeepClone(),
                ["annotations"] = new JsonObject { ["readOnlyHint"] = tool.ReadOnly, ["openWorldHint"] = false },
            });
        return new JsonObject { ["tools"] = tools };
    }

    private async Task<JsonObject> CallToolAsync(JsonObject parameters, CancellationToken ct)
    {
        var name = parameters["name"]?.GetValue<string>() ?? throw new ArgumentException("Tool name is required.");
        var tool = _tools().FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal))
            ?? throw new ArgumentException("Unknown tool: " + name);
        var arguments = parameters["arguments"] as JsonObject ?? new JsonObject();
        LoopbackMcpToolResult result;
        try { result = await tool.Invoke((JsonObject)arguments.DeepClone(), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // The host's tool layer words its own refusals; anything else stays generic.
            _logger.LogWarning(ex, "Loopback MCP tool {Tool} failed", name);
            result = new(ex is InvalidOperationException or ArgumentException ? ex.Message : "The tool failed (" + ex.GetType().Name + ").", true);
        }
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = result.Text }),
            ["isError"] = result.IsError,
        };
    }

    private bool Authorized(string? header) => LoopbackAuthority.BearerMatches(header, _token);

    private bool IsLoopbackAuthority(string? host) => LoopbackAuthority.IsLoopbackHost(host, Port);

    private bool IsLoopbackOrigin(string origin) => LoopbackAuthority.IsLoopbackOrigin(origin, Port);

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    public async ValueTask DisposeAsync()
    {
        if (_cts is null) return;
        _cts.Cancel();
        try { _listener?.Stop(); } catch (SocketException) { }
        if (_acceptLoop is not null) { try { await _acceptLoop.ConfigureAwait(false); } catch { } }
        _cts.Dispose();
        _cts = null;
    }

    private sealed class MethodNotFound : Exception;
}
