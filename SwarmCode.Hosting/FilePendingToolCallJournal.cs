using System.Text.Json;
using System.Text.Json.Serialization;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.ToolUseEngine;

namespace SwarmCode.Hosting;

/// <summary>
/// Tool-call journal persisted as one append-only JSON-lines file per conversation under
/// &lt;data&gt;/sessions/pending-tools/&lt;conversationId&gt;.jsonl. A <c>requested</c> line is
/// written per call before its batch is dispatched and a <c>started</c> line immediately
/// before the tool runs; loading folds the lines, and clearing deletes the file. The file
/// only ever holds the calls of the batch in flight, so it stays tiny.
/// </summary>
public sealed class FilePendingToolCallJournal : IPendingToolCallJournal
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly string _directory;
    private readonly object _gate = new();

    public FilePendingToolCallJournal(SwarmCodeDataPaths paths)
    {
        _directory = paths.PendingToolCallsDirectory;
        Directory.CreateDirectory(_directory);
    }

    public Task RecordRequestedAsync(string conversationId, IReadOnlyList<PendingToolCall> calls, CancellationToken ct = default)
    {
        if (calls.Count == 0) return Task.CompletedTask;
        AppendLines(conversationId, calls.Select(call => JsonSerializer.Serialize(Line.Requested(call), Json)));
        return Task.CompletedTask;
    }

    public Task RecordStartedAsync(string conversationId, string callId, CancellationToken ct = default)
    {
        AppendLines(conversationId, [JsonSerializer.Serialize(Line.Started(callId, DateTimeOffset.UtcNow), Json)]);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Appends and flushes through to disk: the started marker is the one fact that must
    /// survive a power loss, because without it the next turn reports the call as never run.
    /// </summary>
    private void AppendLines(string conversationId, IEnumerable<string> lines)
    {
        lock (_gate)
        {
            using var stream = new FileStream(PathFor(conversationId), FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream);
            foreach (var line in lines) writer.WriteLine(line);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
    }

    public Task<IReadOnlyList<PendingToolCall>> LoadAsync(string conversationId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var path = PathFor(conversationId);
            if (!File.Exists(path)) return Task.FromResult<IReadOnlyList<PendingToolCall>>([]);
            var calls = new Dictionary<string, PendingToolCall>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var raw in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                Line? line;
                try { line = JsonSerializer.Deserialize<Line>(raw, Json); }
                catch (JsonException) { continue; }
                if (line?.CallId is not { Length: > 0 } id) continue;
                if (line.Kind == LineKind.Requested)
                {
                    if (!calls.ContainsKey(id)) order.Add(id);
                    calls[id] = new PendingToolCall(id, line.ToolName ?? string.Empty, line.InputJson ?? "{}", line.SideEffect, line.At, calls.GetValueOrDefault(id)?.StartedUtc);
                }
                else if (line.Kind == LineKind.Started)
                {
                    if (calls.TryGetValue(id, out var existing))
                        calls[id] = existing with { StartedUtc = existing.StartedUtc ?? line.At };
                    else
                    {
                        // Started before its request line (native early dispatch); keep the fact.
                        order.Add(id);
                        calls[id] = new PendingToolCall(id, string.Empty, "{}", null, line.At, line.At);
                    }
                }
            }
            return Task.FromResult<IReadOnlyList<PendingToolCall>>(order.Select(id => calls[id]).ToList());
        }
    }

    public Task ClearAsync(string conversationId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var path = PathFor(conversationId);
            if (File.Exists(path)) File.Delete(path);
        }
        return Task.CompletedTask;
    }

    private string PathFor(string conversationId)
    {
        StorageIds.Validate(conversationId);
        return Path.Combine(_directory, conversationId + ".jsonl");
    }

    private enum LineKind { Requested, Started }

    private sealed class Line
    {
        public LineKind Kind { get; set; }
        public string CallId { get; set; } = string.Empty;
        public string? ToolName { get; set; }
        public string? InputJson { get; set; }
        public ToolSideEffect? SideEffect { get; set; }
        public DateTimeOffset At { get; set; }

        public static Line Requested(PendingToolCall call) => new()
        {
            Kind = LineKind.Requested, CallId = call.CallId, ToolName = call.ToolName, InputJson = call.InputJson,
            SideEffect = call.SideEffect, At = call.RequestedUtc,
        };

        public static Line Started(string callId, DateTimeOffset at) => new() { Kind = LineKind.Started, CallId = callId, At = at };
    }
}
