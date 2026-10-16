namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// Naming contract for the Ollama provider family.
///
/// <list type="bullet">
/// <item><c>Ollama</c> is the built-in adapter for the daemon on this device.</item>
/// <item><c>Ollama@&lt;node&gt;</c> names an Ollama daemon on another computer that the
/// user owns on the same private network (a "LAN node"). Each node is its own
/// provider: its own settings row, its own cooldown domain, its own model catalog,
/// its own proofs.</item>
/// </list>
///
/// Both forms speak the same wire protocol and share model-tag, digest, cost, and
/// execution-contract semantics, so code that reasons about <em>what an Ollama
/// model is</em> should use <see cref="IsOllamaFamily"/>. Code that reasons about
/// <em>this machine's daemon</em> (starting the process, installing models on this
/// disk, charging this GPU's memory, capturing this device's hardware) must keep
/// using <see cref="IsLocalDaemon"/>; a LAN node never matches it.
/// </summary>
public static class OllamaProviderFamily
{
    public const string LocalProviderName = "Ollama";
    public const string LanNodePrefix = "Ollama@";

    /// <summary>
    /// The settings row's ProviderName column is 50 characters; leave room for the
    /// prefix and keep names readable in the routing UI.
    /// </summary>
    public const int MaxNodeNameLength = 40;

    /// <summary>True for the local daemon and every LAN node.</summary>
    public static bool IsOllamaFamily(string? providerName)
        => IsLocalDaemon(providerName) || IsLanNode(providerName);

    /// <summary>True only for this device's built-in Ollama adapter.</summary>
    public static bool IsLocalDaemon(string? providerName)
        => !string.IsNullOrWhiteSpace(providerName)
           && providerName.Trim().Equals(LocalProviderName, StringComparison.OrdinalIgnoreCase);

    /// <summary>True for a well-formed <c>Ollama@&lt;node&gt;</c> provider name.</summary>
    public static bool IsLanNode(string? providerName)
        => TryGetNodeName(providerName, out _);

    /// <summary>
    /// Extracts the node name from <c>Ollama@&lt;node&gt;</c>. Returns false for the
    /// plain local provider name and for anything that is not a valid node name.
    /// </summary>
    public static bool TryGetNodeName(string? providerName, out string nodeName)
    {
        nodeName = string.Empty;
        if (string.IsNullOrWhiteSpace(providerName)) return false;

        var trimmed = providerName.Trim();
        if (trimmed.Length <= LanNodePrefix.Length
            || !trimmed.StartsWith(LanNodePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return TryNormalizeNodeName(trimmed[LanNodePrefix.Length..], out nodeName, out _);
    }

    /// <summary>Builds the provider name for a node. Throws when the name is invalid.</summary>
    public static string BuildLanNodeProviderName(string nodeName)
    {
        if (!TryNormalizeNodeName(nodeName, out var normalized, out var error))
            throw new ArgumentException(error, nameof(nodeName));

        return LanNodePrefix + normalized;
    }

    /// <summary>
    /// Node names are short machine labels ("scottpc", "mac-studio"): letters, digits,
    /// '-', '_' and '.', 1–<see cref="MaxNodeNameLength"/> characters. Case is
    /// preserved for display; comparisons elsewhere are case-insensitive.
    /// </summary>
    public static bool TryNormalizeNodeName(string? raw, out string nodeName, out string? error)
    {
        nodeName = string.Empty;
        error = null;

        var value = raw?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            error = "Computer name is required.";
            return false;
        }

        if (value.Length > MaxNodeNameLength)
        {
            error = $"Computer name must be at most {MaxNodeNameLength} characters.";
            return false;
        }

        foreach (var c in value)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
                continue;

            error = "Computer name may only contain letters, digits, '-', '_' and '.'.";
            return false;
        }

        if (value.Equals(LocalProviderName, StringComparison.OrdinalIgnoreCase))
        {
            error = "\"Ollama\" is reserved for this computer's own Ollama.";
            return false;
        }

        nodeName = value;
        return true;
    }

    /// <summary>
    /// Provider name to use when a fixed per-provider catalog (defaults, vision
    /// families, execution contracts) should treat a LAN node exactly like the local
    /// daemon. Non-family names pass through unchanged.
    /// </summary>
    public static string CanonicalCatalogName(string providerName)
        => IsLanNode(providerName) ? LocalProviderName : providerName;
}
