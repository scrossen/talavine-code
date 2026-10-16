namespace SwarmCore.Chat.Components;

/// <summary>One changed file in a working tree, shaped like a <c>git status --porcelain</c> entry.</summary>
/// <param name="Path">Repository-relative path.</param>
/// <param name="Status">Two-character porcelain code, for example " M", "A ", "??".</param>
public sealed record ChangedFile(string Path, string Status)
{
    /// <summary>add | del | mod: the colour class of the status code.</summary>
    public string Kind
    {
        get
        {
            var s = (Status ?? "").Trim();
            if (s.Contains('?') || s.Contains('A')) return "add";
            if (s.Contains('D')) return "del";
            return "mod";
        }
    }
}

/// <summary>A working-tree status snapshot for <c>ChangesPanel</c> (branch, tracking, changed files).</summary>
public sealed record ChangesSnapshot(
    string? Branch,
    string? Upstream,
    int Ahead,
    int Behind,
    IReadOnlyList<ChangedFile> Files)
{
    /// <summary>Set when the status could not be read; the panel shows it instead of the list.</summary>
    public string? Error { get; init; }

    public static ChangesSnapshot Failed(string error) => new(null, null, 0, 0, []) { Error = error };
}

/// <summary>One file's unified diff; <see cref="Truncated"/> when the host cut it at a size limit.</summary>
public sealed record ChangesDiff(string? Text, bool Truncated = false);

/// <summary>
/// Host seam for <c>ChangesPanel</c>: where the status and diffs come from. The panel calls it off the
/// UI dispatcher; implementations read through the host's own sandboxed git tooling (never a shell from
/// the UI) and decide which repository applies.
/// </summary>
public interface IChangesSource
{
    Task<ChangesSnapshot> GetStatusAsync(CancellationToken ct);

    /// <summary>Unified diff of one file; empty or null text for an untracked or binary file.</summary>
    Task<ChangesDiff?> GetDiffAsync(ChangedFile file, CancellationToken ct);
}

/// <summary>A lane/route notice ready for <c>ChatNotice</c> (<see cref="Kind"/> is one of its kinds).</summary>
public sealed record ChatLaneNotice(string Kind, string Text, string? Details = null);

/// <summary>
/// The shared wording and <c>ChatNotice</c> kinds for coding-lane events, so every host shows lane
/// selection, hand-off, skips and exhaustion the same way.
/// </summary>
public static class ChatLaneNotices
{
    public static ChatLaneNotice Selected(string lane, string? route, int attempt = 1)
    {
        var where = string.IsNullOrWhiteSpace(route) || string.Equals(route, lane, StringComparison.Ordinal) ? lane : $"{lane} ({route})";
        return new("lane", attempt <= 1 ? where : $"Continuing on {where}");
    }

    public static ChatLaneNotice HandOff(string from, string to, string? reason)
        => new("handoff", $"Switching from {from} to {to}.", string.IsNullOrWhiteSpace(reason) ? null : reason);

    public static ChatLaneNotice Skipped(string lane, string? reason)
        => new("info", string.IsNullOrWhiteSpace(reason) ? $"Skipping {lane}." : $"Skipping {lane}: {reason}");

    public static ChatLaneNotice Exhausted(string reason) => new("exhausted", reason);
}
