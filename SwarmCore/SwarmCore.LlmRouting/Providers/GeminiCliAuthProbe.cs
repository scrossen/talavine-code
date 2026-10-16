using System.Text.Json;

namespace SwarmCore.LlmRouting.Providers;

/// <summary>
/// File-based subscription-session probe for the Gemini CLI. Unlike Claude
/// Code (`auth status`) and Codex (`login status`), the Gemini CLI exposes no
/// non-interactive login-status command — its auth state lives in
/// <c>~/.gemini/settings.json</c> (selected auth method) plus cached OAuth
/// credentials. Reading those files is free, safe, and never launches the CLI.
///
/// Only Google-account OAuth ("oauth-personal") counts as an authenticated
/// subscription session: API-key and Vertex auth methods are metered, and the
/// subscription CLI lane must never silently incur API spend.
/// </summary>
public static class GeminiCliAuthProbe
{
    private static readonly JsonDocumentOptions LenientJson = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private static readonly string[] OAuthCredentialFileNames =
    [
        "oauth_creds.json",
        "google_accounts.json"
    ];

    public static (bool IsAuthenticated, string Detail) Probe(string? configDirectory = null)
    {
        var directory = string.IsNullOrWhiteSpace(configDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini")
            : configDirectory;

        var settingsPath = Path.Combine(directory, "settings.json");
        if (!File.Exists(settingsPath))
        {
            return (false,
                "Gemini CLI is installed but has never been authenticated (no settings.json). " +
                "Run `gemini` in a terminal and choose Login with Google.");
        }

        string? authType;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath), LenientJson);
            authType = ReadSelectedAuthType(doc.RootElement);
        }
        catch (Exception)
        {
            return (false, "Gemini CLI settings.json could not be read; re-run `gemini` to repair its configuration.");
        }

        if (string.IsNullOrWhiteSpace(authType))
        {
            return (false,
                "Gemini CLI has no auth method configured. Run `gemini` in a terminal and choose Login with Google.");
        }

        if (!authType.Equals("oauth-personal", StringComparison.OrdinalIgnoreCase))
        {
            return (false,
                $"Gemini CLI is configured for '{authType}' auth, which is metered API access. " +
                "Subscription routing stays disabled to avoid unexpected spend; switch the CLI to Login with Google to enable it.");
        }

        var hasCachedCredentials = OAuthCredentialFileNames
            .Any(name => File.Exists(Path.Combine(directory, name)));
        if (!hasCachedCredentials)
        {
            return (false,
                "Gemini CLI is set to Google login but has no cached credentials. " +
                "Run `gemini` in a terminal to complete the browser sign-in.");
        }

        return (true, "Gemini CLI has an authenticated Google-account session.");
    }

    /// <summary>
    /// Current schema nests the value at security.auth.selectedType; older CLI
    /// versions wrote a root-level selectedAuthType. Accept both.
    /// </summary>
    private static string? ReadSelectedAuthType(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;

        if (root.TryGetProperty("security", out var security)
            && security.ValueKind == JsonValueKind.Object
            && security.TryGetProperty("auth", out var auth)
            && auth.ValueKind == JsonValueKind.Object
            && auth.TryGetProperty("selectedType", out var selected)
            && selected.ValueKind == JsonValueKind.String)
        {
            return selected.GetString();
        }

        if (root.TryGetProperty("selectedAuthType", out var legacy)
            && legacy.ValueKind == JsonValueKind.String)
        {
            return legacy.GetString();
        }

        return null;
    }
}
