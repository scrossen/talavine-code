using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.Configuration;

namespace SwarmCode.Hosting.Llm;

/// <summary>
/// Legacy key location: the plaintext <c>llm-settings.json</c> provider settings. Kept so
/// keys saved by earlier SwarmCode builds are found and migrated into the protected vault
/// by <see cref="SwarmCodeApiKeyProvider"/>; new keys no longer land here.
/// </summary>
public sealed class SettingsApiKeyProvider : ILlmApiKeyProvider
{
    private readonly ILlmSettingsProvider _settings;

    public SettingsApiKeyProvider(ILlmSettingsProvider settings) => _settings = settings;

    public async Task<string?> GetApiKeyAsync(string providerName)
    {
        var settings = await _settings.GetProviderSettingsAsync(Canonical(providerName)).ConfigureAwait(false);
        return settings?.ApiKey;
    }

    public async Task SetApiKeyAsync(string providerName, string apiKey)
    {
        var name = Canonical(providerName);
        var existing = await _settings.GetProviderSettingsAsync(name).ConfigureAwait(false) ?? new LlmProviderSettings { ProviderName = name };
        existing.ApiKey = apiKey;
        existing.IsEnabled = true;
        var saved = await _settings.SaveProviderAsync(existing).ConfigureAwait(false);
        if (!saved.Success)
            throw new InvalidOperationException(saved.Error ?? "The provider changed. Reload before saving.");
    }

    /// <summary>Blanks the plaintext key after it has been moved into the vault. Keeps the provider enabled.</summary>
    public async Task ClearApiKeyAsync(string providerName, string? expectedApiKey = null)
    {
        var existing = await _settings.GetProviderSettingsAsync(Canonical(providerName)).ConfigureAwait(false);
        if (existing is null || string.IsNullOrEmpty(existing.ApiKey)) return;
        if (expectedApiKey is not null && !string.Equals(existing.ApiKey, expectedApiKey, StringComparison.Ordinal)) return;
        existing.ApiKey = null;
        var saved = await _settings.SaveProviderAsync(existing).ConfigureAwait(false);
        if (!saved.Success)
            throw new InvalidOperationException(saved.Error ?? "The provider changed. Reload before saving.");
    }

    public async Task<string?> MigrateApiKeyAsync(string providerName, Func<string, Task<bool>> adoptIfAbsent)
    {
        if (_settings is not SwarmCore.LlmRouting.Services.JsonFileLlmSettingsProvider json)
            return await GetApiKeyAsync(providerName).ConfigureAwait(false);
        string? observed = null;
        await json.TryConsumeProviderApiKeyAsync(Canonical(providerName), key =>
        {
            observed = key;
            return adoptIfAbsent(key);
        }).ConfigureAwait(false);
        return observed;
    }

    public async Task<bool> ClearWithAuthorityAsync(string providerName, Func<Task<bool>> clearAuthoritative)
    {
        if (_settings is SwarmCore.LlmRouting.Services.JsonFileLlmSettingsProvider json)
            return await json.ClearProviderApiKeyAsync(Canonical(providerName), clearAuthoritative).ConfigureAwait(false);
        var hadLegacyKey = !string.IsNullOrEmpty(await GetApiKeyAsync(providerName).ConfigureAwait(false));
        await ClearApiKeyAsync(providerName).ConfigureAwait(false);
        var clearedAuthority = await clearAuthoritative().ConfigureAwait(false);
        return hadLegacyKey || clearedAuthority;
    }

    public async Task<bool> HasApiKeyAsync(string providerName)
        => !string.IsNullOrEmpty(await GetApiKeyAsync(providerName).ConfigureAwait(false));

    // Map provider aliases to the canonical names SwarmCode stores under.
    private static string Canonical(string providerName) => providerName.ToUpperInvariant() switch
    {
        "ANTHROPIC" => "Claude",
        "GOOGLE" => "Gemini",
        _ => providerName,
    };
}
