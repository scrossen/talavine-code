namespace SwarmCore.Mcp.Models;

/// <summary>
/// Canonical MCP transport type names plus alias-tolerant classification helpers.
/// Configs may say "http", "sse", or "streamable-http" (any casing) — all three are
/// served by the SDK's HTTP client transport; the specific value picks the HTTP
/// transport mode (auto-detect / SSE-only / streamable-HTTP-only).
/// </summary>
public static class McpTransportTypes
{
    public const string Stdio = "stdio";
    public const string Http = "http";
    public const string Sse = "sse";
    public const string StreamableHttp = "streamable-http";

    /// <summary>Human-readable list of accepted transport values for error messages.</summary>
    public const string SupportedList = "'stdio', 'http', 'sse', 'streamable-http'";

    public static bool IsStdio(string? transportType)
        => string.Equals(transportType?.Trim(), Stdio, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the transport type names any HTTP-family transport
    /// ("http", "sse", "streamable-http", or the no-dash "streamablehttp").
    /// </summary>
    public static bool IsHttp(string? transportType)
    {
        var normalized = transportType?.Trim();
        return string.Equals(normalized, Http, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, Sse, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, StreamableHttp, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "streamablehttp", StringComparison.OrdinalIgnoreCase);
    }
}
