namespace SwarmCore.Agent.Culture;

/// <summary>Who the agents act on behalf of.</summary>
public enum AgentCulturePrincipalKind
{
    /// <summary>One person (a professional, a founder, a household).</summary>
    Person = 0,

    /// <summary>A company or organization.</summary>
    Company = 1,

    /// <summary>A named team inside a larger organization.</summary>
    Team = 2,
}

/// <summary>
/// The customer-owned description of the working culture every agent shares:
/// who the agents represent, what matters to that person or company, and how
/// they like things said. Stored by the host; rendered into prompt text by
/// <see cref="AgentCultureCharter"/>. Everything here is optional — an empty
/// profile still yields the full default culture.
/// </summary>
public sealed record AgentCultureProfile
{
    public const int MaxPrincipalNameLength = 120;
    public const int MaxValues = 12;
    public const int MaxValueLength = 240;
    public const int MaxNotesLength = 1200;
    public const int MaxLanguageLength = 60;

    public static readonly AgentCultureProfile Empty = new();

    /// <summary>
    /// A saved profile is an explicit choice, including a blank name. Only a
    /// profile that has never been saved inherits the host's contact-card default.
    /// Editors and runtimes use this same rule so their charter text agrees.
    /// </summary>
    public static AgentCultureProfile Resolve(AgentCultureProfile? saved, AgentCulturePrincipal? suggested) =>
        (saved ?? (suggested is null ? Empty : new AgentCultureProfile
        {
            PrincipalName = suggested.Name,
            PrincipalKind = suggested.Kind,
        })).Normalize();

    /// <summary>The person, company, or team the agents work for, as they want it written.</summary>
    public string? PrincipalName { get; init; }

    public AgentCulturePrincipalKind PrincipalKind { get; init; } = AgentCulturePrincipalKind.Person;

    /// <summary>Short, plain statements of what matters here ("Customers hear back the same day.").</summary>
    public IReadOnlyList<string> Values { get; init; } = [];

    /// <summary>Optional tone guidance ("friendly but never casual with customers").</summary>
    public string? ToneNotes { get; init; }

    /// <summary>Optional writing language, e.g. "English" or "Spanish". Null means the reader's language.</summary>
    public string? Language { get; init; }

    public DateTimeOffset? UpdatedUtc { get; init; }

    /// <summary>True when the customer has supplied anything beyond the defaults.</summary>
    public bool HasCustomization =>
        !string.IsNullOrWhiteSpace(PrincipalName)
        || Values.Count > 0
        || !string.IsNullOrWhiteSpace(ToneNotes)
        || !string.IsNullOrWhiteSpace(Language);

    /// <summary>
    /// Trims, caps, and strips control characters so stored text stays bounded
    /// and prompt-safe. Values that are blank after cleaning are dropped.
    /// </summary>
    public AgentCultureProfile Normalize()
    {
        var values = Values
            .Select(value => Clean(value, MaxValueLength))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxValues)
            .ToArray();

        return this with
        {
            PrincipalName = NullIfEmpty(Clean(PrincipalName, MaxPrincipalNameLength)),
            Values = values,
            ToneNotes = NullIfEmpty(Clean(ToneNotes, MaxNotesLength)),
            Language = NullIfEmpty(Clean(Language, MaxLanguageLength)),
        };
    }

    private static string Clean(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var chars = text
            .Where(c => !char.IsControl(c) || c == '\n')
            .ToArray();
        var cleaned = new string(chars).Trim();
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength].TrimEnd();
    }

    private static string? NullIfEmpty(string text) => text.Length == 0 ? null : text;
}
