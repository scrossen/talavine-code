namespace SwarmCore.Coding.Checkpoints;

/// <summary>Turn checkpoints (<see cref="TurnCheckpointService"/>): <c>Coding:Checkpoints</c>.</summary>
public sealed class TurnCheckpointOptions
{
    /// <summary>False turns checkpoints off; hosts then offer no undo.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Checkpoints kept per conversation; older ones are deleted after each turn. At least 1.</summary>
    public int MaxCheckpointsPerConversation { get; set; } = 50;

    /// <summary>Budget for one snapshot (all its git calls). A slower repository gets no checkpoint for that turn.</summary>
    public TimeSpan SnapshotTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>A working tree with more untracked (not ignored) files than this gets no checkpoint.</summary>
    public int MaxUntrackedFiles { get; set; } = 20_000;

    /// <summary>A working tree whose untracked (not ignored) files total more bytes than this gets no checkpoint.</summary>
    public long MaxUntrackedBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>Private ref namespace; never under refs/heads, refs/tags or refs/stash.</summary>
    public string RefNamespace { get; set; } = "refs/swarmcode/checkpoints";
}

public enum TurnFileChangeKind { Added, Modified, Deleted }

/// <summary>One path a turn changed, relative to the repository root with '/' separators.</summary>
public sealed record TurnFileChange(string Path, TurnFileChangeKind Kind);

/// <summary>The pre-turn snapshot a host holds until the turn ends (<see cref="TurnCheckpointService.CompleteAsync"/>).</summary>
public sealed record TurnCheckpoint
{
    /// <summary>The repository's top-level directory (a workspace may be a subfolder of it).</summary>
    public required string RepositoryRoot { get; init; }
    public required string ConversationId { get; init; }
    public required string CheckpointId { get; init; }
    public required string PreTree { get; init; }
    public string? PreHead { get; init; }
}

/// <summary>Outcome of <see cref="TurnCheckpointService.BeginAsync"/>: a checkpoint, or why there is none.</summary>
public sealed record TurnCheckpointStart(TurnCheckpoint? Checkpoint, string? UnavailableReason, bool NotARepository = false)
{
    public static TurnCheckpointStart Unavailable(string reason, bool notARepository = false) => new(null, reason, notARepository);
}

/// <summary>What a finished turn changed. <see cref="Changes"/> is empty when it changed nothing (no checkpoint is kept then).</summary>
public sealed record TurnCheckpointRecord(
    string CheckpointId,
    string RepositoryRoot,
    IReadOnlyList<TurnFileChange> Changes,
    bool HeadMoved,
    string? UnavailableReason = null)
{
    public bool CanUndo => UnavailableReason is null && Changes.Count > 0;
}

/// <summary>How <see cref="TurnCheckpointService.UndoAsync"/> treats paths that changed again after the turn.</summary>
public enum TurnUndoConflictMode
{
    /// <summary>Change nothing when any path conflicts; report the conflicts.</summary>
    Refuse,
    /// <summary>Restore the other paths and leave conflicting ones as they are.</summary>
    Skip,
    /// <summary>Restore conflicting paths too ("undo anyway"); their current content is saved in a backup ref first.</summary>
    Force,
}

public enum TurnUndoStatus { Undone, NothingToUndo, ConflictsNeedDecision, Unavailable, Failed }

/// <summary>What undoing a turn would do (<see cref="TurnCheckpointService.PreviewUndoAsync"/>) or did (<see cref="TurnCheckpointService.UndoAsync"/>).</summary>
public sealed record TurnUndoResult
{
    public required TurnUndoStatus Status { get; init; }
    /// <summary>One plain sentence or two for the person.</summary>
    public required string Summary { get; init; }
    /// <summary>The paths the turn changed.</summary>
    public IReadOnlyList<TurnFileChange> Changes { get; init; } = [];
    /// <summary>Paths that changed again after the turn (current content differs from both the turn's result and the pre-turn content).</summary>
    public IReadOnlyList<string> Conflicts { get; init; } = [];
    /// <summary>Paths already back at their pre-turn content.</summary>
    public IReadOnlyList<string> AlreadyRestored { get; init; } = [];
    /// <summary>Paths written back to pre-turn content.</summary>
    public IReadOnlyList<string> Restored { get; init; } = [];
    /// <summary>Files the turn created that were deleted.</summary>
    public IReadOnlyList<string> Deleted { get; init; } = [];
    /// <summary>Paths left as they are, with the reason in parentheses for anything but a conflict (conflicts in <see cref="TurnUndoConflictMode.Skip"/>, a directory where a file was, a file ignored before the turn).</summary>
    public IReadOnlyList<string> Kept { get; init; } = [];
    /// <summary>The turn moved HEAD (made or reset commits); undo restores files only and never touches commits.</summary>
    public bool HeadMoved { get; init; }
    /// <summary>Ref holding the current content of the changed paths saved just before the undo wrote anything.</summary>
    public string? BackupRef { get; init; }
}
