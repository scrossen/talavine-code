using System.Text.RegularExpressions;

namespace SwarmCore.Agent.Continual;

/// <summary>
/// Classifies the grammatical form of an authentic objective for acceptance.
/// The visible reply is the deliverable for an information request: a question,
/// or an ask to explain, describe, summarize, compare, or review. Everything
/// else, including imperative wording this policy does not recognize, remains
/// action work that needs receipts or an independent audit. Every independent
/// clause must qualify; hosts still audit any turn that used tools, reported an
/// error, had a possible side effect, or promised future work.
/// </summary>
public static partial class ContinualObjectiveRequestForm
{
    // "Please, in two short sentences, briefly: what ..." needs three. The bound
    // keeps a long preamble from being skipped wholesale.
    private const int MaxLeadIns = 3;

    private static readonly HashSet<string> Prepositions = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "according", "across", "among", "around", "at", "based", "between", "beyond", "by",
        "concerning", "considering", "during", "for", "from", "given", "in", "including", "into", "of",
        "on", "out", "over", "per", "regarding", "through", "throughout", "to", "under", "via", "with",
        "within", "without",
    };

    public static bool IsInformationRequest(string? objectiveText)
    {
        if (string.IsNullOrWhiteSpace(objectiveText)) return false;

        // Tone, length, and topic framing precede the main clause: "In two short
        // sentences, what ...", "Briefly, how ...", "Please explain ...". Only
        // closed-class lead-ins are skipped. A lead-in that can carry work of its
        // own, such as a clause ("After you archive them,") or a gerund ("In
        // addition to archiving them,"), is kept, so the request stays audited.
        var clause = objectiveText.Trim();
        for (var i = 0; i < MaxLeadIns && TrySkipLeadIn(ref clause); i++)
        {
        }

        // A question does not waive a later instruction. Inspect every sentence
        // and explicit sequencing boundary, rather than only the first clause.
        var clauses = IndependentClauses().Split(clause).Where(c => !string.IsNullOrWhiteSpace(c)).ToArray();
        return clauses.Length > 0 && clauses.All(IsSingleInformationClause);
    }

    private static bool IsSingleInformationClause(string text)
    {
        var clause = text.Trim();
        for (var i = 0; i < MaxLeadIns && TrySkipLeadIn(ref clause); i++) { }
        var coordinated = CoordinatedClauses().Split(clause);
        if (coordinated.Length > 1)
        {
            // Preserve questions with simple nominal lists ("my emails and
            // documents?"). A multiword tail may be an independent instruction
            // and must qualify on its own. This is a grammatical fast path,
            // not a semantic proof of every possible single-word imperative.
            if (clause.EndsWith('?') && QuestionOpening().IsMatch(clause)
                && NominalListConjunction().Split(clause).Length == coordinated.Length
                && coordinated.Skip(1).All(part => SingleQuestionTerm().IsMatch(part.Trim())))
                return IsSingleInformationClause(coordinated[0].TrimEnd() + "?");
            // "How do I access a folder and its files so I can ask questions"
            // still asks for guidance. A possessive/determined noun followed
            // by the user's own purpose is not an instruction to the agent.
            // Keep this narrow: every conjunction must be an "and" noun-list
            // continuation, and any separate action clause stays audited.
            if (QuestionOpening().IsMatch(clause)
                && NominalListConjunction().Split(clause).Length == coordinated.Length
                && coordinated.Skip(1).All(part => DeterminedQuestionTerm().IsMatch(part.Trim())))
                return IsSingleInformationClause(coordinated[0]);
            // Questions can carry another instruction just as imperatives can.
            // Ambiguous coordinated noun lists take the semantic audit path;
            // a question word cannot waive a later independent action.
            return coordinated.All(IsSingleInformationClause);
        }
        if (InformationClause().IsMatch(clause)) return true;

        // Yes/no questions ("Is there a way to ...?", "Do you support ...?") ask
        // for information, but "Could you archive these?" and "Can we archive
        // them?" are polite commands: a modal whose subject includes the
        // assistant must name an informational verb.
        var sentence = FirstSentence().Match(clause).Value;
        return sentence.EndsWith('?')
               && AuxiliaryQuestion().IsMatch(sentence)
               && !PoliteCommand().IsMatch(sentence);
    }

    private static bool TrySkipLeadIn(ref string clause)
    {
        var lead = DiscourseMarker().Match(clause);
        if (!lead.Success)
        {
            lead = FramingPhrase().Match(clause);
            if (!lead.Success || HasGerundComplement(lead.Groups["phrase"].Value)) return false;
        }

        if (lead.Length == 0 || lead.Length >= clause.Length) return false;
        clause = clause[lead.Length..];
        return true;
    }

    private static bool HasGerundComplement(string phrase)
    {
        var words = phrase.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 1 < words.Length; i++)
        {
            if (Prepositions.Contains(words[i])
                && words[i + 1].Length > 4
                && words[i + 1].EndsWith("ing", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex(@"^(?:please|kindly|so|ok|okay|well|now|also|and|but|hey|hi|hello|just|actually|anyway)\b[\s,:;!.—–-]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DiscourseMarker();

    // "-ly" adverbs ("Briefly,", "Honestly:") frame the request. Imperative
    // verbs that also end in "-ly" ("Reply,", "Apply:", "Supply,") are the
    // request's action and must never be skipped as a lead-in.
    [GeneratedRegex(@"^(?<phrase>(?!(?:apply|reapply|reply|supply|resupply|comply|rely|multiply|imply|ally|rally|tally|fly|bully|sully|dally|belly|jelly|ply)\b)\p{L}{2,}ly|(?:about|according\s+to|across|among|around|at|based\s+on|between|beyond|by|concerning|considering|during|for|from|given|in|including|into|on|out\s+of|over|per|regarding|through|throughout|to|under|via|with|within|without)\b(?:\s+[\p{L}\p{N}'’-]+){0,7})\s*(?:[,:;—–]|\s-(?=\s))\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FramingPhrase();

    [GeneratedRegex(@"^(?:what|why|how|when|where|who|whom|whose|which|explain|describe|summari[sz]e|analy[sz]e|brainstorm|review|compare|tell\s+me|does|do\s+you\s+think|should|(?:can|could|would|will)\s+you\s+(?:please\s+)?(?:tell|explain|describe|summari[sz]e))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InformationClause();

    [GeneratedRegex(@"^[^.?!\r\n]*[.?!]?", RegexOptions.CultureInvariant)]
    private static partial Regex FirstSentence();

    [GeneratedRegex(@"^(?:(?:is|are|was|were|do|does|did|has|have|had|could|should|would|must)(?:n['’]t)?|am|can|can['’]t|will|won['’]t|may|might|shall)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuxiliaryQuestion();

    [GeneratedRegex(@"^(?:can|can['’]t|could|couldn['’]t|would|wouldn['’]t|will|won['’]t|shall)\s+(?:you|we)\b(?!\s+(?:please\s+)?(?:tell|explain|describe|summari[sz]e)\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PoliteCommand();

    [GeneratedRegex(@"(?<=[.!?])\s*(?=\p{L})|;\s*|[\r\n]+|,\s*(?:and|but)\s+|(?:,?\s+)(?:and\s+then|then)\s+|\s+(?:and|but)\s+(?=(?:please|kindly|(?:can|could|would|will|shall)\s+(?:you|we))\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IndependentClauses();

    [GeneratedRegex(@"\s+(?:and|but)\s+|,\s*|\s+[—–]\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CoordinatedClauses();

    [GeneratedRegex(@"^(?:what|why|how|when|where|who|whom|whose|which|is|are|was|were|do|does|did|has|have|had|could|should|would|must|am|can|will|may|might|shall)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuestionOpening();

    [GeneratedRegex(@"^\p{L}+\??$", RegexOptions.CultureInvariant)]
    private static partial Regex SingleQuestionTerm();

    [GeneratedRegex(@"^(?:my|your|his|her|its|our|their|this|that|these|those|a|an|the)\s+\p{L}+(?:\s+so(?:\s+that)?\s+I\s+(?:can|could|may|might)\s+[^.!?;]+)?[?!.]?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeterminedQuestionTerm();

    [GeneratedRegex(@"\s+and\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NominalListConjunction();
}
