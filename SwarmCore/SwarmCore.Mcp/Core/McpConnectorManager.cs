using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SwarmCore.Mcp.Abstractions;
using SwarmCore.Mcp.Models;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SwarmCore.Abstractions.Processes;

namespace SwarmCore.Mcp.Core;

/// <summary>
/// Manages connections to external MCP servers using the official C# MCP SDK.
/// Singleton — maintains long-lived connections to MCP server subprocesses.
/// </summary>
public class McpConnectorManager : IMcpConnectorManager
{
    /// <summary>Upper bound for disposing all connections concurrently on shutdown.</summary>
    internal static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long the SDK waits for a stdio child to exit on its own before it
    /// kills the process tree. The SDK's cleanup order is: wait
    /// <c>ShutdownTimeout</c> for a spontaneous exit, <i>then</i> kill — it never
    /// closes the child's stdin first, so no well-behaved server ever exits during
    /// that window and the SDK default (5 s) is pure dead time on every
    /// disconnect / dispose. One second keeps a small grace period for servers
    /// that do exit on session end while making disconnects feel immediate.
    /// </summary>
    internal static readonly TimeSpan StdioShutdownTimeout = TimeSpan.FromSeconds(1);

    private readonly ILogger<McpConnectorManager> _logger;
    private readonly ConcurrentDictionary<Guid, ManagedConnection> _connections = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<Guid>>> _inFlightConnects = new(StringComparer.Ordinal);
    private readonly object _lifecycleLock = new();
    private bool _disposed;

    private readonly IReadOnlyList<IMcpMissionBinding> _missionBindings;
    public McpConnectorManager(ILogger<McpConnectorManager> logger, IEnumerable<IMcpMissionBinding>? missionBindings = null)
    {
        _logger = logger;
        _missionBindings = missionBindings?.ToArray() ?? [];
    }

    /// <inheritdoc />
    public event EventHandler<McpConnectionChangedEventArgs>? ConnectionsChanged;

    /// <summary>
    /// Test hook: when set and it returns a transport, that transport is used
    /// instead of constructing a stdio/HTTP transport from the options. Lets
    /// tests drive the real connection lifecycle against an in-process server.
    /// </summary>
    internal Func<McpConnectorOptions, IClientTransport?>? TransportFactory { get; set; }

    // GUI apps on macOS (and launchd-spawned processes generally) inherit a
    // minimal PATH that omits the user tool dirs — ~/.local/bin,
    // /opt/homebrew/bin, ~/.cargo/bin — where uvx/uv/npx are typically
    // installed. So a bare command like "uvx" fails to launch with
    // "No such file or directory". We resolve the command to an absolute path
    // and widen the child's PATH so the server (and any tool it shells out to)
    // can be found.
    private static readonly string[] AdditionalPathDirs = BuildAdditionalPathDirs();

    private static string[] BuildAdditionalPathDirs()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var directories = new List<string>
        {
            "/opt/homebrew/bin",
            "/usr/local/bin",
            Path.Combine(home, ".local", "bin"),
            Path.Combine(home, ".cargo", "bin"),
            Path.Combine(home, ".bun", "bin"),
            "/usr/bin",
            "/bin",
        };

        return directories.ToArray();
    }

    /// <summary>
    /// Resolves a bare command name (e.g. "uvx") to an absolute path on
    /// Unix-like hosts by searching PATH plus common user tool dirs the GUI
    /// process doesn't inherit. Windows commands must remain bare: the MCP SDK
    /// launches them through <c>cmd.exe /c</c>, and expanding <c>npx</c> to a
    /// path such as <c>C:\Program Files\nodejs\npx</c> makes cmd parse the
    /// executable path as separate tokens. On Windows the child environment
    /// instead carries <c>NoDefaultCurrentDirectoryInExePath=1</c> so cmd.exe
    /// resolves the bare name against PATH rather than the (user-configurable)
    /// working directory — see <see cref="BuildEnvironment"/>. Apple commands are
    /// resolved against the final child PATH and working directory, then
    /// architecture-checked before the MCP SDK can spawn them.
    /// </summary>
    internal static string ResolveCommand(
        string command,
        string? workingDirectory = null,
        string? searchPath = null)
    {
        if (string.IsNullOrWhiteSpace(command))
            return command;

        if (OperatingSystem.IsWindows())
            return command;

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
        {
            var effectiveSearchPath = searchPath ?? string.Join(
                Path.PathSeparator,
                EnumerateSearchDirs());
            var resolved = AppleSiliconExecutable.ResolveCompatiblePath(
                command,
                workingDirectory,
                effectiveSearchPath);
            if (!File.Exists(resolved))
            {
                throw new FileNotFoundException(
                    $"Apple Silicon MCP executable could not be resolved: {command}",
                    command);
            }

            return resolved;
        }

        if (command.Contains('/') || command.Contains(Path.DirectorySeparatorChar))
            return command;

        foreach (var dir in EnumerateSearchDirs())
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var candidate = Path.Combine(dir, command);
            if (File.Exists(candidate))
                return candidate;
        }

        return command;
    }

    private static IEnumerable<string> EnumerateSearchDirs()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator))
                yield return dir;
        }

        foreach (var dir in AdditionalPathDirs)
            yield return dir;
    }

    // ── Child environment allowlist ─────────────────────────────────
    //
    // The child is started with InheritEnvironmentVariables = false, so only
    // what is listed here (plus the connector's own EnvironmentVariables)
    // reaches the server process. Everything else on the host — API keys,
    // tokens, cloud credentials — stays put. The list is deliberately
    // pragmatic: enough for npx / uvx / dotnet / node / python launchers and
    // the tools they shell out to.

    /// <summary>Exact variable names forwarded to child servers (matched case-insensitively).</summary>
    internal static readonly string[] AllowedEnvironmentNames =
    [
        // Locating executables and the user
        "PATH", "PATHEXT", "HOME", "USERPROFILE", "HOMEDRIVE", "HOMEPATH",
        "USER", "USERNAME", "LOGNAME", "PWD", "SHELL", "ComSpec",
        // Temp / system dirs
        "TEMP", "TMP", "TMPDIR", "SystemRoot", "SystemDrive", "windir",
        "APPDATA", "LOCALAPPDATA", "ProgramData", "PUBLIC",
        "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432",
        "CommonProgramFiles", "CommonProgramFiles(x86)", "CommonProgramW6432",
        // Locale / terminal
        "LANG", "LANGUAGE", "TERM", "TERM_PROGRAM", "COLORTERM",
        // Machine facts some launchers read
        "OS", "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS",
        // Network egress configuration (corporate proxies, custom CAs)
        "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "ALL_PROXY",
        "SSL_CERT_FILE", "SSL_CERT_DIR", "REQUESTS_CA_BUNDLE", "NODE_EXTRA_CA_CERTS",
        // Runtime homes
        "JAVA_HOME", "VIRTUAL_ENV", "CARGO_HOME", "RUSTUP_HOME", "GOPATH", "GOROOT",
    ];

    /// <summary>Variable-name prefixes forwarded to child servers (matched case-insensitively).</summary>
    internal static readonly string[] AllowedEnvironmentPrefixes =
    [
        "LC_", "XDG_", "DOTNET_", "NODE_", "NPM_CONFIG_", "NVM_", "UV_", "PYTHON",
        "CONDA_", "PIP_", "BUN_", "DENO_",
    ];

    internal static bool IsEnvironmentVariableAllowed(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var allowed in AllowedEnvironmentNames)
        {
            if (string.Equals(allowed, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        foreach (var prefix in AllowedEnvironmentPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Builds the child environment from scratch: an allowlisted subset of the
    /// host environment, the inherited PATH widened with the user tool dirs (so
    /// the server can locate sibling executables, e.g. uvx spawning uv/python),
    /// <c>NoDefaultCurrentDirectoryInExePath=1</c> on Windows so cmd.exe never
    /// resolves a bare command against the working directory, and finally the
    /// caller's explicit env vars layered on top (caller values always win).
    /// </summary>
    internal static Dictionary<string, string?> BuildEnvironment(Dictionary<string, string>? userEnv)
    {
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string key || entry.Value is not string value) continue;
            if (!IsEnvironmentVariableAllowed(key)) continue;
            // Exported bash functions ("() { ...") are not useful and have a bad history.
            if (value.StartsWith("() ", StringComparison.Ordinal)) continue;
            env[key] = value;
        }

        var existingPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var parts = existingPath
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        foreach (var dir in AdditionalPathDirs)
        {
            if (Directory.Exists(dir) && !parts.Contains(dir))
                parts.Add(dir);
        }
        env.Remove("Path");
        env["PATH"] = string.Join(Path.PathSeparator, parts);

        if (OperatingSystem.IsWindows())
        {
            // cmd.exe (which the SDK uses to launch bare commands) honours this
            // flag and skips the current directory when resolving `npx` & co.
            env["NoDefaultCurrentDirectoryInExePath"] = "1";
        }

        if (userEnv is not null)
        {
            // Caller-provided values win — including an explicit PATH override.
            foreach (var kvp in userEnv)
                env[kvp.Key] = kvp.Value;
        }

        return env;
    }

    /// <summary>
    /// Builds the SDK stdio transport options for a connector: resolved command,
    /// working directory, and an isolated (non-inherited) environment.
    /// </summary>
    internal static StdioClientTransportOptions BuildStdioTransportOptions(McpConnectorOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Command))
            throw new ArgumentException("Command is required for stdio transport", nameof(options));

        var workingDirectory = string.IsNullOrWhiteSpace(options.WorkingDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : options.WorkingDirectory;
        var environment = BuildEnvironment(options.EnvironmentVariables);

        return new StdioClientTransportOptions
        {
            Name = options.Name,
            Command = ResolveCommand(
                options.Command!,
                workingDirectory,
                environment["PATH"]),
            Arguments = options.Arguments ?? [],
            WorkingDirectory = workingDirectory,
            InheritEnvironmentVariables = false,
            EnvironmentVariables = environment,
            ShutdownTimeout = StdioShutdownTimeout,
        };
    }

    /// <summary>
    /// Connects to the server described by <paramref name="options"/>.
    /// <para>
    /// Concurrent calls with <b>identical</b> options are deduplicated while the
    /// first attempt is in flight: the later caller joins the same attempt and
    /// receives the same connection id, so a UI double-click or two racing
    /// startup paths cannot spawn two child processes for one connector. The
    /// joined attempt runs under the first caller's cancellation token; a joiner
    /// that cancels its own token stops waiting without affecting the attempt.
    /// Once an attempt has finished (successfully or not) a subsequent call with
    /// the same options starts a new, independent connection.
    /// </para>
    /// </summary>
    public async Task<Guid> ConnectAsync(McpConnectorOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await AuthorizeMissionAsync(options, null, new Dictionary<string, object?>(), ct);

        var key = BuildConnectKey(options);
        Lazy<Task<Guid>> ours = null!;
        ours = new Lazy<Task<Guid>>(
            () => ConnectCoreAsync(options, key, ours, ct),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var registered = _inFlightConnects.GetOrAdd(key, ours);
        if (ReferenceEquals(registered, ours))
            return await ours.Value;

        _logger.LogDebug(
            "ConnectAsync for MCP server '{Name}' joined an identical in-flight connect instead of starting a second one",
            options.Name);
        return await registered.Value.WaitAsync(ct);
    }

    private async Task<Guid> ConnectCoreAsync(
        McpConnectorOptions options,
        string connectKey,
        Lazy<Task<Guid>> self,
        CancellationToken ct)
    {
        var credentials = McpCredentialRedactor.FromOptions(options);
        try
        {
            return await ConnectUnguardedAsync(options, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Include transport construction/resolution failures, without logging option
            // dictionaries containing headers, credentials or child environment values.
            _logger.LogWarning("Failed to connect to MCP server '{Name}' ({Transport}): {Message}", options.Name, options.TransportType, credentials.Redact(ex.Message));
            var safe = credentials.SafeException(ex);
            if (ReferenceEquals(safe, ex)) throw;
            throw safe;
        }
        finally
        {
            // Only clear our own registration (value-matched remove): a later
            // attempt may already have claimed the key after we finished.
            _inFlightConnects.TryRemove(new KeyValuePair<string, Lazy<Task<Guid>>>(connectKey, self));
        }
    }

    private async Task<Guid> ConnectUnguardedAsync(McpConnectorOptions options, CancellationToken ct)
    {
        var connectionId = Guid.NewGuid();

        IClientTransport transport;
        string transportLabel;
        var transportType = options.TransportType?.Trim();

        if (SendsCredentialsOverPlaintext(options, out var plaintextHost))
        {
            _logger.LogWarning(
                "MCP connector '{Name}' sends credentials (API key / custom headers) to the non-loopback http:// endpoint host '{Host}' in plaintext; use an https:// URL",
                options.Name, plaintextHost);
        }

        var customTransport = TransportFactory?.Invoke(options);
        if (customTransport is not null)
        {
            transport = customTransport;
            transportLabel = string.IsNullOrWhiteSpace(transportType) ? "in-process" : transportType!.ToLowerInvariant();
        }
        else if (McpTransportTypes.IsStdio(transportType))
        {
            var stdioOptions = BuildStdioTransportOptions(options);
            transport = OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst()
                ? new ChildProcessStdioTransport(stdioOptions)
                : new StdioClientTransport(stdioOptions);
            transportLabel = McpTransportTypes.Stdio;
        }
        else if (McpTransportTypes.IsHttp(transportType))
        {
            var endpoint = ParseHttpEndpoint(options.Url);
            var transportOptions = new HttpClientTransportOptions
            {
                Name = options.Name,
                Endpoint = endpoint,
                TransportMode = ResolveHttpTransportMode(transportType),
                AdditionalHeaders = BuildHttpHeaders(options),
            };

            transport = new HttpClientTransport(transportOptions, new HttpClient(new OriginBoundHandler(endpoint)), ownsHttpClient: true);
            transportLabel = transportType!.ToLowerInvariant();
        }
        else
        {
            throw new NotSupportedException(
                $"Transport type '{options.TransportType ?? "(missing)"}' is not supported. Use one of {McpTransportTypes.SupportedList}.");
        }

        var managed = new ManagedConnection(connectionId, options.Name, transportLabel, options, DateTime.UtcNow);

        // The SDK exposes no disconnect event on McpClient, but the session
        // transport's message channel completes when the transport dies; observe
        // that so a dead server is reported without probing.
        transport = new ObservedClientTransport(transport, () => OnTransportClosed(managed));

        var clientOptions = new McpClientOptions
        {
            Handlers = new McpClientHandlers
            {
                NotificationHandlers =
                [
                    new KeyValuePair<string, Func<JsonRpcNotification, CancellationToken, ValueTask>>(
                        NotificationMethods.ToolListChangedNotification,
                        (_, _) =>
                        {
                            OnToolListChanged(managed);
                            return default;
                        }),
                ],
            },
        };

        McpClient? client = null;
        try
        {
            client = await McpClient.CreateAsync(transport, clientOptions, null, ct);
            managed.Client = client;

            var tools = await RunWithTimeoutAsync(
                options.CallTimeout, ct,
                token => client.ListToolsAsync(cancellationToken: token),
                "tools/list", options.Name);
            managed.ToolCount = tools.Count;

            lock (_lifecycleLock)
            {
                // A concurrent DisposeAsync may have raced past us; never leave a
                // live client behind after the manager has been torn down.
                ObjectDisposedException.ThrowIf(_disposed, this);
                _connections[connectionId] = managed;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation(
                "Connect to MCP server '{Name}' ({Transport}) was cancelled by the caller", options.Name, transportLabel);
            if (client is not null)
                await DisposeClientQuietlyAsync(client, connectionId);
            throw;
        }
        catch (Exception)
        {
            if (client is not null)
                await DisposeClientQuietlyAsync(client, connectionId);
            throw;
        }

        _logger.LogInformation(
            "Connected to MCP server '{Name}' ({Transport}) — {ToolCount} tools available [ConnectionId={Id}]",
            options.Name, transportLabel, managed.ToolCount, connectionId);

        RaiseChanged(managed, McpConnectionChangeKind.Connected);
        return connectionId;
    }

    public async Task DisconnectAsync(Guid connectionId, CancellationToken ct = default)
    {
        if (_connections.TryRemove(connectionId, out var managed))
        {
            _logger.LogInformation("Disconnecting from MCP server '{Name}' [ConnectionId={Id}]", managed.Name, connectionId);
            managed.Closing = true;
            await DisposeClientQuietlyAsync(managed.Client, connectionId);
            RaiseChanged(managed, McpConnectionChangeKind.Disconnected);
        }
    }

    public Task<IReadOnlyList<McpConnectionInfo>> GetConnectionsAsync(CancellationToken ct = default)
    {
        var connections = _connections.Values
            .Select(c => new McpConnectionInfo(
                c.ConnectionId,
                c.Name,
                c.TransportType,
                c.IsAlive,
                c.ConnectedAt,
                c.ToolCount))
            .ToList();

        return Task.FromResult<IReadOnlyList<McpConnectionInfo>>(connections);
    }

    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(Guid connectionId, CancellationToken ct = default)
    {
        var managed = GetConnection(connectionId);

        IList<McpClientTool> tools;
        try
        {
            tools = await RunWithTimeoutAsync(
                managed.Options.CallTimeout, ct,
                token => managed.Client.ListToolsAsync(cancellationToken: token),
                "tools/list", managed.Name);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not TimeoutException)
        {
            MarkFaultedIfTransportClosed(managed, ex);
            var safe = managed.Credentials.SafeException(ex);
            if (ReferenceEquals(safe, ex)) throw;
            throw safe;
        }

        managed.ToolCount = tools.Count;
        MarkAlive(managed);

        return tools.Select(t => managed.Credentials.Sanitize(t.ProtocolTool)).Select(t => new McpToolInfo(
            t.Name, t.Description,
            t.InputSchema.ValueKind != JsonValueKind.Undefined ? t.InputSchema.GetRawText() : null,
            ToAnnotations(t.Annotations))).ToList();
    }

    internal static McpToolAnnotationsInfo? ToAnnotations(ToolAnnotations? annotations) => annotations is null
        ? null
        : new McpToolAnnotationsInfo(
            annotations.Title,
            annotations.ReadOnlyHint,
            annotations.DestructiveHint,
            annotations.IdempotentHint,
            annotations.OpenWorldHint);

    public async Task<McpToolResult> InvokeToolAsync(
        Guid connectionId,
        string toolName,
        IDictionary<string, object?>? arguments = null,
        CancellationToken ct = default)
    {
        // Inside a mission the resource boundary answers first, even for an unknown connection: a call outside the
        // mission is refused as such (fail closed) rather than surfacing as a lookup error the caller might retry.
        if (SwarmCore.Abstractions.Security.ExecutionResourceBoundary.IsActive && !_connections.ContainsKey(connectionId))
            await SwarmCore.Abstractions.Security.ExecutionResourceBoundary.CheckUnclassifiedAsync("mcp", connectionId.ToString(), ct);
        var managed = GetConnection(connectionId);
        // Snapshot before checking; the caller cannot mutate arguments after authorization.
        arguments = arguments is null ? new Dictionary<string, object?>() : JsonSerializer.Deserialize<Dictionary<string, object?>>(
            JsonSerializer.Serialize(arguments))!;
        await AuthorizeMissionAsync(managed.Options, toolName, new Dictionary<string, object?>(arguments), ct);

        try
        {
            IReadOnlyDictionary<string, object?> readOnlyArgs = new Dictionary<string, object?>(arguments);
            var result = await RunWithTimeoutAsync(
                managed.Options.CallTimeout, ct,
                token => managed.Client.CallToolAsync(toolName, readOnlyArgs, cancellationToken: token),
                $"tools/call '{toolName}'", managed.Name);
            MarkAlive(managed);
            var content = managed.Credentials.Redact(FormatToolResult(managed.Credentials.Sanitize(result)));

            return new McpToolResult(result.IsError ?? false, content);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller-driven cancellation must surface as cancellation so executors
            // and timeouts can tell it apart from a server-side failure.
            throw;
        }
        catch (TimeoutException ex)
        {
            var safe = managed.Credentials.Redact(ex.Message);
            _logger.LogWarning("MCP tool '{Tool}' on '{Server}' timed out: {Message}", toolName, managed.Name, safe);
            return new McpToolResult(IsError: true, Content: safe);
        }
        catch (Exception ex)
        {
            MarkFaultedIfTransportClosed(managed, ex);
            var safe = managed.Credentials.Redact(ex.Message);
            _logger.LogError("Error invoking MCP tool '{Tool}' on '{Server}': {Message}", toolName, managed.Name, safe);
            return new McpToolResult(IsError: true, Content: safe);
        }
    }

    private sealed class OriginBoundHandler(Uri endpoint) : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false })
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var target = request.RequestUri;
            if (target is null || target.Scheme != endpoint.Scheme || target.Authority != endpoint.Authority || target.UserInfo.Length != 0)
                throw new HttpRequestException("MCP transport attempted to leave its configured origin.");
            await SwarmCore.Abstractions.Security.ExecutionResourceBoundary.CheckAsync(new(
                "mcp.transport", "Endpoint", endpoint.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped), "Inspect"), ct);
            return await base.SendAsync(request, ct);
        }
    }

    private async Task AuthorizeMissionAsync(McpConnectorOptions options, string? tool, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        if (!SwarmCore.Abstractions.Security.ExecutionResourceBoundary.IsActive) return;
        var actions = _missionBindings.Select(binding => binding.Resolve(options, tool, arguments)).FirstOrDefault(a => a is not null);
        if (actions is null || actions.Count == 0)
        {
            await SwarmCore.Abstractions.Security.ExecutionResourceBoundary.CheckUnclassifiedAsync("mcp", options.Name, ct);
            return;
        }
        foreach (var action in actions)
            await SwarmCore.Abstractions.Security.ExecutionResourceBoundary.CheckAsync(action, ct);
    }

    public void Dispose()
    {
        // Run on the pool so a UI SynchronizationContext cannot deadlock the
        // sync-over-async wait.
        Task.Run(() => DisposeAsync().AsTask()).GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        ManagedConnection[] toDispose;
        lock (_lifecycleLock)
        {
            if (_disposed) return;
            _disposed = true;
            toDispose = _connections.Values.ToArray();
            _connections.Clear();
        }

        if (toDispose.Length == 0)
            return;

        foreach (var managed in toDispose)
            managed.Closing = true;

        var all = Task.WhenAll(toDispose.Select(c => DisposeClientQuietlyAsync(c.Client, c.ConnectionId)));
        var finished = await Task.WhenAny(all, Task.Delay(DisposeTimeout));
        if (finished != all)
        {
            _logger.LogWarning(
                "Timed out after {Timeout}s waiting for {Count} MCP connection(s) to dispose; abandoning the rest",
                DisposeTimeout.TotalSeconds, toDispose.Length);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Runs <paramref name="operation"/> under a linked token that also fires
    /// after <paramref name="timeout"/>. A timeout surfaces as
    /// <see cref="TimeoutException"/>; caller cancellation propagates unchanged.
    /// A non-positive timeout disables the bound.
    /// </summary>
    internal static async Task<T> RunWithTimeoutAsync<T>(
        TimeSpan timeout,
        CancellationToken ct,
        Func<CancellationToken, ValueTask<T>> operation,
        string what,
        string serverName)
    {
        if (timeout <= TimeSpan.Zero)
            return await operation(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            return await operation(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"MCP {what} on '{serverName}' timed out after {timeout.TotalSeconds:0.#}s.");
        }
    }

    private async Task DisposeClientQuietlyAsync(McpClient? client, Guid connectionId)
    {
        if (client is null) return;
        try
        {
            await client.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error disposing MCP connection {Id}", connectionId);
        }
    }

    private void OnToolListChanged(ManagedConnection managed)
    {
        _logger.LogInformation(
            "MCP server '{Name}' announced a tool list change [ConnectionId={Id}]",
            managed.Name, managed.ConnectionId);
        RaiseChanged(managed, McpConnectionChangeKind.ToolsChanged);
    }

    private void OnTransportClosed(ManagedConnection managed)
    {
        managed.TransportClosed = true;
        if (managed.Closing) return; // we closed it on purpose
        MarkFaulted(managed, null);
    }

    private void MarkAlive(ManagedConnection managed)
    {
        // A transport whose channel has completed can never come back.
        if (managed.TransportClosed) return;

        if (managed.TrySetAlive(true))
        {
            _logger.LogInformation(
                "MCP server '{Name}' is reachable again [ConnectionId={Id}]",
                managed.Name, managed.ConnectionId);
            RaiseChanged(managed, McpConnectionChangeKind.Connected);
        }
    }

    private void MarkFaulted(ManagedConnection managed, Exception? reason)
    {
        if (managed.TrySetAlive(false))
        {
            _logger.LogWarning(
                "MCP server '{Name}' is no longer reachable; reporting it disconnected [ConnectionId={Id}]: {Message}",
                managed.Name, managed.ConnectionId, managed.Credentials.Redact(reason?.Message ?? "Transport closed."));
            RaiseChanged(managed, McpConnectionChangeKind.Faulted);
        }
    }

    private void MarkFaultedIfTransportClosed(ManagedConnection managed, Exception ex)
    {
        if (IsTransportClosed(ex, McpTransportTypes.IsStdio(managed.TransportType)))
            MarkFaulted(managed, ex);
    }

    /// <summary>
    /// True when <paramref name="ex"/> means the session itself is gone. A stdio pipe that
    /// throws an I/O or socket error belongs to a process that has exited; over HTTP the same
    /// errors are one failed request (a 503, a reset connection, a proxy hiccup) and the next
    /// call can succeed, so only a closed or disposed transport counts there. A per-call
    /// failure must not hide the connector's tools until the user reconnects.
    /// </summary>
    internal static bool IsTransportClosed(Exception? ex, bool processPipeTransport)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case ClientTransportClosedException:
                case ObjectDisposedException:
                case EndOfStreamException:
                    return true;
                case IOException or SocketException or HttpRequestException when processPipeTransport:
                    return true;
                case AggregateException aggregate:
                    if (aggregate.InnerExceptions.Any(inner => IsTransportClosed(inner, processPipeTransport))) return true;
                    break;
            }
        }
        return false;
    }

    private void RaiseChanged(ManagedConnection managed, McpConnectionChangeKind kind)
    {
        var handler = ConnectionsChanged;
        if (handler is null) return;
        try
        {
            handler(this, new McpConnectionChangedEventArgs(managed.ConnectionId, managed.Name, kind));
        }
        catch (Exception ex)
        {
            _logger.LogWarning("ConnectionsChanged handler threw for {Kind} on '{Name}': {Message}", kind, managed.Name, managed.Credentials.Redact(ex.Message));
        }
    }

    /// <summary>
    /// Validates and parses the server URL for the HTTP transport family.
    /// </summary>
    internal static Uri ParseHttpEndpoint(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("Url is required for http transport", nameof(url));

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"Url '{url}' must be an absolute http(s) URL for http transport", nameof(url));
        }

        return endpoint;
    }

    /// <summary>
    /// Maps a transport-type alias to the SDK's HTTP transport mode:
    /// "http" auto-detects (streamable HTTP first, SSE fallback), while
    /// "sse" and "streamable-http" pin the respective protocol.
    /// </summary>
    internal static HttpTransportMode ResolveHttpTransportMode(string? transportType)
    {
        var normalized = transportType?.Trim();
        if (string.Equals(normalized, McpTransportTypes.Sse, StringComparison.OrdinalIgnoreCase))
            return HttpTransportMode.Sse;
        if (string.Equals(normalized, McpTransportTypes.StreamableHttp, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "streamablehttp", StringComparison.OrdinalIgnoreCase))
            return HttpTransportMode.StreamableHttp;
        return HttpTransportMode.AutoDetect;
    }

    /// <summary>
    /// Builds the extra request headers for the HTTP transport: caller-supplied
    /// headers plus a bearer <c>Authorization</c> header derived from
    /// <see cref="McpConnectorOptions.ApiKey"/> (an explicit Authorization header
    /// in <see cref="McpConnectorOptions.Headers"/> wins). Returns null when there
    /// is nothing to add.
    /// </summary>
    internal static Dictionary<string, string>? BuildHttpHeaders(McpConnectorOptions options)
    {
        Dictionary<string, string>? headers = null;
        if (options.Headers is { Count: > 0 })
        {
            headers = new Dictionary<string, string>(options.Headers, StringComparer.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(options.ApiKey)
            && (headers is null || !headers.ContainsKey("Authorization")))
        {
            headers ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            headers["Authorization"] = $"Bearer {options.ApiKey.Trim()}";
        }

        return headers;
    }

    /// <summary>
    /// True when an HTTP-family connector would send an API key or custom headers
    /// to a plain <c>http://</c> endpoint that is not loopback — i.e. credentials
    /// in cleartext on the wire. Loopback (localhost / 127.0.0.1 / ::1) and
    /// <c>https://</c> endpoints never trigger this. <paramref name="host"/> is the
    /// endpoint host only, safe to log.
    /// </summary>
    internal static bool SendsCredentialsOverPlaintext(McpConnectorOptions options, out string host)
    {
        host = string.Empty;
        if (!McpTransportTypes.IsHttp(options.TransportType))
            return false;
        if (string.IsNullOrWhiteSpace(options.Url)
            || !Uri.TryCreate(options.Url.Trim(), UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttp
            || endpoint.IsLoopback)
        {
            return false;
        }

        var carriesCredentials = !string.IsNullOrWhiteSpace(options.ApiKey)
            || options.Headers is { Count: > 0 };
        if (!carriesCredentials)
            return false;

        host = endpoint.Host;
        return true;
    }

    /// <summary>
    /// Stable identity for a set of connector options, used to deduplicate
    /// concurrent identical connects. Every field that influences the launched
    /// process or HTTP session is folded into a SHA-256 so secrets (API key,
    /// headers, env values) never sit in a dictionary as plain text.
    /// </summary>
    internal static string BuildConnectKey(McpConnectorOptions options)
    {
        var sb = new StringBuilder();
        Append(sb, "name", options.Name);
        Append(sb, "transport", options.TransportType?.Trim().ToLowerInvariant());
        Append(sb, "timeout", options.CallTimeout.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(sb, "command", options.Command);
        Append(sb, "args", options.Arguments is null ? null : string.Join('\u001f', options.Arguments));
        Append(sb, "cwd", options.WorkingDirectory);
        Append(sb, "env", Flatten(options.EnvironmentVariables));
        Append(sb, "url", options.Url);
        Append(sb, "apiKey", options.ApiKey);
        Append(sb, "headers", Flatten(options.Headers));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexStringLower(hash);

        static void Append(StringBuilder sb, string label, string? value)
            => sb.Append(label).Append('=').Append(value is null ? "N" : "V" + value).Append('\n');

        static string? Flatten(Dictionary<string, string>? map)
            => map is null
                ? null
                : string.Join('\u001f', map
                    .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => $"{kv.Key}\u001e{kv.Value}"));
    }

    /// <summary>
    /// Formats a complete tool result: the content blocks, or — when they yield
    /// no text — the structured content serialised as JSON. The output is
    /// sanitised and capped at <see cref="McpTextSanitizer.MaxResultLength"/>.
    /// </summary>
    internal static string FormatToolResult(CallToolResult result)
    {
        var content = FormatToolContent(result.Content ?? [], sanitize: false);
        if (string.IsNullOrWhiteSpace(content)
            && result.StructuredContent is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } structured)
        {
            content = structured.GetRawText();
        }

        return McpTextSanitizer.SanitizeResult(content);
    }

    internal static string FormatToolContent(IEnumerable<ContentBlock> content)
        => FormatToolContent(content, sanitize: true);

    private static string FormatToolContent(IEnumerable<ContentBlock> content, bool sanitize)
    {
        var parts = new List<string>();
        foreach (var block in content)
        {
            switch (block)
            {
                case TextContentBlock text:
                    if (!string.IsNullOrEmpty(text.Text))
                    {
                        parts.Add(text.Text);
                    }
                    break;
                case ImageContentBlock image:
                    parts.Add($"[image content: mimeType={image.MimeType ?? "unknown"}, base64Length={image.Data.Length}]");
                    break;
                case AudioContentBlock audio:
                    parts.Add($"[audio content: mimeType={audio.MimeType ?? "unknown"}, base64Length={audio.Data.Length}]");
                    break;
                case ResourceLinkBlock link:
                    parts.Add($"[resource link: name={link.Name ?? "(unnamed)"}, uri={link.Uri}, mimeType={link.MimeType ?? "unknown"}, size={link.Size?.ToString() ?? "unknown"}]");
                    break;
                case EmbeddedResourceBlock { Resource: TextResourceContents textResource }:
                    // Prefix so embedded documents are distinguishable from the tool's own text.
                    parts.Add($"[resource uri={textResource.Uri}]\n{textResource.Text ?? string.Empty}");
                    break;
                case EmbeddedResourceBlock { Resource: BlobResourceContents blobResource }:
                    parts.Add($"[embedded resource: uri={blobResource.Uri}, mimeType={blobResource.MimeType ?? "unknown"}, base64Length={blobResource.Blob.Length}]");
                    break;
                case EmbeddedResourceBlock embedded:
                    parts.Add($"[embedded resource: type={embedded.Resource?.GetType().Name ?? "unknown"}]");
                    break;
                default:
                    parts.Add($"[{block.Type ?? block.GetType().Name} content]");
                    break;
            }
        }

        var joined = string.Join("\n", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
        return sanitize ? McpTextSanitizer.SanitizeResult(joined) : joined;
    }

    private ManagedConnection GetConnection(Guid connectionId)
    {
        if (!_connections.TryGetValue(connectionId, out var managed))
            throw new InvalidOperationException($"No MCP connection found with ID {connectionId}");
        return managed;
    }

    private sealed class ManagedConnection(
        Guid connectionId,
        string name,
        string transportType,
        McpConnectorOptions options,
        DateTime connectedAt)
    {
        private int _alive = 1;

        public Guid ConnectionId { get; } = connectionId;
        public string Name { get; } = name;
        public string TransportType { get; } = transportType;
        public McpConnectorOptions Options { get; } = options;
        public McpCredentialRedactor Credentials { get; } = McpCredentialRedactor.FromOptions(options);
        public DateTime ConnectedAt { get; } = connectedAt;
        public McpClient Client { get; set; } = null!;
        public int ToolCount { get; set; }
        public bool IsAlive => Volatile.Read(ref _alive) == 1;

        /// <summary>Set once the session transport's message channel has completed.</summary>
        public volatile bool TransportClosed;

        /// <summary>Set before we dispose the client ourselves so the closed signal is not reported as a fault.</summary>
        public volatile bool Closing;

        /// <summary>Returns true when the liveness flag actually changed.</summary>
        public bool TrySetAlive(bool alive)
            => Interlocked.Exchange(ref _alive, alive ? 1 : 0) != (alive ? 1 : 0);
    }

    /// <summary>
    /// Decorates a client transport so the manager learns when the session's
    /// inbound message channel completes — the SDK's only observable signal that
    /// the server side went away (process exit, closed pipe, dropped HTTP stream).
    /// The session transport itself is returned unwrapped, so SDK behaviour is
    /// untouched.
    /// </summary>
    private sealed class ObservedClientTransport(IClientTransport inner, Action onClosed) : IClientTransport
    {
        public string Name => inner.Name;

        public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default)
        {
            var session = await inner.ConnectAsync(cancellationToken);
            _ = session.MessageReader.Completion.ContinueWith(
                _ => onClosed(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return session;
        }
    }
}
