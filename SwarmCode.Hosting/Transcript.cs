using System.Text;

namespace SwarmCode.Hosting;

/// <summary>One item in a session's visible transcript. Mutable so streaming updates in place.</summary>
public abstract class TranscriptEntry
{
    /// <summary>Stable identity; the presentation journal replays entries by it.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTime AtUtc { get; init; } = DateTime.UtcNow;
}

public sealed class UserEntry : TranscriptEntry
{
    public required string Text { get; init; }
    public IReadOnlyList<SwarmCore.Coding.Files.CodingAttachment> Attachments { get; init; } = [];
    /// <summary>True for an orchestrator hand-off continuation rather than something the person typed.</summary>
    public bool IsContinuation { get; init; }
}

public sealed class AssistantEntry : TranscriptEntry
{
    private readonly StringBuilder _text = new();
    private readonly StringBuilder _reasoning = new();
    private readonly object _contentGate = new();

    public string Text { get { lock (_contentGate) return _text.ToString(); } }
    public string Reasoning { get { lock (_contentGate) return _reasoning.ToString(); } }
    public bool IsStreaming { get; set; } = true;
    public string? LaneName { get; set; }
    public TimeSpan? Duration { get; set; }

    public void AppendText(string delta) { lock (_contentGate) _text.Append(delta); }
    public void AppendReasoning(string delta) { lock (_contentGate) _reasoning.Append(delta); }
    public void ReplaceText(string text) { lock (_contentGate) { _text.Clear(); _text.Append(text); } }
}

public enum ToolCallState { Running, Completed, Failed, Unknown }

public sealed class ToolCallEntry : TranscriptEntry
{
    public required string ToolCallId { get; init; }
    public required string ToolName { get; init; }
    public string InputJson { get; set; } = "{}";
    public string? Result { get; set; }
    public ToolCallState State { get; set; } = ToolCallState.Running;
    public TimeSpan? Elapsed { get; set; }
    public bool IsError => State == ToolCallState.Failed;
}

public enum NoticeKind { LaneSelected, HandOff, LanesExhausted, Error, Cancelled, Compaction, Info, Secret }

public sealed class NoticeEntry : TranscriptEntry
{
    public required NoticeKind Kind { get; init; }
    public required string Text { get; init; }
    public string? Details { get; init; }
}

/// <summary>
/// The files one finished turn changed, recorded from its working-tree checkpoint
/// (<see cref="SwarmCore.Coding.Checkpoints.TurnCheckpointService"/>), so the turn can be undone.
/// </summary>
public sealed class TurnChangesEntry : TranscriptEntry
{
    public required string CheckpointId { get; init; }
    /// <summary>The repository's top-level directory the checkpoint belongs to.</summary>
    public required string RepositoryRoot { get; init; }
    public required IReadOnlyList<SwarmCore.Coding.Checkpoints.TurnFileChange> Files { get; init; }
    /// <summary>The turn also made or changed commits; undo restores files only.</summary>
    public bool HeadMoved { get; init; }
    /// <summary>Set after an undo restored the turn's files.</summary>
    public bool Undone { get; set; }
}

/// <summary>An approval the engine is waiting on. Resolved through the runner's broker.</summary>
public sealed class PermissionEntry : TranscriptEntry
{
    public required string RequestId { get; init; }
    public required string Title { get; init; }
    public required IReadOnlyList<string> Permissions { get; init; }
    /// <summary>Who asked and for what (shown on the card); empty on entries journalled before they were recorded.</summary>
    public string SkillName { get; init; } = "";
    public string FunctionName { get; init; } = "";
    /// <summary>Risk level of each requested permission, parallel to <see cref="Permissions"/>.</summary>
    public IReadOnlyList<SwarmCore.Skills.Models.PermissionLevel> Levels { get; init; } = [];
    public bool? Approved { get; set; }
    /// <summary>The scope of an approval (Once or Conversation); null while pending or when denied.</summary>
    public SwarmCore.Skills.Models.PermissionGrantScope? Scope { get; set; }
    /// <summary>True after the user withdrew this approval's conversation grant.</summary>
    public bool GrantRevoked { get; set; }
    public bool IsPending => Approved is null;
}

/// <summary>A choice on a question card.</summary>
public sealed record QuestionOption(string Label, string? Description = null);

/// <summary>A question the agent asked the person (<c>user.ask</c>). Resolved through the runner's question broker.</summary>
public sealed class QuestionEntry : TranscriptEntry
{
    public required string RequestId { get; init; }
    public required string Question { get; init; }
    /// <summary>Short background shown under the question.</summary>
    public string? Context { get; init; }
    public IReadOnlyList<QuestionOption> Options { get; init; } = [];
    public bool AllowMultiple { get; init; }
    public bool AllowFreeText { get; init; } = true;
    /// <summary>The free-text answer, if any.</summary>
    public string? Answer { get; set; }
    /// <summary>Labels of the options the person picked.</summary>
    public IReadOnlyList<string> Selected { get; set; } = [];
    /// <summary>Null while pending; false when skipped (or when nobody could answer before a restart).</summary>
    public bool? Answered { get; set; }
    public bool IsPending => Answered is null;
}
