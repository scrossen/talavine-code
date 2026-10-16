using System.Text;
using System.Text.Json.Nodes;

namespace SwarmCore.Agent.AppControl;

/// <summary>A request header or body larger than the server accepts; the server answers 413 and closes.</summary>
internal sealed class LoopbackRequestTooLargeException(string message) : IOException(message);

/// <summary>
/// The minimal HTTP/1.1 request reader shared by the in-process loopback servers (<see cref="LoopbackMcpServer"/>
/// and <see cref="LoopbackJsonEndpoint"/>): Content-Length or chunked bodies, bounded header and body sizes.
/// </summary>
internal sealed record LoopbackHttpRequest(string Method, string Path, Dictionary<string, string> Headers, string Body, bool Http11 = true)
{
    public const int MaxHeaderBytes = 32 * 1024;

    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;

    public static async Task<LoopbackHttpRequest?> ReadAsync(Stream stream, int maxBodyBytes, CancellationToken ct)
    {
        var header = new MemoryStream();
        var one = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(one, ct).ConfigureAwait(false);
            if (read == 0) return null;
            header.WriteByte(one[0]);
            if (header.Length > MaxHeaderBytes) throw new LoopbackRequestTooLargeException("Request header too large.");
            if (header.Length >= 4 && EndsWithBlankLine(header)) break;
        }
        var lines = Encoding.ASCII.GetString(header.ToArray(), 0, (int)header.Length - 4).Split("\r\n", StringSplitOptions.None);
        var start = lines[0].Split(' ');
        if (start.Length < 2) throw new IOException("Malformed request line.");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        var path = start[1];
        var query = path.IndexOf('?');
        if (query >= 0) path = path[..query];
        var http11 = !start.ElementAtOrDefault(2)?.StartsWith("HTTP/1.0", StringComparison.Ordinal) ?? true;
        if (headers.TryGetValue("transfer-encoding", out var te) && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            return new(start[0].ToUpperInvariant(), path, headers,
                Encoding.UTF8.GetString(await ReadChunkedAsync(stream, maxBodyBytes, ct).ConfigureAwait(false)), http11);
        var length = headers.TryGetValue("content-length", out var cl) && int.TryParse(cl, out var n) ? n : 0;
        if (length < 0) throw new IOException("Malformed Content-Length.");
        if (length > maxBodyBytes) throw new LoopbackRequestTooLargeException("Request body too large.");
        var body = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(body.AsMemory(offset), ct).ConfigureAwait(false);
            if (read == 0) return null;
            offset += read;
        }
        return new(start[0].ToUpperInvariant(), path, headers, Encoding.UTF8.GetString(body), http11);
    }

    /// <summary>True when the client asked to keep the connection open (HTTP/1.1 default, HTTP/1.0 opt-in).</summary>
    public bool KeepAlive
    {
        get
        {
            var connection = Header("connection");
            return Http11
                ? !string.Equals(connection, "close", StringComparison.OrdinalIgnoreCase)
                : string.Equals(connection, "keep-alive", StringComparison.OrdinalIgnoreCase);
        }
    }

    // HttpClient sends JSON content chunked when it has no precomputed length (the MCP SDK does).
    private static async Task<byte[]> ReadChunkedAsync(Stream stream, int maxBodyBytes, CancellationToken ct)
    {
        var body = new MemoryStream();
        while (true)
        {
            var sizeLine = await ReadLineAsync(stream, ct).ConfigureAwait(false);
            var semicolon = sizeLine.IndexOf(';');
            if (semicolon >= 0) sizeLine = sizeLine[..semicolon];
            if (!int.TryParse(sizeLine.Trim(), System.Globalization.NumberStyles.HexNumber, null, out var size) || size < 0)
                throw new IOException("Malformed chunk size.");
            if (size == 0)
            {
                while ((await ReadLineAsync(stream, ct).ConfigureAwait(false)).Length > 0) { } // trailers
                return body.ToArray();
            }
            if (body.Length + size > maxBodyBytes) throw new LoopbackRequestTooLargeException("Request body too large.");
            var chunk = new byte[size];
            var offset = 0;
            while (offset < size)
            {
                var read = await stream.ReadAsync(chunk.AsMemory(offset), ct).ConfigureAwait(false);
                if (read == 0) throw new IOException("Connection closed inside a chunk.");
                offset += read;
            }
            body.Write(chunk);
            await ReadLineAsync(stream, ct).ConfigureAwait(false); // CRLF after the chunk
        }
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var line = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(one, ct).ConfigureAwait(false) == 0) throw new IOException("Connection closed.");
            if (one[0] == '\n') return line.ToString().TrimEnd('\r');
            if (line.Length > 1024) throw new IOException("Line too long.");
            line.Append((char)one[0]);
        }
    }

    private static bool EndsWithBlankLine(MemoryStream buffer)
    {
        var b = buffer.GetBuffer();
        var n = (int)buffer.Length;
        return b[n - 4] == '\r' && b[n - 3] == '\n' && b[n - 2] == '\r' && b[n - 1] == '\n';
    }
}

/// <summary>A complete HTTP/1.1 response with a fixed Content-Length.</summary>
internal sealed record LoopbackHttpResponse(int Status, string ContentType, byte[] Body, (string Name, string Value)[] Headers)
{
    public static LoopbackHttpResponse Text(int status, string text, params (string, string)[] headers)
        => new(status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text), headers);

    public static LoopbackHttpResponse Json(int status, JsonNode body, params (string, string)[] headers)
        => new(status, "application/json", Encoding.UTF8.GetBytes(body.ToJsonString()), headers);

    public static LoopbackHttpResponse Empty(int status) => new(status, "text/plain", [], []);

    public async Task WriteAsync(Stream stream, bool keepAlive, CancellationToken ct)
    {
        var head = new StringBuilder()
            .Append("HTTP/1.1 ").Append(Status).Append(' ').Append(Reason(Status)).Append("\r\n")
            .Append("Content-Type: ").Append(ContentType).Append("\r\n")
            .Append("Content-Length: ").Append(Body.Length).Append("\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("Connection: ").Append(keepAlive ? "keep-alive" : "close").Append("\r\n");
        foreach (var (name, value) in Headers) head.Append(name).Append(": ").Append(value).Append("\r\n");
        head.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct).ConfigureAwait(false);
        if (Body.Length > 0) await stream.WriteAsync(Body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK", 202 => "Accepted", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden",
        404 => "Not Found", 405 => "Method Not Allowed", 409 => "Conflict", 413 => "Content Too Large",
        415 => "Unsupported Media Type", 421 => "Misdirected Request", 422 => "Unprocessable Content", 500 => "Internal Server Error", _ => "Status",
    };
}

/// <summary>Host and Origin checks shared by the loopback servers (DNS-rebinding protection).</summary>
internal static class LoopbackAuthority
{
    public static bool IsLoopbackHost(string? host, int port)
        => host is not null && (host == $"127.0.0.1:{port}" || host == $"localhost:{port}" || host == $"[::1]:{port}");

    public static bool IsLoopbackOrigin(string origin, int port)
        => Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.IsLoopback && uri.Port == port;

    public static bool BearerMatches(string? header, byte[] token)
    {
        if (header is null || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        var presented = Encoding.UTF8.GetBytes(header["Bearer ".Length..].Trim());
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(presented, token);
    }
}
