using System.Text;
using System.Text.RegularExpressions;

namespace SwarmCore.Coding.History;

/// <summary>
/// One saved coding session as the history tools see it: the title, the project (repository root) it belongs to,
/// its activity window, and the visible user/assistant turns. Hosts build these from their own stores through
/// <see cref="IConversationHistorySource"/>; tool payloads, reasoning and provider transcripts are never part of it.
/// </summary>
public sealed record HistorySessionDocument(
    string Id,
    string Title,
    string Project,
    DateTime CreatedUtc,
    DateTime LastActivityUtc,
    IReadOnlyList<HistoryTurn> Turns)
{
    /// <summary>The lane the session last ran on, when the host records one.</summary>
    public string? Lane { get; init; }

    /// <summary>The project's display name: the last segment of the repository path.</summary>
    public string ProjectName => HistoryProjects.Name(Project);
}

/// <summary>One user request and the reply it got. <see cref="IsContinuation"/> marks host-authored follow-up prompts.</summary>
public sealed record HistoryTurn(
    Guid? Id,
    string UserText,
    string? AssistantText,
    DateTime CreatedUtc,
    DateTime? CompletedUtc,
    bool IsContinuation = false);

/// <summary>Which sessions a host should load. Null bounds mean unbounded; <see cref="Project"/> is a path or a project name.</summary>
public sealed record HistoryScope(DateTime? SinceUtc = null, DateTime? UntilUtc = null, string? Project = null, bool IncludeAgentSessions = false)
{
    public static readonly HistoryScope Everything = new();

    /// <summary>True when a session active between <paramref name="createdUtc"/> and <paramref name="lastActivityUtc"/> in <paramref name="project"/> falls inside this scope.</summary>
    public bool Covers(string project, DateTime createdUtc, DateTime lastActivityUtc)
    {
        if (SinceUtc is { } since && lastActivityUtc < since) return false;
        if (UntilUtc is { } until && createdUtc >= until) return false;
        return HistoryProjects.Matches(project, Project);
    }
}

/// <summary>Where saved sessions come from. Hosts own the files or database; the history tools own search and summaries.</summary>
public interface IConversationHistorySource
{
    /// <summary>Sessions touching <paramref name="scope"/>, newest activity first, each with its visible turns.</summary>
    Task<IReadOnlyList<HistorySessionDocument>> LoadAsync(HistoryScope scope, CancellationToken ct = default);
}

public static class HistoryProjects
{
    /// <summary>The last path segment of a repository root (the folder name), or the root itself when it has none.</summary>
    public static string Name(string project)
    {
        if (string.IsNullOrWhiteSpace(project)) return project ?? "";
        var trimmed = project.TrimEnd('\\', '/');
        var cut = trimmed.LastIndexOfAny(['\\', '/']);
        var name = cut < 0 ? trimmed : trimmed[(cut + 1)..];
        return name.Length == 0 ? project : name;
    }

    /// <summary>A filter matches a project by full path or by folder name, ignoring case and trailing separators. Empty: everything.</summary>
    public static bool Matches(string project, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        var wanted = filter.Trim().TrimEnd('\\', '/');
        var root = (project ?? "").TrimEnd('\\', '/');
        return string.Equals(root, wanted, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Name(root), wanted, StringComparison.OrdinalIgnoreCase)
            || string.Equals(root.Replace('\\', '/'), wanted.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }
}

public enum HistoryMatchField { None, Title, Prompt, Response }

/// <summary>What the person typed to search with. Every term (a word or a "quoted phrase") must occur somewhere in a session.</summary>
public sealed record HistorySearchOptions(string Query)
{
    /// <summary>Also search the assistant's replies. Off searches titles and the person's requests only.</summary>
    public bool IncludeResponses { get; init; }

    /// <summary>Only sessions with activity at or after this time.</summary>
    public DateTime? SinceUtc { get; init; }

    /// <summary>Only sessions created before this time.</summary>
    public DateTime? UntilUtc { get; init; }

    /// <summary>Repository root or project name; null for every project.</summary>
    public string? Project { get; init; }
}

/// <summary>One matching session with the first place a term was found and a bounded excerpt around it.</summary>
public sealed record HistorySearchResult(
    HistorySessionDocument Session,
    HistoryMatchField Field,
    string Preview,
    Guid? TurnId,
    DateTime MatchedAtUtc,
    int MatchingTurns);

/// <summary>
/// Plain-text search over saved sessions. Deterministic and allocation-light: it never calls a model, so it works with
/// no provider configured. Matching is case-insensitive and requires every term; a session matches when its title or
/// one of its turns contains each term (a term may be satisfied by a different turn than another term).
/// Host-authored continuation prompts are not searched as requests, since the person did not write them.
/// </summary>
public static partial class ConversationSearch
{
    private const int PreviewBefore = 60;
    private const int PreviewAfter = 110;
    private const int PreviewMax = 180;

    public static IReadOnlyList<HistorySearchResult> Search(IEnumerable<HistorySessionDocument> sessions, HistorySearchOptions options, int take = 50)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(options);
        var terms = Terms(options.Query);
        var results = new List<HistorySearchResult>();
        foreach (var session in sessions.OrderByDescending(s => s.LastActivityUtc))
        {
            if (!HistoryProjects.Matches(session.Project, options.Project)) continue;
            if (options.SinceUtc is { } since && session.LastActivityUtc < since) continue;
            if (options.UntilUtc is { } until && session.CreatedUtc >= until) continue;
            var match = Match(session, terms, options.IncludeResponses);
            if (match is not null) results.Add(match);
            if (take > 0 && results.Count >= take) break;
        }
        return results;
    }

    /// <summary>Splits a query into terms: quoted phrases stay whole, everything else splits on whitespace.</summary>
    public static IReadOnlyList<string> Terms(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var terms = new List<string>();
        foreach (Match m in TermPattern().Matches(query))
        {
            var term = m.Groups["phrase"].Success ? m.Groups["phrase"].Value : m.Value;
            term = term.Trim();
            if (term.Length > 0 && !terms.Contains(term, StringComparer.OrdinalIgnoreCase)) terms.Add(term);
        }
        return terms;
    }

    private static HistorySearchResult? Match(HistorySessionDocument session, IReadOnlyList<string> terms, bool includeResponses)
    {
        if (terms.Count == 0)
        {
            var first = session.Turns.FirstOrDefault(t => !t.IsContinuation && !string.IsNullOrWhiteSpace(t.UserText));
            return new HistorySearchResult(session, HistoryMatchField.None, Clip(Collapse(first?.UserText ?? ""), PreviewMax), first?.Id,
                first?.CreatedUtc ?? session.LastActivityUtc, 0);
        }

        var satisfied = new bool[terms.Count];
        HistorySearchResult? best = null;
        var matchingTurns = 0;

        for (var i = 0; i < terms.Count; i++)
        {
            if (Contains(session.Title, terms[i]))
            {
                satisfied[i] = true;
                best ??= new HistorySearchResult(session, HistoryMatchField.Title, Snippet(session.Title, terms[i]), null, session.CreatedUtc, 0);
            }
        }

        foreach (var turn in session.Turns)
        {
            var turnMatched = false;
            if (!turn.IsContinuation && !string.IsNullOrEmpty(turn.UserText))
            {
                for (var i = 0; i < terms.Count; i++)
                {
                    if (!Contains(turn.UserText, terms[i])) continue;
                    satisfied[i] = true;
                    turnMatched = true;
                    best ??= new HistorySearchResult(session, HistoryMatchField.Prompt, Snippet(turn.UserText, terms[i]), turn.Id, turn.CreatedUtc, 0);
                }
            }
            if (includeResponses && !string.IsNullOrEmpty(turn.AssistantText))
            {
                for (var i = 0; i < terms.Count; i++)
                {
                    if (!Contains(turn.AssistantText, terms[i])) continue;
                    satisfied[i] = true;
                    turnMatched = true;
                    best ??= new HistorySearchResult(session, HistoryMatchField.Response, Snippet(turn.AssistantText, terms[i]), turn.Id, turn.CompletedUtc ?? turn.CreatedUtc, 0);
                }
            }
            if (turnMatched) matchingTurns++;
        }

        if (best is null || Array.Exists(satisfied, s => !s)) return null;
        return best with { MatchingTurns = matchingTurns };
    }

    private static bool Contains(string? text, string term)
        => !string.IsNullOrEmpty(text) && text.Contains(term, StringComparison.OrdinalIgnoreCase);

    /// <summary>An excerpt around the first occurrence of <paramref name="term"/>, whitespace collapsed, with ellipses where cut.</summary>
    public static string Snippet(string text, string term)
    {
        var at = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return Clip(Collapse(text), PreviewMax);
        var start = Math.Max(0, at - PreviewBefore);
        var end = Math.Min(text.Length, at + term.Length + PreviewAfter);
        var sb = new StringBuilder();
        if (start > 0) sb.Append('…');
        sb.Append(Collapse(text[start..end]));
        if (end < text.Length) sb.Append('…');
        return sb.ToString();
    }

    internal static string Collapse(string text) => Whitespace().Replace(text.Trim(), " ");

    internal static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";

    [GeneratedRegex("\"(?<phrase>[^\"]+)\"|\\S+", RegexOptions.CultureInvariant)]
    private static partial Regex TermPattern();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}

/// <summary>Buckets sessions by how recently they were active, in the person's local calendar.</summary>
public static class HistoryRecency
{
    public const string Today = "Today";
    public const string Yesterday = "Yesterday";
    public const string Week = "Previous 7 days";
    public const string Month = "Previous 30 days";
    public const string Older = "Older";

    /// <summary>The bucket order used when listing groups.</summary>
    public static readonly IReadOnlyList<string> Order = [Today, Yesterday, Week, Month, Older];

    /// <summary>The bucket for an activity time, given the local date that counts as today.</summary>
    public static string Bucket(DateTime activityLocal, DateTime todayLocal)
    {
        var days = (todayLocal.Date - activityLocal.Date).Days;
        return days <= 0 ? Today : days == 1 ? Yesterday : days <= 7 ? Week : days <= 30 ? Month : Older;
    }
}
