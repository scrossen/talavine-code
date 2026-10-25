using System.Text.Json;
using System.Text.RegularExpressions;
using SwarmCore.Agent.Continual;
using SwarmCore.Coding.Ide;

namespace SwarmCore.Coding.Goals;

/// <summary>What a saved receipt did, in the person's terms.</summary>
public enum CodingGoalLedgerKind
{
    /// <summary>Sent or posted somewhere outside this computer.</summary>
    Published,
    /// <summary>Created, edited or removed local files or saved state.</summary>
    Changed,
    /// <summary>Ran a command, delegated work or operated an app; the outcome is in the result.</summary>
    Ran,
    /// <summary>Answered a person or recorded a report through the goal's own channels.</summary>
    Reported,
    /// <summary>Read, searched or checked something without changing it.</summary>
    Observed,
    /// <summary>The tool reported an error.</summary>
    Failed,
    /// <summary>Started in an earlier attempt and never completed; its physical outcome is unknown.</summary>
    Uncertain,
}

public sealed record CodingGoalLedgerLink(string Url, string Label);

/// <summary>One receipt as a ledger line. <see cref="Files"/> are as the tool reported them until a host resolves them.</summary>
public sealed record CodingGoalLedgerEntry(int Attempt, long Sequence, string ToolCallId, string ToolName, DateTimeOffset AtUtc,
    CodingGoalLedgerKind Kind, ContinualSideEffectClass SideEffect, string Title, string? Summary,
    IReadOnlyList<CodingGoalLedgerLink> Links, IReadOnlyList<string> Files, bool HostVerified)
{
    public string Key => Attempt + "/" + ToolCallId;
}

public sealed record CodingGoalLedgerPage(IReadOnlyList<CodingGoalLedgerEntry> Entries, int Total, int Offset, bool HasMore,
    long GoalVersion, long EventSequence, string? RepositoryRoot);

/// <summary>Host-owned read of a goal's ledger. The host supplies the saved goal, resolves file paths and owns caching.</summary>
public interface ICodingGoalLedger
{
    /// <summary>Null when the goal does not exist in this profile.</summary>
    Task<CodingGoalLedgerPage?> ReadAsync(Guid goalId, int offset = 0, int take = 50, bool includeObservations = false,
        CancellationToken ct = default);
}

/// <summary>
/// Derives "what has been done" from a goal's durable tool receipts: one entry per effect, newest first,
/// with the links and files found structurally in the receipt. It reads only saved receipts and never
/// infers success beyond what the receipt and any host proof state. There are no per-tool branches: titles
/// come from common structural fields (<c>tool</c>/<c>server</c>, <c>command</c>, <c>changes</c>, <c>query</c>).
/// </summary>
public static partial class CodingGoalLedger
{
    public const int SummaryLength = 240;
    public const int MaxLinks = 6;
    public const int MaxFiles = 8;
    public const int MaxPage = 500;
    private const int MaxJsonChars = 2 * 1024 * 1024;
    private static readonly string[] ExcludedHosts = ["localhost", "127.0.0.1", "0.0.0.0", "[::1]"];
    private static readonly string[] SummaryKeys = ["output", "error", "message", "text", "summary", "status"];
    private static readonly string[] SubjectKeys = ["url", "path", "file", "fileName", "file_name", "query", "command", "title", "name"];
    private static readonly string[] ArgumentKeys = ["argumentList", "arguments", "args", "argv"];
    private static readonly string[] NestedKeys = ["arguments", "input", "params", "args"];

    public static IReadOnlyList<CodingGoalLedgerEntry> Build(ContinualObjectiveSnapshot goal, bool includeObservations = false)
    {
        ArgumentNullException.ThrowIfNull(goal);
        return Order(goal.Receipts.Select(receipt => Describe(goal, receipt, includeObservations)).Where(entry => entry is not null).Select(entry => entry!));
    }

    public static IReadOnlyList<CodingGoalLedgerEntry> Order(IEnumerable<CodingGoalLedgerEntry> entries)
        => entries.OrderByDescending(entry => entry.AtUtc).ThenByDescending(entry => entry.Sequence).ToArray();

    public static CodingGoalLedgerPage Page(ContinualObjectiveSnapshot goal, IReadOnlyList<CodingGoalLedgerEntry> entries,
        int offset, int take, string? repositoryRoot = null)
    {
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(entries);
        offset = Math.Clamp(offset, 0, entries.Count);
        take = Math.Clamp(take, 1, MaxPage);
        var slice = entries.Skip(offset).Take(take).ToArray();
        return new(slice, entries.Count, offset, offset + slice.Length < entries.Count, goal.Version, goal.LastEventSequence, repositoryRoot);
    }

    /// <summary>Null when the receipt is not a ledger line: a read-only call (unless asked for) or the current attempt's still-running call.</summary>
    public static CodingGoalLedgerEntry? Describe(ContinualObjectiveSnapshot goal, ContinualToolReceipt receipt, bool includeObservations)
    {
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(receipt);
        CodingGoalLedgerKind kind;
        if (receipt.State == ContinualToolReceiptState.Started)
        {
            // The current attempt's open call is still running; only an older open call is an unknown outcome.
            if (receipt.Attempt >= goal.AttemptCount && goal.Status == ContinualObjectiveStatus.InProgress) return null;
            kind = CodingGoalLedgerKind.Uncertain;
        }
        else if (receipt.SideEffect == ContinualSideEffectClass.ReadOnly)
        {
            if (!includeObservations) return null;
            kind = receipt.IsError ? CodingGoalLedgerKind.Failed : CodingGoalLedgerKind.Observed;
        }
        else if (receipt.IsError) kind = CodingGoalLedgerKind.Failed;
        else kind = receipt.SideEffect switch
        {
            ContinualSideEffectClass.WritesLocal or ContinualSideEffectClass.Destructive => CodingGoalLedgerKind.Changed,
            ContinualSideEffectClass.ExternalSend => CodingGoalLedgerKind.Published,
            ContinualSideEffectClass.ExecutionMetadata => CodingGoalLedgerKind.Reported,
            _ => CodingGoalLedgerKind.Ran,
        };
        var input = Parse(receipt.InputJson);
        var result = Parse(receipt.Result);
        // Files are listed only for calls that write: a command's executable or a read's target is not a changed file.
        var changed = receipt.SideEffect is ContinualSideEffectClass.WritesLocal or ContinualSideEffectClass.Destructive;
        var files = changed
            ? ToolFileReferences.Extract(receipt.InputJson, receipt.Result, MaxFiles).Select(target => target.Path).Distinct(StringComparer.Ordinal).Take(MaxFiles).ToArray()
            : [];
        return new(receipt.Attempt, receipt.Sequence, receipt.ToolCallId, receipt.ToolName, receipt.CompletedAtUtc ?? receipt.StartedAtUtc,
            kind, receipt.SideEffect, Title(receipt, input, result), Summary(receipt.Result, receipt.IsError), Links(receipt.Result, receipt.InputJson),
            files, receipt.HostResolution is not null && ContinualReceiptResolutionPolicy.IsResolved(receipt, goal.Receipts));
    }

    private static string Title(ContinualToolReceipt receipt, JsonElement? input, JsonElement? result)
    {
        if (input is { ValueKind: JsonValueKind.Object } root)
        {
            if (String(root, "command") is { Length: > 0 } command) return Command(command);
            if (root.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Array)
            {
                var count = changes.GetArrayLength();
                if (count == 1 && changes[0].ValueKind == JsonValueKind.Object && String(changes[0], "path") is { Length: > 0 } single)
                    return "Edited " + Shorten(FileName(single), 80);
                return count == 1 ? "Edited 1 file" : $"Edited {count} files";
            }
            var query = String(root, "query");
            if (string.IsNullOrWhiteSpace(query) && result is { ValueKind: JsonValueKind.Object } answer) query = String(answer, "query");
            if (!string.IsNullOrWhiteSpace(query)) return "Searched: " + Shorten(query.Trim(), 100);
            // The subject (what the call was about) matters more than the verb; a bare identifier is shown as a name, not fake English.
            if (String(root, "tool") is { Length: > 0 } tool)
                return Subject(root) is { } subject ? Humanize(tool) + ": " + subject : "Used " + tool;
            if (String(root, "nativeType") is { Length: > 0 } native)
                return Subject(root) is { } subject ? Humanize(native) + ": " + subject : Humanize(native);
            if (String(root, "operation") is { Length: > 0 } operation) return Humanize(operation);
        }
        var name = receipt.ToolName;
        var dot = name.LastIndexOf('.');
        return Humanize(dot >= 0 && dot < name.Length - 1 ? name[(dot + 1)..] : name);
    }

    private static string? Subject(JsonElement root)
    {
        foreach (var candidate in Nested(root))
            foreach (var key in SubjectKeys)
                if (String(candidate, key) is { } value && !string.IsNullOrWhiteSpace(value))
                    return key is "fileName" or "file_name" ? Compact(value.Trim() + Arguments(candidate), "command") : Compact(value.Trim(), key);
        return null;
    }

    /// <summary>A process start's string arguments, so "python3 -c …" reads like the command it was.</summary>
    private static string Arguments(JsonElement candidate)
    {
        foreach (var key in ArgumentKeys)
            if (candidate.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array)
            {
                var parts = list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray();
                return parts.Length == 0 ? "" : " " + string.Join(' ', parts);
            }
        return "";
    }

    private static IEnumerable<JsonElement> Nested(JsonElement root)
    {
        yield return root;
        foreach (var key in NestedKeys)
            if (root.TryGetProperty(key, out var nested) && nested.ValueKind == JsonValueKind.Object) yield return nested;
    }

    private static string Compact(string value, string key)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return Shorten(Label(uri), 80);
        if (key is "path" or "file") return Shorten(FileName(value), 80);
        return Shorten(value.Split('\n', 2)[0].Trim(), 80);
    }

    private static string FileName(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var cut = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\'));
        return cut >= 0 && cut < trimmed.Length - 1 ? trimmed[(cut + 1)..] : trimmed;
    }

    private static string Humanize(string value)
    {
        var text = CamelBoundary().Replace(value.Replace('_', ' ').Replace('-', ' ').Trim(), " ");
        text = Whitespace().Replace(text, " ");
        return text.Length == 0 ? value : char.ToUpperInvariant(text[0]) + text[1..];
    }

    private static string Command(string command)
    {
        var text = ShellWrapper().Replace(command.Trim(), "").Trim();
        if (text.Length >= 2 && (text[0] is '\'' or '"') && text[^1] == text[0]) text = text[1..^1].Trim();
        var line = text.Split('\n', 2)[0].Trim();
        return "Ran: " + Shorten(line.Length == 0 ? command.Trim() : line, 100);
    }

    private static string? Summary(string? result, bool isError)
    {
        // A compacted result keeps its first kilobyte after the marker line; that is what a person wants to read.
        if (ContinualReceiptRetentionPolicy.TryReadExcerpt(result, out var excerpt)) result = excerpt;
        var text = Unwrap(result, 0);
        if (string.IsNullOrWhiteSpace(text)) return null;
        var collapsed = Whitespace().Replace(text.Trim(), " ");
        // A raw JSON dump or a bare status token adds nothing a person needs; the title and chips already say it.
        if (collapsed[0] is '{' or '[') return null;
        if (!isError && collapsed.Length <= 16 && !collapsed.Contains(' ')) return null;
        return Shorten(collapsed, SummaryLength);
    }

    /// <summary>Peels transport envelopes (MCP <c>content[].text</c>, command <c>output</c>, nested JSON strings) down to the words.</summary>
    private static string? Unwrap(string? text, int depth)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (depth > 3 || Parse(text) is not { } element) return text;
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return Unwrap(element.GetString(), depth + 1);
            case JsonValueKind.Object:
                if (element.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                    foreach (var part in content.EnumerateArray())
                        if (String(part, "text") is { } inner && !string.IsNullOrWhiteSpace(inner)) return Unwrap(inner, depth + 1);
                foreach (var key in SummaryKeys)
                    if (String(element, key) is { } value && !string.IsNullOrWhiteSpace(value)) return value;
                return Shorten(element.GetRawText(), SummaryLength * 2);
            case JsonValueKind.Array:
                return Shorten(element.GetRawText(), SummaryLength * 2);
            default:
                return element.GetRawText();
        }
    }

    private static IReadOnlyList<CodingGoalLedgerLink> Links(string? result, string? input)
    {
        var links = new List<CodingGoalLedgerLink>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in new[] { result, input })
        {
            if (string.IsNullOrEmpty(source)) continue;
            foreach (Match match in UrlPattern().Matches(source))
            {
                var candidate = match.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}', '\'', '"', '*');
                if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) continue;
                if (ExcludedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)
                    || uri.AbsolutePath.Contains("/schemas/", StringComparison.OrdinalIgnoreCase)) continue;
                if (!seen.Add(uri.AbsoluteUri)) continue;
                links.Add(new(uri.AbsoluteUri, Label(uri)));
                if (links.Count >= MaxLinks) return links;
            }
        }
        return links;
    }

    private static string Label(Uri uri)
    {
        var path = uri.AbsolutePath.TrimEnd('/');
        var text = uri.Host + (path.Length > 1 ? Uri.UnescapeDataString(path) : "") + (uri.Fragment.Length > 1 ? uri.Fragment : "");
        return Shorten(text, 100);
    }

    private static JsonElement? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxJsonChars) return null;
        var trimmed = json.AsSpan().TrimStart();
        if (trimmed.Length == 0 || trimmed[0] is not ('{' or '[' or '"')) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException) { return null; }
    }

    private static string? String(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";

    [GeneratedRegex(@"https?://[^\s""'<>\\\)\]\}]+", RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();
    [GeneratedRegex(@"^(?:/bin/|/usr/bin/|/usr/local/bin/)?(?:zsh|bash|sh)\s+-l?c\s+", RegexOptions.CultureInvariant)]
    private static partial Regex ShellWrapper();
    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex CamelBoundary();
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
