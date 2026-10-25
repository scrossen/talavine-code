using System.Text.Json;

namespace SwarmCode.Hosting;

/// <summary>Atomic presentation journal. Loading it never resumes an action automatically.</summary>
public sealed class FileConversationJobStore(SwarmCodeDataPaths paths)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();

    public ConversationJob? Load(string conversationId)
    {
        lock (_gate)
        {
            var path = PathFor(conversationId);
            return File.Exists(path) ? JsonSerializer.Deserialize<ConversationJob>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException("The saved job could not be read.") : null;
        }
    }

    public void Save(string conversationId, ConversationJob job)
    {
        lock (_gate)
        {
            var path = PathFor(conversationId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, job, Json);
                    stream.Flush(flushToDisk: true);
                }
                SwarmCore.Abstractions.Storage.TransientFileLock.Retry(
                    () => File.Move(temp, path, overwrite: true), accessDeniedIsTransient: true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    public void Delete(string conversationId)
    {
        lock (_gate) File.Delete(PathFor(conversationId));
    }

    private string PathFor(string conversationId)
    {
        StorageIds.Validate(conversationId);
        return Path.Combine(paths.SessionsDirectory, "jobs", conversationId + ".json");
    }
}

public sealed record ConversationJob
{
    public string State { get; init; } = "Idle";
    public bool IsGoal { get; init; }
    public SwarmCore.Abstractions.Collaboration.TurnAssistanceOptions? Assistance { get; init; }
    public bool OpinionsObtained { get; init; }
    public string? ActiveLane { get; init; }
    public string? ActiveModel { get; init; }
    public DateTime? StartedUtc { get; init; }
    public DateTime UpdatedUtc { get; init; } = DateTime.UtcNow;
    public string? Request { get; init; }
    public SwarmCore.Coding.Routing.CodingContinuationContext? Continuation { get; init; }
    public IReadOnlyList<SwarmCore.Coding.Files.CodingAttachment> Attachments { get; init; } = [];
    public bool QueuePaused { get; init; }
    public TimeSpan Elapsed { get; init; }
    public int CompletedToolCalls { get; init; }
    public long? ReportedTokens { get; init; }
    public decimal? ReportedCostUsd { get; init; }
    public SwarmCore.Coding.Routing.CodingAcceptanceResult? Acceptance { get; init; }
    public IReadOnlyList<ConversationRunner.QueuedTurn> Queue { get; init; } = [];
    public IReadOnlyList<SavedTranscriptEntry> Entries { get; init; } = [];
}

/// <summary>Values, rather than mutable streaming objects, cross the persistence boundary.</summary>
public sealed record SavedTranscriptEntry
{
    public string Kind { get; init; } = "notice";
    public DateTime AtUtc { get; init; }
    public string Text { get; init; } = "";
    public IReadOnlyList<SwarmCore.Coding.Files.CodingAttachment> Attachments { get; init; } = [];
    public string? Reasoning { get; init; }
    public string? Lane { get; init; }
    public bool Continuation { get; init; }
    public TimeSpan? Duration { get; init; }
    public string? ToolCallId { get; init; }
    public string? InputJson { get; init; }
    public string? Result { get; init; }
    public ToolCallState ToolState { get; init; }
    public NoticeKind NoticeKind { get; init; }
    public string? Details { get; init; }

    public static SavedTranscriptEntry? From(TranscriptEntry entry) => entry switch
    {
        UserEntry u => new() { Kind = "user", AtUtc = u.AtUtc, Text = u.Text, Continuation = u.IsContinuation, Attachments = u.Attachments },
        AssistantEntry a => new() { Kind = "assistant", AtUtc = a.AtUtc, Text = a.Text, Reasoning = a.Reasoning, Lane = a.LaneName, Duration = a.Duration },
        ToolCallEntry t => new() { Kind = "tool", AtUtc = t.AtUtc, Text = t.ToolName, ToolCallId = t.ToolCallId, InputJson = t.InputJson, Result = t.Result, ToolState = t.State, Duration = t.Elapsed },
        NoticeEntry n => new() { Kind = "notice", AtUtc = n.AtUtc, Text = n.Text, NoticeKind = n.Kind, Details = n.Details },
        _ => null, // Pending approvals are process-bound and must never be reauthorized on restore.
    };

    public TranscriptEntry Restore()
    {
        if (Kind == "user") return new UserEntry { AtUtc = AtUtc, Text = Text, IsContinuation = Continuation, Attachments = Attachments };
        if (Kind == "tool") return new ToolCallEntry { AtUtc = AtUtc, ToolCallId = ToolCallId ?? "unknown", ToolName = Text, InputJson = InputJson ?? "{}", Result = Result ?? (ToolState == ToolCallState.Running ? "Outcome unknown: execution was interrupted. Inspect the workspace before retrying." : null), State = ToolState == ToolCallState.Running ? ToolCallState.Unknown : ToolState, Elapsed = Duration };
        if (Kind == "assistant")
        {
            var assistant = new AssistantEntry { AtUtc = AtUtc, IsStreaming = false, LaneName = Lane, Duration = Duration };
            assistant.ReplaceText(Text);
            if (Reasoning is not null) assistant.AppendReasoning(Reasoning);
            return assistant;
        }
        return new NoticeEntry { AtUtc = AtUtc, Kind = NoticeKind, Text = Text, Details = Details };
    }
}
