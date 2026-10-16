using System.Text.RegularExpressions;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.Coding.Routing;

/// <summary>Why a lane's turn did or did not deliver the request.</summary>
public enum LaneOutcomeKind
{
    /// <summary>The turn completed and nothing suggests a refusal.</summary>
    Delivered,
    /// <summary>The model declined the whole request on policy or capability grounds.</summary>
    Refused,
    /// <summary>The model did part of the work and declined the rest; only the remainder needs another lane.</summary>
    PartialRefusal,
    /// <summary>The route itself failed: CLI missing or signed out, quota, rate limit, network, provider error.</summary>
    RouteUnavailable,
    /// <summary>The turn ran out of time or context; another lane with more room may finish it.</summary>
    Exhausted,
    /// <summary>The model kept repeating the same failing action and was stopped by the harness.</summary>
    Stuck,
    /// <summary>The turn errored for another reason.</summary>
    Failed,
}

/// <summary>A structured decline the model made through the <c>harness.decline</c> tool.</summary>
public sealed record DeclineSignal(string Scope, string Reason, string? Remaining)
{
    public bool IsPartial => string.Equals(Scope, "part", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Classifies how a lane's turn ended. Typed signals win: a <see cref="DeclineSignal"/> from the
/// decline tool, then the provider's <see cref="LlmFailureCategory"/>; the text heuristics
/// remain as the fallback for lanes that only return prose (the subscription CLIs).
/// </summary>
public static partial class RefusalDetector
{
    // A deliberate decline is different from an inability to build/run/read a repository.
    // Diagnostic verb exclusions below must never hide "I won't build" or "I will not run".
    [GeneratedRegex(@"\A\W{0,3}(?:(?:(?:i['’]?m|i am)\s+)?(?:really\s+|very\s+)?sorry,?\s+(?:but\s+)?)?(?:i\s+)?(?:(?:still|also|absolutely|definitely)\s+)?(?:won['’]?t|will\s+not|must\s+decline)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingIntentionalRefusal();

    // Native agents sometimes summarize an intentional decline in past tense before
    // listing alternatives. Keep the policy reason in the same opening sentence so
    // ordinary reports such as "I didn't run tests because the SDK is missing" stay ordinary.
    [GeneratedRegex(@"\A\W{0,3}i\s+(?:didn['’]?t|did\s+not)\b[^.\r\n]{0,400}\b(?:because|since)\b[^.\r\n]{0,400}\b(?:pirat\w*|illegal|unlawful|counterfeit|prohibited|not\s+allowed|against\s+(?:my|our|the)\s+(?:policy|policies|guidelines)|violat\w*\s+(?:my|our|the)\s+(?:policy|policies|guidelines))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingRetrospectiveRefusal();

    // Diagnostic verbs can also express a deliberate decline when the same sentence
    // explicitly gives a policy or rights reason. Ordinary missing-file reports stay ordinary.
    [GeneratedRegex(@"\bi\s+(?:cannot|can['’]?t|am\s+unable\s+to)\s+(?:find|locate|get|access|run|build|read|assist\s+(?:in|with)|help\s+(?:in|with))\b[^.\r\n]{0,400}\b(?:because|since|as)\b[^.\r\n]{0,400}\b(?:do\s+not\s+have\s+(?:the\s+)?rights|pirat\w*|illegal|unlawful|prohibited|not\s+allowed|contrary\s+to\s+(?:legal|ethical)|against\s+(?:my|our|the)\s+(?:policy|policies|guidelines))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ContextualRefusal();

    // Opening-position refusals: the reply leads with a decline.
    // "I can't find/see/reproduce X" is a progress report, not a refusal, so those verbs are excluded.
    [GeneratedRegex(@"\A\W{0,3}(?:(?:(?:i['’]?m|i am)\s+)?(?:really\s+|very\s+)?sorry,?\s+(?:but\s+)?)?(?:i\s+)?(?:can['’]?t|cannot|won['’]?t|am\s+unable\s+to|am\s+not\s+able\s+to|will\s+not|must\s+decline|['’]m\s+not\s+able\s+to|['’]m\s+unable\s+to)\b(?!\s+(?:find|locate|see|tell|determine|confirm|verify|reproduce|read|open|run|build|reach|access|get|load|resolve|figure|check|know|recall|remember|be\s+sure|guarantee|promise)\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingRefusal();

    // Explicit policy language anywhere in a short reply.
    [GeneratedRegex(@"\b(?:against\s+(?:my|our|the)\s+(?:guidelines|policy|policies|usage\s+polic)|violat\w*\s+(?:my|our|the)\s+(?:guidelines|polic)|i\s+(?:must|have\s+to|need\s+to)\s+(?:decline|refuse)|not\s+something\s+i\s+can\s+(?:help|assist|do)|i\s+(?:can['’]?t|cannot|won['’]?t)\s+(?:help|assist|comply)\s+with\s+(?:that|this)|i\s+(?:can['’]?t|cannot|won['’]?t)\s+(?:ethically|legally)\s+(?:provide|supply|source|share|help|assist)|i['’]?m\s+not\s+(?:able|going)\s+to\s+(?:help|assist|do)|unable\s+to\s+(?:help|assist|comply|proceed)\s+with\s+(?:that|this))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PolicyLanguage();

    // A decline sentence inside a longer reply: a paragraph that starts with a refusal.
    [GeneratedRegex(@"(?:^|\n|[.!?]\s+)\W{0,3}(?:however[,;:]?\s+)?(?:i\s+)?(?:won['’]?t|cannot|can['’]?t|will\s+not|must\s+decline|refuse\s+to)\s+(?:(?:ethically|legally)\s+)?(?:help\s+with|assist\s+with|provide|supply|source|share|do|implement|write|create|build|modify|continue\s+with|proceed\s+with)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmbeddedRefusal();

    [GeneratedRegex(@"\b(?:that['’]s|this\s+is|that\s+is)\s+(?:the\s+)?(?:part|portion)\s+i\s+(?:won['’]?t|will\s+not|cannot|can['’]?t)\s+(?:help\s+with|assist\s+with|provide|supply|source|share|do|implement|write|create|build|modify)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitRemainingRefusal();

    // "Connection refused" / "actively refused it" is a socket error (a route problem), not a model's refusal.
    [GeneratedRegex(@"content[_\s-]?(?:policy|filter)|safety\s+(?:settings|system|filter)|blocked\s+by\s+safety|prohibited\s+content|policy\s+violation|(?<!connection\s|actively\s)\brefus(?:ed|al)\b|\bdeclined\s+this\s+request\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PolicyError();

    [GeneratedRegex(@"timed?\s*out|timeout|time\s+budget|hard\s+timeout|context\s+(?:window|length)|too\s+many\s+tokens|maximum\s+of\s+\d+\s+iterations|exceeded\s+the\s+maximum",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExhaustedError();

    // Provider and route phrasing only. Bare words (login, network, connection, executable, credits) and bare numbers
    // turn up in paths, stack traces and exit codes of failures that have nothing to do with the route, and a match
    // cools a healthy lane down and hides the real error behind a hand-off.
    [GeneratedRegex(@"not\s+installed|not\s+authenticated|not\s+(?:logged|signed)\s+in|log\s*in\s+required|sign\s*[- ]?in\s+(?:is\s+)?required|please\s+(?:log|sign)\s*[- ]?in\b"
        + @"|quota\s+(?:exceeded|exhausted|reached)|exceeded\s+(?:your\s+)?(?:current\s+)?quota|insufficient[_\s](?:credit|quota|funds)|\b(?:out\s+of|no\s+remaining)\s+credits?\b|\bcredit\s+balance\b|\bpayment\s+required\b|\bbilling\s+(?:hard\s+limit|issue|problem|required|details)\b"
        + @"|rate[\s_-]?limit|usage\s+limit"
        + @"|no\s+api\s+key|(?:invalid|missing|incorrect)\s+api\s+key|api\s+key\s+(?:is\s+)?(?:missing|invalid|not\s+(?:set|found|configured|valid))|\bunauthori[sz]ed\b"
        + @"|(?:\bHTTP(?:/[\d.]+)?|\bstatus(?:[\s_]?code)?|\berror|does\s+not\s+indicate\s+success:)\s*[:=]?\s*(?:40[13]|429|5\d\d)\b"
        + @"|\b(?:403\s+forbidden|429\s+too\s+many\s+requests|50[0-4]\s+\(?(?:internal\s+server\s+error|bad\s+gateway|service\s+unavailable|gateway\s+time-?out))\b"
        + @"|\b(?:service|model|provider|server|endpoint|route|lane)\s+(?:is\s+)?(?:currently\s+|temporarily\s+)?unavailable\b|no\s+provider|provider\s+not\s+(?:found|configured|available|registered)|model\s+not\s+found|unknown\s+model"
        + @"|\bcould\s+not\s+launch\b|\bfailed\s+to\s+launch\b|\bCLI\b[^.\n]{0,40}\bcould\s+not\s+be\s+started\b|executable\s+(?:was\s+)?not\s+found"
        + @"|connection\s+(?:refused|reset|timed\s+out|closed|aborted|failed)|(?:no|could\s+not\s+(?:establish|make))\s+(?:a\s+)?connection|network\s+(?:error|failure|is\s+unreachable|unreachable)|SocketException"
        + @"|\bDNS\s+(?:lookup|resolution|error|failure)|name\s+or\s+service\s+not\s+known|no\s+such\s+host|\bECONN(?:REFUSED|RESET|ABORTED)\b|\bENOTFOUND\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RouteError();

    /// <summary>Maximum length of a final answer that is still eligible to count as a whole-request refusal by policy language alone.</summary>
    public const int ShortReplyThreshold = 1500;

    /// <summary>True when the final answer reads as a refusal of all or part of the request.</summary>
    public static bool IsRefusal(string? finalText)
    {
        if (string.IsNullOrWhiteSpace(finalText)) return false;
        var text = finalText.Trim();
        if (LeadingIntentionalRefusal().IsMatch(text) || LeadingRetrospectiveRefusal().IsMatch(text)
            || ContextualRefusal().IsMatch(text) || LeadingRefusal().IsMatch(text)
            || ExplicitRemainingRefusal().IsMatch(text)) return true;
        if (text.Length <= ShortReplyThreshold && PolicyLanguage().IsMatch(text)) return true;
        return EmbeddedRefusal().IsMatch(text) && PolicyLanguage().IsMatch(text);
    }

    /// <summary>Classifies a turn error message by text alone.</summary>
    public static LaneOutcomeKind ClassifyError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return LaneOutcomeKind.Failed;
        if (PolicyError().IsMatch(message)) return LaneOutcomeKind.Refused;
        if (ExhaustedError().IsMatch(message)) return LaneOutcomeKind.Exhausted;
        if (RouteError().IsMatch(message)) return LaneOutcomeKind.RouteUnavailable;
        return LaneOutcomeKind.Failed;
    }

    /// <summary>Maps a provider's typed failure to a lane outcome, or null when the category says nothing about routing.</summary>
    public static LaneOutcomeKind? ClassifyCategory(LlmFailureCategory? category) => category switch
    {
        LlmFailureCategory.ContentPolicy => LaneOutcomeKind.Refused,
        LlmFailureCategory.RateLimit or LlmFailureCategory.QuotaExhausted or LlmFailureCategory.AuthInvalid
            or LlmFailureCategory.ModelNotFound or LlmFailureCategory.LaunchDeclined => LaneOutcomeKind.RouteUnavailable,
        LlmFailureCategory.Timeout or LlmFailureCategory.ContextOverflow => LaneOutcomeKind.Exhausted,
        LlmFailureCategory.MalformedReply => LaneOutcomeKind.Failed,
        _ => null,
    };

    /// <summary>Compatibility overload: text-only classification.</summary>
    public static LaneOutcomeKind Classify(string? finalText, string? errorMessage, bool mutatingToolsSucceeded)
        => Classify(finalText, errorMessage, mutatingToolsSucceeded, category: null, decline: null, stuck: false);

    /// <summary>
    /// Classifies a completed or errored turn. A refusal after mutating work is a
    /// <see cref="LaneOutcomeKind.PartialRefusal"/>: the next lane finishes only the
    /// remainder and never repeats what is already done.
    /// </summary>
    public static LaneOutcomeKind Classify(
        string? finalText,
        string? errorMessage,
        bool mutatingToolsSucceeded,
        LlmFailureCategory? category,
        DeclineSignal? decline,
        bool stuck)
    {
        if (stuck) return LaneOutcomeKind.Stuck;
        if (decline is not null)
            return decline.IsPartial || mutatingToolsSucceeded ? LaneOutcomeKind.PartialRefusal : LaneOutcomeKind.Refused;

        if (ClassifyCategory(category) is { } typed)
            return typed == LaneOutcomeKind.Refused && mutatingToolsSucceeded ? LaneOutcomeKind.PartialRefusal : typed;

        if (errorMessage is not null)
        {
            var byText = ClassifyError(errorMessage);
            return byText == LaneOutcomeKind.Refused && mutatingToolsSucceeded ? LaneOutcomeKind.PartialRefusal : byText;
        }

        if (!IsRefusal(finalText)) return LaneOutcomeKind.Delivered;
        return mutatingToolsSucceeded ? LaneOutcomeKind.PartialRefusal : LaneOutcomeKind.Refused;
    }
}
