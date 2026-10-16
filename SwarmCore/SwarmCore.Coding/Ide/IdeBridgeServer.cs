using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SwarmCore.Agent.AppControl;

namespace SwarmCore.Coding.Ide;

/// <summary>What the host did with a selection, returned to the editor extension.</summary>
public sealed record IdeDeliveryResult(bool Delivered, string Message, string? SessionId = null);

/// <summary>
/// The editor-to-app bridge: a <see cref="LoopbackJsonEndpoint"/> at <c>/ide/selection</c> plus a discovery record
/// (<c>&lt;dir&gt;/&lt;app&gt;.json</c> with url, token, PID and process start time) that the editor extension
/// reads. Kept out of the app-control folder because every record there is an MCP server agents may connect.
/// The token is new for every run. The host's sink routes the selection; nothing is sent to a model here.
/// </summary>
public sealed class IdeBridgeServer : IAsyncDisposable
{
    public const string Route = "/ide/selection";
    public const int MaxBodyBytes = 512 * 1024;

    private readonly LoopbackDiscoveryDirectory _discovery;
    private readonly string _app;
    private readonly string _displayName;
    private readonly string? _version;
    private readonly Func<IdeSelection, CancellationToken, Task<IdeDeliveryResult>> _sink;
    private readonly ILogger _logger;
    private LoopbackJsonEndpoint? _endpoint;
    private bool _published;

    public IdeBridgeServer(string app, string displayName, string? version, Func<IdeSelection, CancellationToken, Task<IdeDeliveryResult>> sink,
        ILogger logger, string? discoveryDirectory = null)
    {
        _app = app;
        _displayName = displayName;
        _version = version;
        _sink = sink;
        _logger = logger;
        _discovery = new LoopbackDiscoveryDirectory(discoveryDirectory ?? DefaultDirectory);
    }

    /// <summary><c>%LOCALAPPDATA%/SwarmCore/ide</c> (per user); <c>SWARMCORE_IDE_DIR</c> overrides it.</summary>
    public static string DefaultDirectory
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("SWARMCORE_IDE_DIR");
            return !string.IsNullOrWhiteSpace(overridden)
                ? overridden
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwarmCore", "ide");
        }
    }

    public string? Url => _endpoint?.Url;

    public string? Token { get; private set; }

    public string DiscoveryPath => _discovery.PathFor(_app);

    /// <summary>Starts the endpoint and publishes the record; returns false when another live instance owns it.</summary>
    public bool Start()
    {
        if (_endpoint is not null) return _published;
        Token = LoopbackMcpServer.NewToken();
        _endpoint = new LoopbackJsonEndpoint(Route, Token, HandleAsync, _logger, MaxBodyBytes);
        _endpoint.Start();
        _published = _discovery.Publish(new AppControlEndpoint(_app, _displayName, _endpoint.Url, Token, Environment.ProcessId, null, _version, DateTimeOffset.UtcNow));
        if (!_published) _logger.LogInformation("Another {App} instance owns the editor bridge record; this one is not published.", _app);
        return _published;
    }

    private async Task<LoopbackJsonResult> HandleAsync(JsonObject body, CancellationToken ct)
    {
        if (!IdeSelection.TryParse(body, out var selection, out var error)) return LoopbackJsonResult.Fail(422, error);
        var result = await _sink(selection, ct).ConfigureAwait(false);
        var reply = new JsonObject { ["ok"] = result.Delivered, ["message"] = result.Message };
        if (result.SessionId is not null) reply["sessionId"] = result.SessionId;
        return new LoopbackJsonResult(result.Delivered ? 200 : 409, reply);
    }

    public async ValueTask DisposeAsync()
    {
        if (_published) _discovery.Withdraw(_app, Environment.ProcessId);
        _published = false;
        if (_endpoint is not null) await _endpoint.DisposeAsync().ConfigureAwait(false);
        _endpoint = null;
    }
}
