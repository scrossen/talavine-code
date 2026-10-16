using System.Text;
using System.Text.Json.Nodes;

namespace SwarmCore.Coding.Ide;

/// <summary>A selection an editor extension sends to the coding app. Lines are 1-based and inclusive.</summary>
public sealed record IdeSelection(
    string? WorkspaceFolder,
    string File,
    int StartLine,
    int EndLine,
    string SelectionText,
    string? LanguageId)
{
    public const int MaxSelectionChars = 200_000;
    public const int MaxPathChars = 4096;

    /// <summary>Validates the JSON an extension posted; <paramref name="error"/> says what was wrong.</summary>
    public static bool TryParse(JsonObject body, out IdeSelection selection, out string error)
    {
        selection = null!;
        error = "";
        var file = Text(body, "file");
        if (string.IsNullOrWhiteSpace(file) || file.Length > MaxPathChars || !Path.IsPathFullyQualified(file) || file.IndexOfAny(['\0', '\n', '\r']) >= 0)
        {
            error = "file must be an absolute path.";
            return false;
        }
        var folder = Text(body, "workspaceFolder");
        if (folder is not null && (folder.Length > MaxPathChars || !Path.IsPathFullyQualified(folder))) folder = null;
        var text = Text(body, "selectionText") ?? "";
        if (text.Length > MaxSelectionChars)
        {
            error = $"The selection is larger than {MaxSelectionChars:N0} characters.";
            return false;
        }
        var start = Int(body, "startLine") ?? 1;
        var end = Int(body, "endLine") ?? start;
        if (start < 1 || end < start || end > 10_000_000)
        {
            error = "startLine and endLine must be 1-based with endLine >= startLine.";
            return false;
        }
        var language = Text(body, "languageId");
        if (language is not null && (language.Length > 64 || !language.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '+' or '.' or '#')))
            language = null;
        selection = new IdeSelection(folder, file, start, end, text, language);
        return true;
    }

    private static string? Text(JsonObject body, string key)
        => body[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int? Int(JsonObject body, string key)
        => body[key] is JsonValue value && value.TryGetValue<int>(out var number) ? number
            : body[key] is JsonValue d && d.TryGetValue<double>(out var real) && real is >= int.MinValue and <= int.MaxValue && real == Math.Floor(real) ? (int)real
            : null;
}

/// <summary>Formats a selection as the quoted reference block a composer receives.</summary>
public static class IdeReferenceFormatter
{
    public static string Format(IdeSelection selection, string? repositoryRoot)
    {
        var path = EditorPathPolicy.Display(selection.File, repositoryRoot);
        var lines = selection.StartLine == selection.EndLine ? $"L{selection.StartLine}" : $"L{selection.StartLine}-L{selection.EndLine}";
        var text = selection.SelectionText.Replace("\r\n", "\n").TrimEnd('\n');
        if (text.Length == 0) return $"`{path}:{lines}`";
        // A fence longer than any backtick run inside the selection, so the quoted code cannot close it early.
        var longest = 0;
        var run = 0;
        foreach (var ch in text)
        {
            run = ch == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        var fence = new string('`', Math.Max(3, longest + 1));
        return new StringBuilder()
            .Append(fence).Append(selection.LanguageId ?? "").Append(' ').Append(path).Append(':').Append(lines).Append('\n')
            .Append(text).Append('\n')
            .Append(fence)
            .ToString();
    }

    /// <summary>Appends the block to an existing draft, separated by a blank line.</summary>
    public static string AppendToDraft(string? draft, string block)
        => string.IsNullOrWhiteSpace(draft) ? block + "\n" : draft.TrimEnd() + "\n\n" + block + "\n";
}

/// <summary>A session a selection could be routed to.</summary>
public sealed record IdeSessionCandidate(string Id, string RepositoryRoot, DateTime LastActivityUtc, bool IsTopLevel = true);

/// <summary>
/// Picks the session that receives a selection: the session on screen when its repository contains the file;
/// otherwise the most recently active top-level session whose repository contains the file (the deepest
/// repository wins for nested checkouts); otherwise the session on screen; otherwise none.
/// </summary>
public static class IdeSessionRouter
{
    public static IdeSessionCandidate? Choose(IReadOnlyList<IdeSessionCandidate> sessions, string file, string? activeSessionId)
    {
        var active = activeSessionId is null ? null : sessions.FirstOrDefault(s => s.Id == activeSessionId);
        if (active is not null && EditorPathPolicy.IsUnder(file, active.RepositoryRoot)) return active;
        var containing = sessions
            .Where(s => s.IsTopLevel && EditorPathPolicy.IsUnder(file, s.RepositoryRoot))
            .OrderByDescending(s => s.RepositoryRoot.TrimEnd('\\', '/').Length)
            .ThenByDescending(s => s.LastActivityUtc)
            .FirstOrDefault();
        return containing ?? active;
    }
}
