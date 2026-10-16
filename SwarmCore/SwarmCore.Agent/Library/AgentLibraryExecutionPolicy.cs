using System.Text.Json;

namespace SwarmCore.Agent.Library;

/// <summary>Source-neutral execution boundaries for installed personal browser agents.</summary>
public static class AgentLibraryExecutionPolicy
{
    public const string ImportToolId = "context.import-ai-memory";
    public static readonly IReadOnlySet<string> ToolIds = new HashSet<string>(StringComparer.Ordinal)
    {
        "shared.user-browser-visit", "shared.user-browser-snapshot",
        "shared.user-browser-click", "shared.user-browser-fill",
        ImportToolId, "context.list-ai-memory"
    };

    public static bool HasMarker(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try { using var doc = JsonDocument.Parse(json); return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("agentLibrary", out _); }
        catch (JsonException) { return json.Contains("agentLibrary", StringComparison.Ordinal); }
    }

    public static bool AllowsUrl(AgentLibraryInstallation installation, string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var candidate)
           && candidate.Scheme == Uri.UriSchemeHttps && candidate.UserInfo.Length == 0
           && installation.AllowedOrigins.Contains(candidate.GetLeftPart(UriPartial.Authority), StringComparer.OrdinalIgnoreCase);

    public static AgentLibrarySourceBinding? SourceForUrl(AgentLibraryInstallation installation, string? url)
        => AllowsUrl(installation, url)
            ? installation.Sources.SingleOrDefault(s => string.Equals(new Uri(s.HomeUrl).Authority,
                new Uri(url!).Authority, StringComparison.OrdinalIgnoreCase)) : null;

    public static bool AccountIdentifierMatches(string expected, IEnumerable<string> observed)
        => !string.IsNullOrWhiteSpace(expected) && observed.Any(value =>
            string.Equals(value.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase));
}
