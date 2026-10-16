namespace SwarmCore.PersonalMemory;

/// <summary>
/// Deterministic checks on the person's own message for the turn. Destructive or hiding memory actions
/// (forget, "don't bring this up") run only when the person asked for them in their own words, so text an
/// agent read elsewhere (an e-mail, a ticket, a repository file, a tool result) cannot erase or hide
/// memories by telling the model to call a tool. Every host gets this through
/// <see cref="PersonalMemoryTools"/>; it does not depend on the host having an approval channel.
/// </summary>
public static class MemoryUserIntent
{
    private static readonly string[] ForgetWords =
    [
        "forget", "delete", "remove", "erase", "wipe", "don't remember", "do not remember", "stop remembering", "unlearn",
    ];

    private static readonly string[] HideWords =
    [
        "don't mention", "do not mention", "stop mentioning", "don't bring", "do not bring", "stop bringing", "hide",
        "suppress", "never mention", "not bring up", "don't bring up", "stop bringing up",
    ];

    /// <summary>True when the person's own message asks to forget/delete something.</summary>
    public static bool AsksToForget(string? userMessage) => Contains(userMessage, ForgetWords);

    /// <summary>True when the person's own message asks to stop mentioning (or to forget) something.</summary>
    public static bool AsksToHide(string? userMessage) => Contains(userMessage, HideWords) || AsksToForget(userMessage);

    private static bool Contains(string? userMessage, IEnumerable<string> words)
    {
        if (string.IsNullOrWhiteSpace(userMessage)) return false;
        var text = userMessage.ToLowerInvariant().Replace('’', '\'');
        return words.Any(w => text.Contains(w, StringComparison.Ordinal));
    }

    /// <summary>
    /// The category for an explicit "remember" that did not name one. The person's own answer-style
    /// preference ("Always keep answers short") reads as a directive to the generic screen; when the text
    /// passes the screen only as a response preference (and carries no tool, exfiltration or override
    /// directive), it is a response preference. Everything else is <see cref="MemoryCategory.Other"/>.
    /// </summary>
    public static MemoryCategory InferCategory(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return MemoryCategory.Other;
        var asOther = MemoryTextScreen.Screen(text, MemorySourceAuthority.UserAuthored, allowStylePreference: false, out _);
        if (asOther != MemoryScreenFinding.Instruction) return MemoryCategory.Other;
        var asStyle = MemoryTextScreen.Screen(text, MemorySourceAuthority.UserAuthored, allowStylePreference: true, out _);
        return asStyle == MemoryScreenFinding.None ? MemoryCategory.ResponsePreference : MemoryCategory.Other;
    }
}
