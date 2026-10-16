using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.ToolUseEngine;
using SwarmCore.Coding.Routing;

namespace SwarmCode.Hosting;

/// <summary>
/// Provider transcript persisted as JSON lines per conversation under
/// &lt;data&gt;/sessions/transcripts/&lt;conversationId&gt;.jsonl so a session resumes with its
/// full tool history after a restart. Uses SwarmCore's own transcript JSON format.
/// Also the <see cref="ITranscriptRewriter"/> the orchestrator uses to replace a lane's
/// refusal with a neutral note before the next lane reads the transcript.
/// </summary>
public sealed class FileAgentSessionStore : IAgentSessionStore, ITranscriptRewriter
{
    private readonly string _directory;
    private readonly object _gate = new();
    private readonly Dictionary<string, List<ChatMessage>> _cache = new();
    private readonly ILogger<FileAgentSessionStore> _logger;

    public FileAgentSessionStore(SwarmCodeDataPaths paths, ILogger<FileAgentSessionStore>? logger = null)
    {
        _directory = paths.TranscriptsDirectory;
        _logger = logger ?? NullLogger<FileAgentSessionStore>.Instance;
        Directory.CreateDirectory(_directory);
    }

    public Task<List<ChatMessage>> LoadAsync(string conversationId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(new List<ChatMessage>(Load(conversationId)));
    }

    public Task AppendAsync(string conversationId, ChatMessage message, CancellationToken ct = default)
        => AppendRangeAsync(conversationId, [message], ct);

    public Task AppendRangeAsync(string conversationId, IReadOnlyList<ChatMessage> messages, CancellationToken ct = default)
    {
        if (messages.Count == 0) return Task.CompletedTask;
        lock (_gate)
        {
            var list = Load(conversationId);
            var path = PathFor(conversationId);
            // A torn final line (a crash mid-write) has no newline; appending straight
            // after it would glue the next message onto the damaged one and lose both.
            var tornTail = !EndsWithNewline(path);
            using (var writer = new StreamWriter(path, append: true))
            {
                if (tornTail) writer.WriteLine();
                foreach (var m in messages) writer.WriteLine(Encode(m));
            }
            list.AddRange(messages);
        }
        return Task.CompletedTask;
    }

    public Task ClearAsync(string conversationId)
    {
        lock (_gate)
        {
            _cache.Remove(conversationId);
            var path = PathFor(conversationId);
            if (File.Exists(path)) File.Delete(path);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Replaces the text of the latest assistant message that equals or contains
    /// <paramref name="matchText"/>. Tool-use parts in that message are kept so receipts
    /// still pair with their results. The file is rewritten atomically.
    /// </summary>
    public Task<bool> ReplaceLastAssistantTextAsync(string conversationId, string matchText, string replacementText, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(matchText)) return Task.FromResult(false);
        var needle = matchText.Trim();
        lock (_gate)
        {
            var list = Load(conversationId);
            for (var i = list.Count - 1; i >= 0; i--)
            {
                var message = list[i];
                if (message.Role != ChatRole.Assistant) continue;
                var text = string.Concat(message.Parts.OfType<TextPart>().Select(p => p.Text)).Trim();
                if (text.Length == 0) continue;
                if (!string.Equals(text, needle, StringComparison.Ordinal) && !text.Contains(needle, StringComparison.Ordinal)) return Task.FromResult(false);

                var parts = new List<ChatContentPart> { new TextPart(replacementText) };
                parts.AddRange(message.Parts.Where(p => p is not TextPart and not ReasoningPart));
                list[i] = new ChatMessage(ChatRole.Assistant, parts);
                Rewrite(conversationId, list);
                return Task.FromResult(true);
            }
        }
        return Task.FromResult(false);
    }

    private void Rewrite(string conversationId, List<ChatMessage> messages)
    {
        var path = PathFor(conversationId);
        var tmp = path + ".tmp";
        using (var writer = new StreamWriter(tmp, append: false))
        {
            foreach (var m in messages) writer.WriteLine(Encode(m));
        }
        File.Move(tmp, path, overwrite: true);
        _cache[conversationId] = messages;
    }

    private List<ChatMessage> Load(string conversationId)
    {
        if (_cache.TryGetValue(conversationId, out var cached)) return cached;
        var list = new List<ChatMessage>();
        var path = PathFor(conversationId);
        if (File.Exists(path))
        {
            var dropped = 0;
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var decoded = TryDecode(line);
                if (decoded is not null) list.Add(decoded);
                else dropped++;
            }
            // A damaged line is skipped rather than failing the whole conversation, so the
            // rest of the history still resumes; the loss is reported once per load.
            if (dropped > 0)
                _logger.LogWarning(
                    "Skipped {Dropped} unreadable line(s) in the provider transcript for conversation {ConversationId}; "
                    + "the remaining {Kept} message(s) were loaded.", dropped, conversationId, list.Count);
        }
        _cache[conversationId] = list;
        return list;
    }

    private static bool EndsWithNewline(string path)
    {
        if (!File.Exists(path)) return true;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length == 0) return true;
        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() == '\n';
    }

    private string PathFor(string conversationId)
    {
        StorageIds.Validate(conversationId);
        return Path.Combine(_directory, conversationId + ".jsonl");
    }

    internal static string Encode(ChatMessage message)
        => new JsonObject
        {
            ["role"] = ChatTranscriptJson.RoleToString(message.Role),
            ["parts"] = JsonNode.Parse(ChatTranscriptJson.Serialize(message.Parts)),
        }.ToJsonString();

    internal static ChatMessage? TryDecode(string line)
    {
        try
        {
            if (JsonNode.Parse(line) is not JsonObject obj || obj["parts"] is not JsonNode parts) return null;
            var role = ChatTranscriptJson.RoleFromString((string?)obj["role"] ?? "user");
            return new ChatMessage(role, ChatTranscriptJson.Deserialize(parts.ToJsonString()));
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or ArgumentException or FormatException)
        {
            return null;
        }
    }
}
