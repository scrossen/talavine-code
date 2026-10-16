using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SwarmCore.Agent.InstructionChat;

namespace SwarmCore.PersonalMemory;

/// <summary>Why the text screen refused a statement.</summary>
public enum MemoryScreenFinding
{
    None = 0,
    Secret = 1,
    Instruction = 2,
    HiddenCharacters = 3,
    Markup = 4,
}

/// <summary>
/// Deterministic content screens shared by capture, consolidation and user edits.
///
/// <para><b>Memory poisoning (OWASP ASI06).</b> A memory is a declarative statement about a
/// person. It is injected into future prompts, so anything that reads as an instruction —
/// role markers, "ignore previous instructions", text addressed to the assistant, tool or
/// action directives — is refused regardless of who wrote it. The one carve-out is a
/// <em>user-authored response-style preference</em> ("Keep answers short", "Never use emoji"),
/// which is the person configuring their own assistant; even then any action, tool,
/// override or exfiltration directive is refused.</para>
///
/// <para><b>Secrets.</b> Credential values and statements about credentials/security posture
/// are never stored. Special-category personal data (health, beliefs, finances,
/// identifiers without values) is admitted but flagged sensitive so the digest excludes it
/// by default.</para>
/// </summary>
public static partial class MemoryTextScreen
{
    /// <summary>NFC, collapse whitespace (newlines included), trim.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return WhitespaceRegex().Replace(text.Normalize(NormalizationForm.FormC), " ").Trim();
    }

    /// <summary>Lowercased, punctuation-insensitive form used for duplicate detection.</summary>
    public static string NormalizeForComparison(string? text)
    {
        var normalized = Normalize(text).ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (char.IsLetterOrDigit(c)) builder.Append(c);
            else if (builder.Length > 0 && builder[^1] != ' ') builder.Append(' ');
        }
        return builder.ToString().Trim();
    }

    /// <summary>True when the text contains a credential value or discusses credentials/security posture.</summary>
    public static bool ContainsSecret(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (CredentialShapeDetector.Detect(text).Count > 0) return true;
        if (CredentialStatementRegex().IsMatch(text)) return true;
        if (IdentifierValueRegex().IsMatch(text)) return true;
        foreach (Match m in CardNumberRegex().Matches(text))
        {
            if (PassesLuhn(m.Value)) return true;
        }
        return false;
    }

    /// <summary>Special-category or otherwise sensitive personal data (admitted, flagged, excluded from the digest by default).</summary>
    public static bool IsSensitive(string? text)
        => !string.IsNullOrWhiteSpace(text)
            && (HealthRegex().IsMatch(text)
                || SpecialCategoryRegex().IsMatch(text)
                || PersonalFinanceRegex().IsMatch(text)
                || IdentifierWordRegex().IsMatch(text));

    /// <summary>
    /// Screens a candidate memory. <paramref name="allowStylePreference"/> is true only for
    /// user-authored <see cref="MemoryCategory.ResponsePreference"/> statements.
    /// </summary>
    public static MemoryScreenFinding Screen(string? text, MemorySourceAuthority authority, bool allowStylePreference, out string? detail)
    {
        detail = null;
        if (string.IsNullOrWhiteSpace(text)) return MemoryScreenFinding.None;

        if (text.Any(IsHiddenOrControl))
        {
            detail = "Contains hidden, bidirectional or control characters.";
            return MemoryScreenFinding.HiddenCharacters;
        }

        if (ContainsSecret(text))
        {
            detail = "Looks like a credential or a statement about credentials/security.";
            return MemoryScreenFinding.Secret;
        }

        if (MarkupRegex().IsMatch(text))
        {
            detail = "Contains prompt/tool markup.";
            return MemoryScreenFinding.Markup;
        }

        if (RoleMarkerRegex().IsMatch(text) || OverrideRegex().IsMatch(text) || PersonaRegex().IsMatch(text))
        {
            detail = "Contains an instruction override or role marker.";
            return MemoryScreenFinding.Instruction;
        }

        if (AddressedToAssistantRegex().IsMatch(text))
        {
            detail = "Is addressed to the assistant rather than stating a fact.";
            return MemoryScreenFinding.Instruction;
        }

        if (ToolDirectiveRegex().IsMatch(text) || ExfiltrationRegex().IsMatch(text))
        {
            detail = "Contains a tool or data-movement directive.";
            return MemoryScreenFinding.Instruction;
        }

        if (ImperativeActionRegex().IsMatch(text))
        {
            detail = "Is an imperative action, not a statement about the person.";
            return MemoryScreenFinding.Instruction;
        }

        var styleDirective = StyleDirectiveRegex().IsMatch(text) || SecondPersonModalRegex().IsMatch(text);
        if (styleDirective && !(allowStylePreference && authority == MemorySourceAuthority.UserAuthored))
        {
            detail = "Reads as an instruction to the assistant.";
            return MemoryScreenFinding.Instruction;
        }

        if (authority != MemorySourceAuthority.UserAuthored && UrlRegex().IsMatch(text))
        {
            detail = "Links from non-user sources are not stored as memories.";
            return MemoryScreenFinding.Instruction;
        }

        return MemoryScreenFinding.None;
    }

    private static bool IsHiddenOrControl(char c)
    {
        if (c is '\n' or '\r' or '\t') return false;
        if (char.IsControl(c)) return true;
        var category = CharUnicodeInfo.GetUnicodeCategory(c);
        if (category == UnicodeCategory.Format) return true; // zero-width, bidi overrides, tags
        return category is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
    }

    private static bool PassesLuhn(string candidate)
    {
        var digits = candidate.Where(char.IsDigit).Select(c => c - '0').ToArray();
        if (digits.Length is < 13 or > 19) return false;
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i];
            if (i % 2 == 1)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
        }
        return sum % 10 == 0;
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(
        @"\b(?:pass\s?words?|pass\s?phrases?|pass\s?codes?|credentials?"
        + @"|log-?ins?(?![\s-]*(?:link|page|screen|url|button|form|prompt)s?\b)"
        + @"|(?:log|sign)-?in\s+(?:details|info|information|code|codes)"
        + @"|api\s+keys?|secret\s+keys?|private\s+keys?|ssh\s+keys?|encryption\s+keys?"
        + @"|access\s+tokens?|refresh\s+tokens?|auth(?:orization)?\s+tokens?|bearer\s+tokens?"
        + @"|one-?time\s+(?:code|codes|password|passcode)|otp|2fa|mfa|two-?factor|multi-?factor"
        + @"|security\s+(?:question|questions|answer|answers|code|codes)"
        + @"|recovery\s+(?:code|codes|key|keys|phrase)"
        + @"|seed\s+phrase|pin\s+(?:number|code)|(?:atm|debit|card)\s+pin)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialStatementRegex();

    // An identifier word followed by a value-looking token.
    [GeneratedRegex(
        @"\b(?:ssn|social\s+security(?:\s+number)?|passport(?:\s+number)?|driver'?s\s+licen[cs]e(?:\s+number)?|tax\s+id|ein|itin"
        + @"|account\s+(?:number|no\.?)|routing\s+(?:number|no\.?)|iban|card\s+number|cvv|cvc|member\s+id|policy\s+number)"
        + @"\s*(?:is|:|=|#)?\s*[A-Z0-9][A-Z0-9 -]{3,}\d",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierValueRegex();

    [GeneratedRegex(@"\b\d{3}-\d{2}-\d{4}\b|\b(?:\d[ -]?){13,19}\b", RegexOptions.CultureInvariant)]
    private static partial Regex CardNumberRegex();

    [GeneratedRegex(
        @"\b(?:diagnos(?:is|es|ed)|medications?|prescri(?:ption|ptions|bed)|chronic\s+(?:condition|illness|pain)"
        + @"|symptoms?|surger(?:y|ies)|hospitali[sz]ed|mental\s+health|therapist|therapy|psychiatr(?:y|ic|ist)"
        + @"|oncolog(?:y|ist)|cancer|diabet(?:es|ic)|pregnan(?:t|cy)|disabilit(?:y|ies)|blood\s+pressure"
        + @"|depression|anxiety|adhd|autism|hiv)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HealthRegex();

    [GeneratedRegex(
        @"\b(?:religio(?:n|us)|church|mosque|synagogue|temple|atheist|muslim|christian|jewish|hindu|buddhist"
        + @"|gay|lesbian|bisexual|transgender|sexual\s+orientation|political\s+(?:party|views?|affiliation)"
        + @"|democrat|republican|union\s+member|criminal\s+record|arrest(?:ed)?|convict(?:ed|ion)|immigration\s+status"
        + @"|ethnicity|race)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SpecialCategoryRegex();

    [GeneratedRegex(
        @"\b(?:(?:my|his|her|their|user'?s)\s+(?:salary|income|net\s+worth|debt|debts|loan|mortgage|credit\s+score|bank\s+balance|savings|bank\s+account))"
        + @"|\b(?:salary|income|net\s+worth|credit\s+score)\s+(?:is|of)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PersonalFinanceRegex();

    [GeneratedRegex(@"\b(?:ssn|social\s+security|passport|driver'?s\s+licen[cs]e|tax\s+id|medicare|medicaid|date\s+of\s+birth|dob)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierWordRegex();

    [GeneratedRegex(@"```|<\|[^>]*\|>|\[/?INST\]|<</?SYS>>|</?(?:system|instructions?|tool_?call|tool|function_?call|function|assistant|user|developer|memory|context)[^>]*>|\{\s*""(?:tool|tool_name|name|function|arguments|parameters)""\s*:",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MarkupRegex();

    [GeneratedRegex(@"(?:^|[\r\n.!?]\s*)(?:system|assistant|developer|user|human|ai)\s*(?:prompt|message)?\s*:",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RoleMarkerRegex();

    [GeneratedRegex(@"\b(?:ignore|disregard|forget|override|bypass|skip)\b[^.!?]{0,40}\b(?:previous|prior|above|earlier|all|any|your|system|safety|these|those|the)\b[^.!?]{0,30}\b(?:instructions?|rules?|prompts?|guidelines?|directions?|policies|polic(?:y)|guardrails?|restrictions?|messages?)\b|\bnew\s+(?:instructions?|rules?|system\s+prompt)\b|\bsystem\s+prompt\b|\bjailbreak\b|\bdeveloper\s+mode\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OverrideRegex();

    [GeneratedRegex(@"\b(?:you\s+are\s+now|from\s+now\s+on,?\s+you|act\s+as\s+(?:if|a|an|the)|pretend\s+(?:to\s+be|you)|roleplay\s+as|you\s+have\s+been\s+(?:granted|authorized|given\s+permission))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PersonaRegex();

    [GeneratedRegex(@"^\s*(?:hey\s+|hi\s+|dear\s+|note\s+to\s+(?:the\s+)?)?(?:assistant|ai|a\.i\.|claude|chatgpt|gpt|copilot|gemini|bot|model|agent|llm)\b\s*[,:;]|\b(?:to|for)\s+(?:the|any)\s+(?:ai|assistant|model|agent|llm|bot)\s*[,:]|\b(?:the|any|an|this)\s+(?:ai|assistant|model|agent|llm|bot)\s+(?:must|should|shall|will|needs?\s+to|has\s+to|is\s+(?:required|instructed|authorized))\b|\bas\s+an\s+ai\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AddressedToAssistantRegex();

    [GeneratedRegex(@"\b(?:call|invoke|run|execute|trigger)\s+(?:the\s+|a\s+|this\s+)?(?:[\w.-]+\s+)?(?:tool|function|command|script|plugin|mcp)\b|\b[a-z][a-z0-9_]*\.[a-z][a-z0-9_-]*\s*\(|\btool[_\s]?call\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ToolDirectiveRegex();

    [GeneratedRegex(@"\b(?:send|forward|email|e-mail|mail|post|upload|share|leak|exfiltrate|copy|bcc|cc)\b[^.!?]{0,60}\b(?:to|with)\b[^.!?]{0,40}(?:@|https?://|\bthis\s+address\b|\bthe\s+following\b)|\b(?:include|append|embed|add)\b[^.!?]{0,40}\b(?:link|url|image|pixel|tracking)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExfiltrationRegex();

    // Sentence-initial imperative action verbs ("Forward ...", "Please delete ..."). The verb must be
    // followed by an object, not a copula, so "Email is my preferred channel" stays declarative.
    [GeneratedRegex(@"(?:^|[.!?;]\s+)(?:please\s+|kindly\s+|now\s+|immediately\s+)?(?:send|forward|email|e-mail|reply\s+to|respond\s+to|wire|transfer|pay|delete|remove|erase|share|upload|download|click|visit|run|execute|install|grant|approve|authorize|reset|export|publish|buy|purchase|sms|call(?!\s+me\b)|schedule|cancel|unsubscribe|log\s+in(?:to)?|sign\s+in(?:to)?|navigate\s+to|browse\s+to|fetch|curl)\s+(?!is\b|are\b|was\b|were\b|has\b|have\b|and\b|or\b|of\b|for\s+work\b)[\w$@""'(]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImperativeActionRegex();

    // Style/behaviour directives that are legitimate only as the person's own response preference.
    [GeneratedRegex(@"(?:^|[.!?;]\s+)(?:please\s+)?(?:always|never|do\s+not|don't|dont|make\s+sure|be\s+sure|remember\s+to|stop|avoid|keep|use|write|answer|reply|respond|format|start|end|address\s+me|call\s+me|refer\s+to\s+me)\b(?!\s+(?:is|are|was|were|date|time)\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StyleDirectiveRegex();

    [GeneratedRegex(@"\byou\s+(?:must|should|shall|will|need\s+to|have\s+to|are\s+(?:required|instructed|expected|allowed|permitted)|may\s+now|can\s+now)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecondPersonModalRegex();

    [GeneratedRegex(@"https?://|\bwww\.[a-z0-9-]+\.[a-z]{2,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();
}
