using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace SwarmCore.Coding.SelfImprovement;

/// <summary>A failure the classifier is willing to call a product defect, ready to report.</summary>
public sealed record DefectCandidate(string Title, string Details, string Fingerprint);

/// <summary>
/// Decides whether a failure is the product's own (a bug in SwarmCode or SwarmCore code) rather than the environment's
/// (network, credentials, a cancelled turn, a missing file, a provider outage). Only product defects enter the backlog:
/// the loop exists to fix code, and environmental failures would only waste cycles.
/// </summary>
public static class DefectClassifier
{
    /// <summary>Exception types that point at code, not circumstances.</summary>
    public static readonly IReadOnlySet<string> ProductExceptionTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(NullReferenceException), nameof(ArgumentException), nameof(ArgumentNullException), nameof(ArgumentOutOfRangeException),
        nameof(InvalidOperationException), nameof(IndexOutOfRangeException), "KeyNotFoundException", nameof(InvalidCastException),
        nameof(NotImplementedException), nameof(NotSupportedException), nameof(FormatException), nameof(OverflowException),
        nameof(DivideByZeroException), nameof(ObjectDisposedException), nameof(ArithmeticException), "JsonException",
        nameof(InvalidProgramException), nameof(TypeInitializationException), nameof(MissingMethodException), nameof(MemberAccessException),
        "InvalidDataException", nameof(RankException), nameof(ArrayTypeMismatchException), "AmbiguousMatchException",
    };

    /// <summary>Exception types (or name fragments) that describe circumstances the code cannot fix.</summary>
    private static readonly string[] EnvironmentalFragments =
    [
        "OperationCanceled", "TaskCanceled", "HttpRequest", "Socket", "WebException", "Timeout", "IOException", "FileNotFound",
        "DirectoryNotFound", "PathTooLong", "DriveNotFound", "UnauthorizedAccess", "Authentication", "Security", "Win32", "DnsResolution",
        "RateLimit", "Quota", "Unauthorized", "Forbidden", "ProviderUnavailable", "ContentPolicy", "Refusal", "Credential", "Network",
        "OutOfMemory", "InsufficientExecutionStack", "StackOverflow", "ThreadAbort", "ThreadInterrupted", "EndOfStream", "Lock",
    ];

    private static readonly Regex TypedPrefix = new("(?<![\\w.])(?<type>(?:[A-Za-z_][\\w]*\\.)*[A-Za-z_]\\w*Exception)\\s*[:\\-]\\s*(?<message>[^\\r\\n]*)", RegexOptions.Compiled);
    private static readonly Regex ProductFrame = new("\\b(SwarmCore|SwarmCode|Talavine|Vibes)\\.[\\w.]+", RegexOptions.Compiled);

    /// <summary>True when the exception's type (after unwrapping) points at product code rather than the environment.</summary>
    public static bool IsProductDefect(Exception exception)
    {
        var inner = Unwrap(exception);
        var type = inner.GetType().Name;
        if (IsEnvironmental(type)) return false;
        if (ProductExceptionTypes.Contains(type)) return true;
        // An unknown (custom) type counts only when the product's own code is on the stack.
        return inner.StackTrace is { } stack && ProductFrame.IsMatch(stack);
    }

    /// <summary>Builds a report candidate from a thrown exception, or null when it is not a product defect.</summary>
    public static DefectCandidate? FromException(Exception exception, string? context = null)
    {
        if (!IsProductDefect(exception)) return null;
        var inner = Unwrap(exception);
        var type = inner.GetType().Name;
        var message = FirstLine(inner.Message);
        var frames = ProductFrames(inner.StackTrace);
        var title = Bound($"{type}: {message}", 160);
        var details = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(context)) details.Append("Context: ").AppendLine(context.Trim());
        details.Append("Exception: ").Append(inner.GetType().FullName ?? type).Append(": ").AppendLine(inner.Message.Trim());
        if (frames.Count > 0)
        {
            details.AppendLine("Product frames:");
            foreach (var frame in frames) details.Append("  ").AppendLine(frame);
        }
        else if (!string.IsNullOrWhiteSpace(inner.StackTrace))
        {
            details.AppendLine("Stack (top):");
            foreach (var line in inner.StackTrace.Split('\n').Take(8)) details.Append("  ").AppendLine(line.Trim());
        }
        var fingerprint = SelfImprovementFingerprint.Compute(SelfImprovementKind.Defect, type, message, string.Join("|", frames.Take(4).Select(StripLineNumbers)));
        return new DefectCandidate(title, details.ToString().TrimEnd(), fingerprint);
    }

    /// <summary>
    /// Builds a candidate from error text (a lane's error event or a failed tool result) when it carries a product
    /// exception type such as "InvalidOperationException: ..."; null otherwise.
    /// </summary>
    public static DefectCandidate? FromErrorText(string? message, string? details = null, string? context = null)
    {
        var text = string.Join('\n', new[] { message, details }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (text.Length == 0) return null;
        foreach (Match match in TypedPrefix.Matches(text))
        {
            var fullType = match.Groups["type"].Value;
            var type = fullType[(fullType.LastIndexOf('.') + 1)..];
            if (IsEnvironmental(type)) return null;
            if (!ProductExceptionTypes.Contains(type) && !ProductFrame.IsMatch(text)) continue;
            var exceptionMessage = FirstLine(match.Groups["message"].Value);
            var frames = ProductFrames(text);
            var title = Bound($"{type}: {exceptionMessage}", 160);
            var body = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(context)) body.Append("Context: ").AppendLine(context.Trim());
            body.Append("Error: ").AppendLine(Bound(text.Trim(), 3000));
            var fingerprint = SelfImprovementFingerprint.Compute(SelfImprovementKind.Defect, type, exceptionMessage, string.Join("|", frames.Take(4).Select(StripLineNumbers)));
            return new DefectCandidate(title, body.ToString().TrimEnd(), fingerprint);
        }
        return null;
    }

    public static bool IsEnvironmental(string typeName)
        => EnvironmentalFragments.Any(fragment => typeName.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static Exception Unwrap(Exception exception)
    {
        var current = exception;
        for (var depth = 0; depth < 8; depth++)
        {
            switch (current)
            {
                case AggregateException { InnerExceptions.Count: > 0 } aggregate: current = aggregate.InnerExceptions[0]; break;
                case TargetInvocationException { InnerException: { } invoked }: current = invoked; break;
                default: return current;
            }
        }
        return current;
    }

    private static List<string> ProductFrames(string? stack)
    {
        if (string.IsNullOrWhiteSpace(stack)) return [];
        return stack.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("at ", StringComparison.Ordinal) && ProductFrame.IsMatch(l)).Take(12).ToList();
    }

    private static string StripLineNumbers(string frame)
    {
        var inIndex = frame.IndexOf(" in ", StringComparison.Ordinal);
        return inIndex > 0 ? frame[..inIndex] : frame;
    }

    private static string FirstLine(string? text)
    {
        var trimmed = (text ?? "").Trim();
        var newline = trimmed.IndexOfAny(['\r', '\n']);
        return newline < 0 ? trimmed : trimmed[..newline];
    }

    private static string Bound(string text, int max) => text.Length <= max ? text : text[..(max - 3)] + "...";
}

/// <summary>
/// Heuristics for "the person asked for something SwarmCode cannot do": a decline whose reason names a missing tool,
/// integration or capability (never a policy refusal), and an unknown slash command that is not a typo of a known one.
/// </summary>
public static class CapabilityGapHeuristics
{
    private static readonly string[] GapPhrases =
    [
        "no tool", "don't have a tool", "do not have a tool", "have no way", "no way to", "not have access", "don't have access",
        "do not have access", "cannot access", "can't access", "unable to access", "not supported", "isn't supported", "is not supported",
        "not available in talavine code", "not available in swarmcode", "no integration", "no ability", "lack the ability", "lacks the ability", "not able to", "cannot perform",
        "can't perform", "no capability", "not implemented", "no such command", "no command", "no setting", "missing capability", "missing feature",
        "would need a new", "does not exist yet", "doesn't exist yet", "not something i can do", "beyond what i can do",
    ];

    private static readonly string[] PolicyPhrases =
    [
        "policy", "harmful", "unsafe", "illegal", "inappropriate", "ethic", "malicious", "not allowed", "refuse", "sexual", "violence",
        "dangerous", "weapon", "exploit", "credential theft", "privacy of", "consent",
    ];

    public static bool IsCapabilityGap(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return false;
        var text = reason.ToLowerInvariant();
        if (PolicyPhrases.Any(text.Contains)) return false;
        return GapPhrases.Any(text.Contains);
    }

    /// <summary>
    /// An unknown slash command is a feature request when it is at least three letters and not within edit distance two
    /// of a command that exists (a typo), so "/hepl" is noise and "/deploy" is a request.
    /// </summary>
    public static bool IsUnknownCommandGap(string? command, IEnumerable<string> knownCommands)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        var name = command.Trim().TrimStart('/').ToLowerInvariant();
        if (name.Length < 3 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return false;
        foreach (var known in knownCommands)
        {
            var k = known.Trim().TrimStart('/').ToLowerInvariant();
            if (k == name) return false;
            if (Math.Abs(k.Length - name.Length) <= 2 && EditDistance(k, name) <= 2) return false;
        }
        return true;
    }

    internal static int EditDistance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }
}
