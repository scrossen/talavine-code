namespace SwarmCore.LlmRouting.ToolUse;

/// <summary>
/// Lightweight Server-Sent Events parser. Handles both named events
/// (<c>event: X\ndata: Y\n\n</c>, used by Anthropic) and data-only events
/// (<c>data: Y\n\n</c>, used by OpenAI / OpenAI-compat endpoints).
/// </summary>
internal static class SseParser
{
    public sealed record SseEvent(string? Name, string Data);

    /// <summary>
    /// Read the SSE stream line-by-line and yield <see cref="SseEvent"/>s as
    /// each complete record arrives (blank line terminates a record).
    /// </summary>
    public static async IAsyncEnumerable<SseEvent> ParseAsync(
        Stream stream,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream);
        string? eventName = null;
        var dataBuf = new System.Text.StringBuilder();

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;

            if (line.Length == 0)
            {
                // Blank line → dispatch accumulated record.
                if (dataBuf.Length > 0)
                {
                    yield return new SseEvent(eventName, dataBuf.ToString());
                }
                eventName = null;
                dataBuf.Clear();
                continue;
            }

            // Skip SSE comments.
            if (line.StartsWith(':')) continue;

            var colon = line.IndexOf(':');
            if (colon < 0) continue; // malformed — ignore
            var field = line[..colon];
            var value = colon + 1 < line.Length ? line[(colon + 1)..].TrimStart(' ') : "";

            switch (field)
            {
                case "event":
                    eventName = value;
                    break;
                case "data":
                    if (dataBuf.Length > 0) dataBuf.Append('\n');
                    dataBuf.Append(value);
                    break;
                // id / retry fields ignored.
            }
        }

        // Emit any trailing record (no terminating blank line).
        if (dataBuf.Length > 0)
        {
            yield return new SseEvent(eventName, dataBuf.ToString());
        }
    }
}
