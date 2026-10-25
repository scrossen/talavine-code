namespace SwarmCore.Coding.Goals;

public enum CodingGoalCommandKind { Status, Start, Pause, Resume, Cancel, Invalid }

/// <summary>Shared syntax for the console and desktop <c>/goal</c> command.</summary>
public sealed record CodingGoalCommand(CodingGoalCommandKind Kind, string? Objective = null, string? Error = null)
{
    public const string Name = "goal";
    public const string Description = "Keep working toward a verified goal: /goal <objective>; status, pause, resume, cancel";
    public const string Usage = "/goal <objective> starts a goal. Use /goal status, /goal pause, /goal resume, or /goal cancel. "
        + "Use /goal start <objective> when the objective begins with a command word.";

    public static bool IsInvocation(string text)
    {
        var split = text.IndexOfAny([' ', '\t', '\r', '\n']);
        return (split < 0 ? text : text[..split]).Equals("/" + Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Parses the arguments after <c>/goal</c>, preserving the objective's interior whitespace.</summary>
    public static CodingGoalCommand Parse(string? arguments)
    {
        var text = arguments?.Trim() ?? "";
        if (text.Length == 0) return new(CodingGoalCommandKind.Status);
        var split = text.IndexOfAny([' ', '\t', '\r', '\n']);
        var word = split < 0 ? text : text[..split];
        var remaining = split < 0 ? "" : text[(split + 1)..].Trim();
        if (word.Equals("start", StringComparison.OrdinalIgnoreCase))
            return remaining.Length == 0
                ? new(CodingGoalCommandKind.Invalid, Error: "Enter the goal after /goal start.")
                : new(CodingGoalCommandKind.Start, remaining);

        var kind = word.ToLowerInvariant() switch
        {
            "status" => CodingGoalCommandKind.Status,
            "pause" => CodingGoalCommandKind.Pause,
            "resume" => CodingGoalCommandKind.Resume,
            "cancel" => CodingGoalCommandKind.Cancel,
            _ => CodingGoalCommandKind.Start,
        };
        if (kind == CodingGoalCommandKind.Start) return new(kind, text);
        return remaining.Length == 0
            ? new(kind)
            : new(CodingGoalCommandKind.Invalid, Error: $"/goal {word.ToLowerInvariant()} takes no arguments. Use /goal start {text} to make that text the objective.");
    }
}
