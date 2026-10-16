namespace SwarmCode.Hosting;

/// <summary>
/// The id rule for file-backed stores whose file or folder name is a conversation id. It is explicit and
/// platform-independent: <see cref="Path.GetInvalidFileNameChars"/> differs by OS (macOS and Linux forbid
/// only '/' and NUL), so relying on it accepts <c>..\x</c> or <c>a:b</c> on one machine and rejects them on
/// another. Allowed: 1-<see cref="MaxLength"/> ASCII letters, digits, '-', '_' and '.', not starting with
/// '.' and never containing "..". Session ids (GUID "N"/"D" format) satisfy it.
/// </summary>
public static class StorageIds
{
    public const int MaxLength = 128;

    public static bool IsValid(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxLength || id[0] == '.' || id.Contains("..", StringComparison.Ordinal))
            return false;
        foreach (var c in id)
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))
                return false;
        return true;
    }

    /// <summary>Returns <paramref name="id"/> when it satisfies the rule; otherwise throws <see cref="ArgumentException"/>.</summary>
    public static string Validate(string? id, string paramName = "conversationId")
        => IsValid(id) ? id! : throw new ArgumentException("Invalid conversation id.", paramName);
}
