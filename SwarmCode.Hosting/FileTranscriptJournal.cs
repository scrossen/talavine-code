using System.Text.Json;
using System.Text.Json.Serialization;

namespace SwarmCode.Hosting;

/// <summary>What a journal file folds to: the visible entries and where reconciliation against the provider transcript may start.</summary>
/// <param name="Entries">Entries in display order.</param>
/// <param name="ReconcileFromMessage">Index of the first provider-transcript message that a reconciliation may add cards for; set by the last clear marker, zero otherwise.</param>
public sealed record TranscriptJournalContents(IReadOnlyList<TranscriptEntry> Entries, int ReconcileFromMessage);

/// <summary>
/// Append-only presentation journal: one JSON line per finalized transcript entry under
/// &lt;data&gt;/sessions/presentation/&lt;conversationId&gt;.jsonl. The runner appends an entry
/// the moment it is final (a user message, a completed tool card, a notice, a closed
/// assistant bubble, an answered permission) and never rewrites a line. Reopening a
/// session folds the lines back into entries, so tool cards, lane notices and hand-offs
/// survive a restart. A later line with the same id replaces the earlier one. A
/// <c>clear</c> marker discards everything before it and records how long the provider
/// transcript was at that moment, so reconciliation never resurrects cleared history;
/// writing the marker also truncates the file to just that line.
/// </summary>
public sealed class FileTranscriptJournal
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly string _directory;
    private readonly object _gate = new();

    public FileTranscriptJournal(SwarmCodeDataPaths paths)
    {
        _directory = Path.Combine(paths.SessionsDirectory, "presentation");
        Directory.CreateDirectory(_directory);
    }

    /// <summary>True when the conversation has a journal file; a session created before the journal existed has none.</summary>
    public bool Exists(string conversationId)
    {
        lock (_gate) return File.Exists(PathFor(conversationId));
    }

    /// <summary>Serializes an entry's current state to one journal line, so a caller can capture it now and write it later.</summary>
    public static string Serialize(TranscriptEntry entry) => JsonSerializer.Serialize(Line.From(entry), Json);

    public void Append(string conversationId, TranscriptEntry entry) => AppendLines(conversationId, [Serialize(entry)]);

    public void AppendRange(string conversationId, IEnumerable<TranscriptEntry> entries)
        => AppendLines(conversationId, entries.Select(Serialize).ToList());

    /// <summary>Appends lines produced earlier by <see cref="Serialize"/>, in order.</summary>
    public void AppendLines(string conversationId, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return;
        lock (_gate)
        {
            using var writer = new StreamWriter(PathFor(conversationId), append: true);
            foreach (var line in lines) writer.WriteLine(line);
        }
    }

    /// <summary>
    /// Marks the visible transcript as cleared. <paramref name="transcriptMessageCount"/> is the
    /// provider transcript's length at this moment; reconciliation ignores messages before it.
    /// The file is rewritten to hold only the marker, since everything before it is dead.
    /// </summary>
    public void AppendClear(string conversationId, int transcriptMessageCount)
    {
        var line = JsonSerializer.Serialize(new Line
        {
            Kind = LineKind.Clear, Id = Guid.NewGuid().ToString("N"), AtUtc = DateTime.UtcNow, TranscriptMessageCount = transcriptMessageCount,
        }, Json);
        lock (_gate)
        {
            var path = PathFor(conversationId);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, line + Environment.NewLine);
            SwarmCore.Abstractions.Storage.TransientFileLock.Retry(() => File.Move(tmp, path, overwrite: true), accessDeniedIsTransient: true);
        }
    }

    public TranscriptJournalContents Load(string conversationId)
    {
        lock (_gate)
        {
            var path = PathFor(conversationId);
            if (!File.Exists(path)) return new([], 0);
            var order = new List<string>();
            var byId = new Dictionary<string, TranscriptEntry>(StringComparer.Ordinal);
            var reconcileFrom = 0;
            foreach (var raw in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                Line? line;
                try { line = JsonSerializer.Deserialize<Line>(raw, Json); }
                catch (JsonException) { continue; }
                if (line is null) continue;
                if (line.Kind == LineKind.Clear)
                {
                    order.Clear();
                    byId.Clear();
                    reconcileFrom = Math.Max(reconcileFrom, line.TranscriptMessageCount ?? 0);
                    continue;
                }
                var entry = line.ToEntry();
                if (entry is null || string.IsNullOrEmpty(entry.Id)) continue;
                if (!byId.ContainsKey(entry.Id)) order.Add(entry.Id);
                byId[entry.Id] = entry;
            }
            return new(order.Select(id => byId[id]).ToList(), reconcileFrom);
        }
    }

    public void Delete(string conversationId)
    {
        lock (_gate)
        {
            var path = PathFor(conversationId);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private string PathFor(string conversationId)
    {
        StorageIds.Validate(conversationId);
        return Path.Combine(_directory, conversationId + ".jsonl");
    }

    private enum LineKind { User, Assistant, Tool, Notice, Permission, Clear, Changes, Question }

    private sealed class Line
    {
        public LineKind Kind { get; set; }
        public string Id { get; set; } = string.Empty;
        public DateTime AtUtc { get; set; }
        public string? Text { get; set; }
        public bool? IsContinuation { get; set; }
        public string? Reasoning { get; set; }
        public string? LaneName { get; set; }
        public double? DurationMs { get; set; }
        public string? ToolCallId { get; set; }
        public string? ToolName { get; set; }
        public string? InputJson { get; set; }
        public string? Result { get; set; }
        public ToolCallState? State { get; set; }
        public double? ElapsedMs { get; set; }
        public NoticeKind? NoticeKind { get; set; }
        public string? Details { get; set; }
        public string? RequestId { get; set; }
        public string? Title { get; set; }
        public IReadOnlyList<string>? Permissions { get; set; }
        public bool? Approved { get; set; }
        public string? SkillName { get; set; }
        public string? FunctionName { get; set; }
        public IReadOnlyList<SwarmCore.Skills.Models.PermissionLevel>? Levels { get; set; }
        public SwarmCore.Skills.Models.PermissionGrantScope? Scope { get; set; }
        public bool? GrantRevoked { get; set; }
        public int? TranscriptMessageCount { get; set; }
        public IReadOnlyList<SwarmCore.Coding.Files.CodingAttachment>? Attachments { get; set; }
        public string? CheckpointId { get; set; }
        public string? RepositoryRoot { get; set; }
        public IReadOnlyList<SwarmCore.Coding.Checkpoints.TurnFileChange>? Files { get; set; }
        public bool? HeadMoved { get; set; }
        public bool? Undone { get; set; }
        public string? Question { get; set; }
        public string? QuestionContext { get; set; }
        public IReadOnlyList<QuestionOption>? Options { get; set; }
        public bool? AllowMultiple { get; set; }
        public bool? AllowFreeText { get; set; }
        public string? Answer { get; set; }
        public IReadOnlyList<string>? Selected { get; set; }
        public bool? Answered { get; set; }

        public static Line From(TranscriptEntry entry) => entry switch
        {
            UserEntry u => new() { Kind = LineKind.User, Id = u.Id, AtUtc = u.AtUtc, Text = u.Text, IsContinuation = u.IsContinuation ? true : null, Attachments = u.Attachments.Count > 0 ? u.Attachments : null },
            AssistantEntry a => new()
            {
                Kind = LineKind.Assistant, Id = a.Id, AtUtc = a.AtUtc, Text = a.Text,
                Reasoning = a.Reasoning.Length > 0 ? a.Reasoning : null, LaneName = a.LaneName, DurationMs = a.Duration?.TotalMilliseconds,
            },
            ToolCallEntry t => new()
            {
                Kind = LineKind.Tool, Id = t.Id, AtUtc = t.AtUtc, ToolCallId = t.ToolCallId, ToolName = t.ToolName, InputJson = t.InputJson,
                // A card journalled while still running has no outcome; it replays as unknown with an explicit note.
                Result = t.State == ToolCallState.Running ? t.Result ?? TranscriptHistory.InterruptedToolResult : t.Result,
                State = t.State == ToolCallState.Running ? ToolCallState.Unknown : t.State, ElapsedMs = t.Elapsed?.TotalMilliseconds,
            },
            NoticeEntry n => new() { Kind = LineKind.Notice, Id = n.Id, AtUtc = n.AtUtc, NoticeKind = n.Kind, Text = n.Text, Details = n.Details },
            PermissionEntry p => new()
            {
                Kind = LineKind.Permission, Id = p.Id, AtUtc = p.AtUtc, RequestId = p.RequestId, Title = p.Title, Permissions = p.Permissions, Approved = p.Approved,
                SkillName = p.SkillName.Length > 0 ? p.SkillName : null, FunctionName = p.FunctionName.Length > 0 ? p.FunctionName : null,
                Levels = p.Levels.Count > 0 ? p.Levels : null, Scope = p.Scope, GrantRevoked = p.GrantRevoked ? true : null,
            },
            TurnChangesEntry c => new()
            {
                Kind = LineKind.Changes, Id = c.Id, AtUtc = c.AtUtc, CheckpointId = c.CheckpointId, RepositoryRoot = c.RepositoryRoot,
                Files = c.Files, HeadMoved = c.HeadMoved ? true : null, Undone = c.Undone ? true : null,
            },
            QuestionEntry q => new()
            {
                Kind = LineKind.Question, Id = q.Id, AtUtc = q.AtUtc, RequestId = q.RequestId, Question = q.Question, QuestionContext = q.Context,
                Options = q.Options.Count > 0 ? q.Options : null, AllowMultiple = q.AllowMultiple ? true : null, AllowFreeText = q.AllowFreeText ? null : false,
                Answer = q.Answer, Selected = q.Selected.Count > 0 ? q.Selected : null,
                // Journalled while still pending (a crash): nobody can answer after a restart, so it replays as skipped.
                Answered = q.Answered ?? false,
            },
            _ => throw new NotSupportedException($"Unknown transcript entry {entry.GetType().Name}."),
        };

        public TranscriptEntry? ToEntry()
        {
            switch (Kind)
            {
                case LineKind.User:
                    return new UserEntry { Id = Id, AtUtc = AtUtc, Text = Text ?? string.Empty, IsContinuation = IsContinuation == true, Attachments = Attachments ?? [] };
                case LineKind.Assistant:
                {
                    var a = new AssistantEntry
                    {
                        Id = Id, AtUtc = AtUtc, IsStreaming = false, LaneName = LaneName,
                        Duration = DurationMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null,
                    };
                    a.ReplaceText(Text ?? string.Empty);
                    if (!string.IsNullOrEmpty(Reasoning)) a.AppendReasoning(Reasoning);
                    return a;
                }
                case LineKind.Tool:
                    return new ToolCallEntry
                    {
                        Id = Id, AtUtc = AtUtc, ToolCallId = ToolCallId ?? string.Empty, ToolName = ToolName ?? string.Empty,
                        InputJson = InputJson ?? "{}", Result = Result, State = State ?? ToolCallState.Failed,
                        Elapsed = ElapsedMs is { } e ? TimeSpan.FromMilliseconds(e) : null,
                    };
                case LineKind.Notice:
                    return new NoticeEntry { Id = Id, AtUtc = AtUtc, Kind = NoticeKind ?? Hosting.NoticeKind.Info, Text = Text ?? string.Empty, Details = Details };
                case LineKind.Permission:
                    return new PermissionEntry
                    {
                        Id = Id, AtUtc = AtUtc, RequestId = RequestId ?? string.Empty, Title = Title ?? string.Empty,
                        Permissions = Permissions ?? [], Approved = Approved,
                        SkillName = SkillName ?? string.Empty, FunctionName = FunctionName ?? string.Empty,
                        Levels = Levels ?? [], Scope = Scope, GrantRevoked = GrantRevoked == true,
                    };
                case LineKind.Changes:
                    return string.IsNullOrEmpty(CheckpointId) || string.IsNullOrEmpty(RepositoryRoot) ? null : new TurnChangesEntry
                    {
                        Id = Id, AtUtc = AtUtc, CheckpointId = CheckpointId, RepositoryRoot = RepositoryRoot,
                        Files = Files ?? [], HeadMoved = HeadMoved == true, Undone = Undone == true,
                    };
                case LineKind.Question:
                    return string.IsNullOrEmpty(Question) ? null : new QuestionEntry
                    {
                        Id = Id, AtUtc = AtUtc, RequestId = RequestId ?? string.Empty, Question = Question, Context = QuestionContext,
                        Options = Options ?? [], AllowMultiple = AllowMultiple == true, AllowFreeText = AllowFreeText != false,
                        Answer = Answer, Selected = Selected ?? [], Answered = Answered ?? false,
                    };
                default:
                    return null;
            }
        }
    }
}
