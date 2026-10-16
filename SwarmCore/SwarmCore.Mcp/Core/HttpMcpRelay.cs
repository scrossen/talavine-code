using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace SwarmCore.Mcp.Core;

/// <summary>A host-owned HTTP client exposed as a tools-only MCP server. Credentials never leave this process.</summary>
public sealed class HttpMcpRelay : IAsyncDisposable
{
    private readonly McpClient _client;
    private readonly McpCredentialRedactor _credentials;
    private HttpMcpRelay(McpClient client, string? bearer) { _client = client; _credentials = new([bearer]); }

    public static async Task<HttpMcpRelay> ConnectAsync(Uri endpoint, string? bearer, CancellationToken ct = default)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https") || endpoint.UserInfo.Length > 0 || endpoint.Fragment.Length > 0)
            throw new ArgumentException("Invalid HTTP MCP endpoint.");
        var transport = new HttpClientTransport(new()
        {
            Name = "host-http-relay", Endpoint = endpoint, TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = bearer is null ? null : new Dictionary<string, string> { ["Authorization"] = "Bearer " + bearer },
        }, new HttpClient(new OriginBoundHandler(endpoint)), ownsHttpClient: true);
        try { return new(await McpClient.CreateAsync(transport, cancellationToken: ct).ConfigureAwait(false), bearer); }
        catch (OperationCanceledException) { await transport.DisposeAsync().ConfigureAwait(false); throw; }
        catch (Exception ex)
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            // The status code says what to do (401: sign in again; 404: wrong URL); exception text could echo
            // server content, so only the status or the exception type is passed on.
            var reason = FindStatus(ex) is { } status ? $"HTTP {(int)status} {status}" : ex.GetType().Name;
            throw new InvalidOperationException($"HTTP MCP relay could not connect ({reason}).");
        }
    }

    private static System.Net.HttpStatusCode? FindStatus(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
            if (ex is HttpRequestException { StatusCode: { } status }) return status;
        return null;
    }

    public async ValueTask<ListToolsResult> ListToolsAsync(ListToolsRequestParams? request, CancellationToken ct = default)
    {
        try
        {
            var result = await _client.SendRequestAsync<ListToolsRequestParams, ListToolsResult>("tools/list", request ?? new(),
                McpJsonUtilities.DefaultOptions, cancellationToken: ct).ConfigureAwait(false);
            return _credentials.Sanitize(result);
        }
        catch (OperationCanceledException) { throw; }
        catch { throw new McpException("HTTP MCP relay could not list tools."); }
    }

    public async ValueTask<CallToolResult> CallToolAsync(CallToolRequestParams? request, CancellationToken ct = default)
    {
        if (request is null) throw new McpException("Missing tool request.");
        try
        {
            var result = await _client.SendRequestAsync<CallToolRequestParams, CallToolResult>("tools/call", request,
                McpJsonUtilities.DefaultOptions, cancellationToken: ct).ConfigureAwait(false);
            return _credentials.Sanitize(result);
        }
        catch (OperationCanceledException) { throw; }
        catch { return new() { IsError = true, Content = [new TextContentBlock { Text = "HTTP MCP relay tool call failed." }] }; }
    }

    public McpServerOptions ServerOptions(string name) => new()
    {
        ServerInfo = new() { Name = name, Version = "1.0.0" },
        Capabilities = new() { Tools = new() },
        Handlers = new()
        {
            ListToolsHandler = (request, ct) => ListToolsAsync(request.Params, ct),
            CallToolHandler = (request, ct) => CallToolAsync(request.Params, ct),
        },
    };

    public ValueTask DisposeAsync() => _client.DisposeAsync();

    private sealed class OriginBoundHandler(Uri endpoint) : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false })
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri is not { } target || target.Scheme != endpoint.Scheme || target.Authority != endpoint.Authority || target.UserInfo.Length > 0)
                throw new HttpRequestException("HTTP MCP relay attempted to leave its configured origin.");
            return base.SendAsync(request, ct);
        }
    }
}
