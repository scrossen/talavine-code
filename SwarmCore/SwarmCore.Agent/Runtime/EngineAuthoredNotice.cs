namespace SwarmCore.Agent.Runtime;

/// <summary>
/// Marks user-role transcript messages that the tool-use engine wrote itself
/// (retry nudges, wrap-up notices, completion checks) so provenance-sensitive
/// consumers can tell them apart from text the user actually typed.
/// <para>
/// The engine sends these as <c>ChatRole.User</c> because that is the only
/// role every provider reliably honours mid-turn, but they carry no user
/// authority: an address or host mentioned inside one must never widen the
/// set of destinations the user is taken to have named. Every notice starts
/// with <see cref="Prefix"/>; the guard excludes such parts from user text.
/// </para>
/// </summary>
public static class EngineAuthoredNotice
{
    /// <summary>Stable prefix shared by every engine-authored notice.</summary>
    public const string Prefix = "(Automatic ";

    /// <summary>True when the text is an engine-authored notice rather than user input.</summary>
    public static bool IsEngineAuthored(string? text)
        => text is not null
           && text.AsSpan().TrimStart().StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// Builds a notice body. <paramref name="kind"/> is the notice family
    /// ("notice", "evidence-synthesis notice", "completion check") and
    /// <paramref name="body"/> the instruction text; the result always carries
    /// <see cref="Prefix"/> so it is recognisable as engine-authored.
    /// </summary>
    public static string Create(string kind, string body)
        => $"{Prefix}{kind}: {body})";
}
