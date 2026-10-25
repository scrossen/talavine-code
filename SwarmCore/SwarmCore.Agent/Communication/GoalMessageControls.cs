using System.Text.RegularExpressions;

namespace SwarmCore.Agent.Communication;

public sealed record GoalMessageControlsResult(string? Handle, GoalMessageKind? Kind, string Text);

/// <summary>Author-written routing/type controls only; never infers intent from natural language.</summary>
public static class GoalMessageControls
{
    public static GoalMessageControlsResult Parse(string text, bool replySubject = false)
    {
        var remaining = replySubject ? Regex.Replace(text, @"^(?:(?:re|fw|fwd):\s*)+", "", RegexOptions.IgnoreCase) : text;
        var start = remaining.TrimStart();
        var match = Regex.Match(start, @"^@[a-zA-Z0-9][a-zA-Z0-9_-]*(?=\s|[,:—–]|$)");
        var handle = match.Success ? match.Value : null;
        if (handle is not null) remaining = start[match.Length..].TrimStart(' ', '\t', '\r', '\n', ',', ':', '—', '–');
        var kind = Regex.Match(remaining.TrimStart(), @"^\[(question|idea|correction|answer)\](?=\s|$)", RegexOptions.IgnoreCase);
        GoalMessageKind? value = kind.Success ? kind.Groups[1].Value.ToLowerInvariant() switch
        { "question" => GoalMessageKind.Question, "idea" => GoalMessageKind.Idea, "correction" => GoalMessageKind.Correction, "answer" => GoalMessageKind.Answer, _ => null } : null;
        if (value is not null) remaining = remaining.TrimStart()[kind.Length..].TrimStart();
        return new(handle, value, remaining);
    }
}
