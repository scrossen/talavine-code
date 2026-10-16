namespace SwarmCore.Coding.Routing;

public static class CodingContinuationPrompt
{
    public static string Steer(string originalTask, string correction)
        => Resume(new CodingContinuationContext(originalTask, [correction]));

    public static string Resume(CodingContinuationContext context)
    {
        return "Continue the current task with the user's updates.\n\n" +
        "Keep the original objective and its unfinished work. New information, credentials, answers and constraints support that objective; " +
        "they are not a replacement task. Change or cancel the objective only when the user explicitly asks to do so, and honor that change. " +
        "Use the saved conversation and tool receipts to resume at the next useful step. Inspect only state needed to resolve an interrupted action. " +
        "A tool call without a saved result has an unknown outcome. " +
        "Do not repeat a potentially completed external action; verify it through a read-only check or report uncertainty. " +
        "A credential reference is already stored: use the registered tool or credential interface under current authority. " +
        "Do not dump process environments or search vault files to use it. Resolve a supporting setup step only as far as needed, then return to the objective. " +
        "Do not announce a change of direction for a supporting update. Complete and verify the remaining requested outcome.\n\n" +
        "Original task:\n" + context.OriginalRequest +
        string.Concat(context.Updates.Select((update, i) => $"\n\nUser update {i + 1}:\n{update}"));
    }
}

/// <summary>Protected user intent, kept separate from generated continuation instructions.</summary>
public sealed record CodingContinuationContext(string OriginalRequest, IReadOnlyList<string> Updates)
{
    public CodingContinuationContext WithUpdate(string update) => new(OriginalRequest, [.. Updates, update]);
    [System.Text.Json.Serialization.JsonIgnore]
    public string AuthenticatedUserRequest => string.Join("\n\n", new[] { OriginalRequest }.Concat(Updates));
}
