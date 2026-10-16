using System.Text.RegularExpressions;

namespace SwarmCore.Agent.Runtime;

/// <summary>Recognizes explicit task-local restrictions in direct user input, never retrieved text.</summary>
public sealed record ExplicitSteeringIntent(bool PersonalOnly, bool Tomorrow, bool NoSchemaChanges = false)
{
    public static ExplicitSteeringIntent Parse(string message)
    {
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        var noSchema = Regex.IsMatch(message, @"\b(?:no\s+(?:database\s+)?schema\s+changes|(?:do\s+not|don't)\s+(?:change|modify|alter)\s+(?:the\s+)?(?:database\s+)?schema)\b", options);
        // Ambiguous/negative language remains ordinary steering input rather than a guessed hard restriction.
        if (Regex.IsMatch(message, @"\b(?:not|never|don't|do\s+not|isn't|instead\s+of)\b|\?", options)) return new(false, false, noSchema);
        var personal = Regex.IsMatch(message, @"\b(?:only\s+(?:use\s+)?(?:my\s+)?personal|personal\s+(?:accounts?\s+)?only)\b", options);
        var tomorrow = Regex.IsMatch(message, @"\b(?:make\s+(?:it|the\s+reminder)|move\b[^.!?]{0,80}|reschedule\b[^.!?]{0,80}|remind\s+me\b[^.!?]{0,80})\s+(?:to\s+)?tomorrow\b", options)
            || personal && Regex.IsMatch(message, @"\band\s+(?:make\s+it\s+)?tomorrow\b", options);
        return new(personal, tomorrow, noSchema);
    }
}
