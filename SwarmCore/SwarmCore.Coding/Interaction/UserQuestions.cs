namespace SwarmCore.Coding.Interaction;

/// <summary>One choice the person can pick; <see cref="Description"/> is an optional one-line explanation.</summary>
public sealed record UserQuestionOption(string Label, string? Description = null);

/// <summary>
/// A question the agent wants the person to answer in the middle of a turn. <see cref="ConversationId"/> is the
/// trusted conversation of the asking turn (never model input), so a host can route the card to the right session
/// and decide whether anyone is there to answer.
/// </summary>
public sealed record UserQuestionRequest(
    string ConversationId,
    string Question,
    IReadOnlyList<UserQuestionOption> Options,
    bool AllowMultiple = false,
    bool AllowFreeText = true,
    string? Context = null)
{
    public const int MaxQuestionLength = 2000;
    public const int MaxOptions = 8;
    public const int MaxLabelLength = 120;
    public const int MaxDescriptionLength = 400;
    public const int MaxContextLength = 1000;
}

/// <summary>The person's answer, or why there is none.</summary>
public sealed record UserAnswer(bool Answered, string? Text, IReadOnlyList<string> Selected, string Message)
{
    public const string NoOneMessage = "No one can answer questions in this session. Decide yourself, state your assumption, and continue.";
    public const string DeclinedMessage = "The person skipped the question. Decide yourself, state your assumption, and continue.";

    /// <summary>Nobody is watching the session (an agent's session, a scheduled run, a non-interactive console).</summary>
    public static UserAnswer NoOne { get; } = new(false, null, [], NoOneMessage);

    /// <summary>The person saw the question and chose not to answer.</summary>
    public static UserAnswer Declined { get; } = new(false, null, [], DeclinedMessage);

    public static UserAnswer From(string? text, IReadOnlyList<string>? selected)
    {
        text = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        var chosen = selected?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct(StringComparer.Ordinal).ToList() ?? [];
        if (text is null && chosen.Count == 0) return Declined;
        var message = chosen.Count switch
        {
            0 => "The person answered in their own words.",
            1 => text is null ? "The person chose one option." : "The person chose one option and added a note.",
            _ => text is null ? "The person chose several options." : "The person chose several options and added a note.",
        };
        return new UserAnswer(true, text, chosen, message);
    }
}

/// <summary>Host seam that shows a question to the person and returns the answer. The turn's token cancels the wait.</summary>
public interface IUserQuestionBroker
{
    Task<UserAnswer> AskAsync(UserQuestionRequest request, CancellationToken ct);
}

/// <summary>Harness default: nobody can answer, so the model is told to decide and continue. Never throws.</summary>
public sealed class NoUserQuestionBroker : IUserQuestionBroker
{
    public static NoUserQuestionBroker Instance { get; } = new();

    public Task<UserAnswer> AskAsync(UserQuestionRequest request, CancellationToken ct) => Task.FromResult(UserAnswer.NoOne);
}

public static class UserQuestionPrompts
{
    /// <summary>System-prompt guidance for hosts that register <c>user.ask</c>.</summary>
    public const string Guidance =
        "Asking the person: user.ask pauses your turn and shows the person a question with optional choices and a free-text "
        + "answer. Use it only for a decision that is genuinely theirs (a product choice, an ambiguity the code and docs cannot "
        + "settle, an action with consequences they should pick), not for anything you can find out or decide yourself. Batch "
        + "related questions into one call with options rather than asking one at a time, keep the question short, and put the "
        + "background in context. When the answer comes back with answered=false, no one can answer in this session or the person "
        + "skipped it: do not ask again, make the most reasonable choice, state the assumption in your reply, and continue.";
}
