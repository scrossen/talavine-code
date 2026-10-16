using System.Net;

namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// Security boundary for <see cref="LlmPrivacyClass.LocalOnly"/> routing.
/// A mutable <see cref="LlmProviderSettings.IsLocalProvider"/> flag is not proof
/// that a request stays on a local network: built-in cloud identities are always
/// remote, while custom providers must opt in and expose an explicitly local HTTP
/// endpoint. Hosts should call <see cref="TryValidateForSave"/> before persisting
/// provider settings and routers should call <see cref="IsTrustedLocalProvider"/>
/// again at execution time so legacy/tampered rows fail closed.
/// </summary>
public static class LlmProviderLocalityPolicy
{
    private static readonly HashSet<string> BuiltInLocalProviders = new(StringComparer.Ordinal)
    {
        "ollama",
        "lmstudio",
        "geniex",
    };

    private static readonly HashSet<string> BuiltInRemoteProviders = new(StringComparer.Ordinal)
    {
        "openai",
        "azureopenai",
        "claude",
        "anthropic",
        "gemini",
        "google",
        "grok",
        "xai",
        "deepseek",
        "openrouter",
        "huggingface",
        "hf",
        "mistral",
        "cohere",
        "bedrock",
        "awsbedrock",
        "claudecode",
        "claudecodesubscription",
        "codexcli",
        "codexsubscription",
        "geminicli",
        "geminisubscription",
        "remotepeer",
    };

    public static bool IsTrustedLocalProvider(LlmProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return IsTrustedLocalProvider(settings.ProviderName, settings);
    }

    public static bool IsTrustedLocalProvider(
        string providerName,
        LlmProviderSettings? settings = null)
    {
        if (string.IsNullOrWhiteSpace(providerName)) return false;

        var normalized = Normalize(providerName);
        if (BuiltInRemoteProviders.Contains(normalized)) return false;

        if (BuiltInLocalProviders.Contains(normalized))
        {
            // Built-in adapters own safe loopback defaults. If a caller changes
            // the endpoint, it must still satisfy the same local endpoint policy.
            return string.IsNullOrWhiteSpace(settings?.Endpoint)
                   || IsLocalEndpoint(settings.Endpoint);
        }

        return settings?.IsLocalProvider == true
               && IsLocalEndpoint(settings.Endpoint);
    }

    /// <summary>
    /// Model-aware LocalOnly boundary. Ollama can expose hosted models through a
    /// loopback daemon, so a trusted local endpoint does not by itself prove that
    /// the selected model executes on this device. The same applies to every
    /// <c>Ollama@node</c> LAN provider: the node's daemon can alias a cloud model
    /// exactly like the local one can.
    /// </summary>
    public static bool IsTrustedLocalRoute(
        string providerName,
        string? model,
        LlmProviderSettings? settings = null)
        => IsTrustedLocalProvider(providerName, settings)
           && (!OllamaProviderFamily.IsOllamaFamily(providerName)
               || !OllamaModelLocalityPolicy.IsCloudBacked(model));

    /// <summary>
    /// Validate a settings write that claims local-provider privileges. Non-local
    /// settings are always valid; they simply cannot satisfy LocalOnly routing.
    /// </summary>
    public static bool TryValidateForSave(
        LlmProviderSettings settings,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(settings);
        error = null;
        if (string.IsNullOrWhiteSpace(settings.ProviderName))
        {
            error = "Provider name is required.";
            return false;
        }
        if (!settings.IsLocalProvider) return true;

        var normalized = Normalize(settings.ProviderName);
        if (BuiltInRemoteProviders.Contains(normalized))
        {
            error = $"Provider {settings.ProviderName} is a built-in remote/cloud identity and cannot be marked local.";
            return false;
        }

        if (BuiltInLocalProviders.Contains(normalized))
        {
            if (string.IsNullOrWhiteSpace(settings.Endpoint) || IsLocalEndpoint(settings.Endpoint))
                return true;

            error = $"Local provider {settings.ProviderName} must use a loopback or private-network HTTP endpoint.";
            return false;
        }

        if (!IsLocalEndpoint(settings.Endpoint))
        {
            error = $"Custom provider {settings.ProviderName} can be marked local only with an explicit loopback or private-network HTTP endpoint.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// True only for absolute HTTP(S) endpoints whose host is syntactically
    /// loopback/private/link-local. Public IPs and DNS names other than the exact
    /// localhost name are rejected; this avoids trusting names that can be rebound.
    /// </summary>
    public static bool IsLocalEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)
            || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        var host = uri.Host.TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!IPAddress.TryParse(host, out var address))
            return false;

        if (IPAddress.IsLoopback(address))
            return true;

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10
                   || bytes[0] == 127
                   || (bytes[0] == 169 && bytes[1] == 254)
                   || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                   || (bytes[0] == 192 && bytes[1] == 168);
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
                return true;

            // RFC 4193 unique-local addresses: fc00::/7.
            var bytes = address.GetAddressBytes();
            return (bytes[0] & 0xFE) == 0xFC;
        }

        return false;
    }

    private static string Normalize(string providerName)
        => new(providerName.Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

    /// <summary>
    /// Display/classification-level check: does this provider <em>name</em>
    /// identify a built-in local runtime (Ollama / LM Studio / GenieX)? This is NOT the
    /// LocalOnly security gate — it looks at the name only, ignoring endpoints
    /// and custom-provider opt-ins, so it must never guard privacy-sensitive
    /// routing; use <see cref="IsTrustedLocalProvider(LlmProviderSettings)"/>
    /// (fail-closed) for that. Intended for cost labeling, health grouping,
    /// quiet-hours shifting, and the other classification call sites that used
    /// to hard-code "Ollama"/"LMStudio"/"LM Studio" comparisons. Matching uses
    /// the same alphanumeric normalization as the trust check, so spacing and
    /// casing variants ("LM Studio", "lmstudio") all classify identically.
    /// <c>Ollama@node</c> LAN providers classify as local too: they are free,
    /// private-network runtimes for every purpose this helper serves.
    /// </summary>
    public static bool IsLocalProviderName(string? providerName)
        => !string.IsNullOrWhiteSpace(providerName)
           && (BuiltInLocalProviders.Contains(Normalize(providerName))
               || OllamaProviderFamily.IsLanNode(providerName));
}

/// <summary>
/// Fail-closed identity policy for Ollama models that are executed by Ollama's
/// hosted service even when requests enter through the local daemon.
/// </summary>
public static class OllamaModelLocalityPolicy
{
    public static bool IsCloudBacked(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;

        var value = model.Trim();
        return value.EndsWith(":cloud", StringComparison.OrdinalIgnoreCase)
               || value.EndsWith("-cloud", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Combines the obvious registry tag with Ollama's authoritative
    /// <c>/api/show</c> <c>remote_host</c> metadata. Cloud models can be copied
    /// to an arbitrary local alias, so the name alone is never sufficient
    /// evidence.
    /// </summary>
    public static bool IsCloudBacked(
        string? model,
        string? remoteHost)
        => IsCloudBacked(model)
           || !string.IsNullOrWhiteSpace(remoteHost);

    /// <summary>
    /// True only when Ollama successfully returned model metadata and did not
    /// identify the route as hosted. A missing/failed metadata probe is unknown,
    /// not local. Capabilities are deliberately not used for locality: they
    /// describe model functions, while <c>remote_host</c> describes execution.
    /// </summary>
    public static bool HasAuthoritativeOnDeviceEvidence(
        string? model,
        bool metadataProbeSucceeded,
        string? remoteHost)
        => metadataProbeSucceeded
           && !IsCloudBacked(model, remoteHost);
}
