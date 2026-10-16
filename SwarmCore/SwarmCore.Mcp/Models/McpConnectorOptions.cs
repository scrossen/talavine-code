namespace SwarmCore.Mcp.Models;

/// <summary>
/// Options for connecting to an MCP server.
/// </summary>
public class McpConnectorOptions
{
    /// <summary>Default per-request timeout applied to tools/list and tools/call.</summary>
    public static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Human-readable name for this connector.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Transport type: "stdio" for local subprocess servers, or one of the HTTP
    /// family ("http" auto-detects streamable HTTP vs SSE, "sse" and
    /// "streamable-http" pin the protocol). See <see cref="McpTransportTypes"/>.
    /// </summary>
    public required string TransportType { get; init; }

    /// <summary>
    /// Upper bound for a single tools/list or tools/call round trip. Callers that
    /// pass <see cref="CancellationToken.None"/> are still protected from a server
    /// that never answers. Defaults to <see cref="DefaultCallTimeout"/> (60 s).
    /// Values of zero or less disable the timeout.
    /// </summary>
    public TimeSpan CallTimeout { get; init; } = DefaultCallTimeout;

    // ── STDIO transport settings ────────────────────────────────────

    /// <summary>
    /// Command to launch the MCP server process (e.g., "dotnet", "npx", "node").
    /// </summary>
    public string? Command { get; init; }

    /// <summary>
    /// Arguments passed to the command.
    /// </summary>
    public string[]? Arguments { get; init; }

    /// <summary>
    /// Working directory for the subprocess.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Environment variables to set for the subprocess. These are layered on top
    /// of a small allowlist of host variables (PATH, HOME, TEMP, …); the host's
    /// full environment is <b>not</b> inherited, so host secrets never reach a
    /// third-party server unless listed here explicitly.
    /// </summary>
    public Dictionary<string, string>? EnvironmentVariables { get; init; }

    // ── HTTP transport settings ─────────────────────────────────────

    /// <summary>
    /// Absolute http(s) URL of the MCP server endpoint. Required for the HTTP transport family.
    /// </summary>
    public string? Url { get; init; }

    /// <summary>
    /// Bearer token sent as <c>Authorization: Bearer &lt;key&gt;</c> on every HTTP request.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Extra HTTP headers to send with every request. An explicit "Authorization"
    /// entry here takes precedence over <see cref="ApiKey"/>.
    /// </summary>
    public Dictionary<string, string>? Headers { get; init; }
}
