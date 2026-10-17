using SwarmCore.Coding.SelfImprovement;

namespace SwarmCode.App.Services;

/// <summary>Explicit local feedback remains available without the developer-only repair worker.</summary>
internal sealed class UserBugIntake(ISelfImprovementBacklog backlog, Func<SelfImprovementOptions> options)
{
    private readonly SelfImprovementReporter _reporter = new(backlog, options);
    public static bool IsCommand(string text) => text.StartsWith("/bug", StringComparison.OrdinalIgnoreCase)
        && (text.Length == 4 || char.IsWhiteSpace(text[4]));
    public Task<SelfImprovementItem> SubmitAsync(SelfImprovementReporter? automaticReporter, string text, string? session)
        => (automaticReporter ?? _reporter).SubmitBugAsync(text.Length == 4 ? "" : text[4..].Trim(), session);
}
