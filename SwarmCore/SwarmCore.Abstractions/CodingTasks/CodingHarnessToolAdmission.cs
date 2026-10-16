namespace SwarmCore.Abstractions.CodingTasks;

/// <summary>
/// Which coding-harness tools (SwarmCore.Coding) a verified coding mission may use inside its isolated
/// workspace. They are admitted only as finer-grained forms of primitives the assignment already
/// allows, so they add no authority: path listing follows a list, read or shell grant, content search and git inspection follow a read or shell grant, and
/// the patch-style edit follows a write grant. <c>git.commit</c> is never admitted (a verified run
/// leaves a reviewable diff and does not commit), and <c>credentials.list</c> / <c>harness.decline</c>
/// belong to coding conversations, not missions. Every admitted tool enforces the mission resource
/// boundary, and the host profile still filters the catalog.
/// </summary>
public static class CodingHarnessToolAdmission
{
    public const string EditFile = "code.edit_file";
    public const string Grep = "code.grep";
    public const string Glob = "code.glob";
    public const string GitStatus = "git.status";
    public const string GitDiff = "git.diff";
    public const string GitLog = "git.log";

    /// <summary>Read-only workspace inspection tools.</summary>
    public static IReadOnlyList<string> InspectionToolIds { get; } = [Grep, Glob, GitStatus, GitDiff, GitLog];

    /// <summary>Every harness tool a verified mission can be admitted to.</summary>
    public static IReadOnlyList<string> WorkspaceToolIds { get; } = [EditFile, .. InspectionToolIds];

    // Content search and git inspection read file contents: only a read or shell grant covers them.
    private static readonly string[] ContentReadGrants = ["fs.read_file", "shell.exec"];
    // Listing paths is covered by a list grant too.
    private static readonly string[] ListGrants = ["fs.read_file", "fs.list_files", "shell.exec"];
    private const string WriteGrant = "fs.write_file";

    /// <summary>
    /// The assignment's allowed tools plus the harness tools those grants already cover. The input is
    /// returned unchanged (same instance) when nothing is added, so persisted request identity is
    /// never rewritten; callers apply this when offering tools, not when hashing the request.
    /// </summary>
    public static IReadOnlyList<string> Expand(IReadOnlyList<string> allowedToolIds)
    {
        ArgumentNullException.ThrowIfNull(allowedToolIds);
        var allowed = new HashSet<string>(allowedToolIds, StringComparer.Ordinal);
        var added = new List<string>();
        if (ListGrants.Any(allowed.Contains) && !allowed.Contains(Glob)) added.Add(Glob);
        if (ContentReadGrants.Any(allowed.Contains))
            added.AddRange(new[] { Grep, GitStatus, GitDiff, GitLog }.Where(id => !allowed.Contains(id)));
        if (allowed.Contains(WriteGrant) && !allowed.Contains(EditFile))
            added.Add(EditFile);
        return added.Count == 0 ? allowedToolIds : [.. allowedToolIds, .. added];
    }
}
