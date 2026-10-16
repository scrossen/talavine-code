using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SwarmCode.Hosting.Llm;
using SwarmCode.Hosting.Secrets;
using SwarmCore.LlmRouting.Configuration;

namespace SwarmCode.Hosting;

/// <summary>
/// BYOK key resolution. Keys live in the OS-keystore-protected vault
/// (<see cref="VaultApiKeyProvider"/>). A key still sitting in the legacy plaintext
/// <c>llm-settings.json</c> is moved into the vault the first time it is read and
/// removed from the JSON. After the vault come configuration and the standard
/// environment variables handled by <see cref="OptionsBasedApiKeyProvider"/>, then
/// SwarmCode's extra env aliases. Key values are never logged or printed.
/// </summary>
public sealed class SwarmCodeApiKeyProvider : ILlmApiKeyProvider
{
    private readonly VaultApiKeyProvider _vault;
    private readonly SettingsApiKeyProvider? _legacy;
    private readonly OptionsBasedApiKeyProvider _options;
    private readonly ILogger<SwarmCodeApiKeyProvider> _logger;
    private readonly ModelSourceEnvironmentKeys? _sourceEnvironment;

    public SwarmCodeApiKeyProvider(VaultApiKeyProvider vault, SettingsApiKeyProvider? legacy, LlmProvidersOptions options, ILogger<SwarmCodeApiKeyProvider>? logger = null, ModelSourceEnvironmentKeys? sourceEnvironment = null)
    {
        _vault = vault;
        _legacy = legacy;
        _options = new OptionsBasedApiKeyProvider(options);
        _logger = logger ?? NullLogger<SwarmCodeApiKeyProvider>.Instance;
        _sourceEnvironment = sourceEnvironment;
    }

    public async Task<string?> GetApiKeyAsync(string providerName)
    {
        var environmentKey = _sourceEnvironment?.Get(providerName);
        if (!string.IsNullOrWhiteSpace(environmentKey)) return environmentKey;
        var key = await _vault.GetApiKeyAsync(providerName).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(key)) return key;

        key = await MigrateLegacyAsync(providerName).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(key)) return key;

        key = await _options.GetApiKeyAsync(providerName).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(key)) return key;

        foreach (var variable in AliasVariables(providerName))
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    public Task<string?> GetCredentialReferenceAsync(string providerName) => _vault.GetCredentialReferenceAsync(providerName);

    public async Task<LlmCredential> GetCredentialAsync(string providerName)
    {
        var key = await GetApiKeyAsync(providerName).ConfigureAwait(false);
        if (string.IsNullOrEmpty(key)) return new LlmCredential(null, null);
        return new LlmCredential(key, await _vault.GetCredentialReferenceAsync(providerName).ConfigureAwait(false));
    }

    public Task SetApiKeyAsync(string providerName, string apiKey) => _vault.SetApiKeyAsync(providerName, apiKey);

    public Task<bool> ClearApiKeyAsync(string providerName)
        => _legacy is null ? _vault.ClearApiKeyAsync(providerName)
            : _legacy.ClearWithAuthorityAsync(providerName, () => _vault.ClearApiKeyAsync(providerName));

    /// <summary>Checked agent/editor replacement; a concurrent rotation or deletion wins.</summary>
    public Task<bool> SetApiKeyIfUnchangedAsync(string providerName, string key, SwarmCore.Secrets.SecretEntry? expected, CancellationToken ct = default)
        => _vault.Vault is SwarmCore.Secrets.IConditionalSecretVault conditional
            ? conditional.TrySetAsync(new SwarmCore.Secrets.SecretWriteRequest { Name = VaultApiKeyProvider.VaultName(providerName), Value = key.Trim(), Kind = SwarmCore.Secrets.SecretKinds.ApiKey }, expected, ct)
            : Task.FromResult(false);

    public Task<bool> ClearApiKeyIfUnchangedAsync(string providerName, SwarmCore.Secrets.SecretEntry? expected, CancellationToken ct = default)
    {
        async Task<bool> Delete()
        {
            if (expected is null) return !await _vault.Vault.HasAsync(VaultApiKeyProvider.VaultName(providerName), ct).ConfigureAwait(false);
            return _vault.Vault is SwarmCore.Secrets.IConditionalSecretVault conditional && await conditional.TryDeleteAsync(expected, ct).ConfigureAwait(false);
        }
        return _legacy is null ? Delete() : _legacy.ClearWithAuthorityAsync(providerName, Delete);
    }

    public async Task<bool> HasApiKeyAsync(string providerName)
        => !string.IsNullOrEmpty(await GetApiKeyAsync(providerName).ConfigureAwait(false));

    /// <summary>Moves a plaintext key from llm-settings.json into the vault and blanks the JSON copy.</summary>
    private async Task<string?> MigrateLegacyAsync(string providerName)
    {
        if (_legacy is null) return null;
        try
        {
            await _legacy.MigrateApiKeyAsync(providerName,
                key => _vault.TrySetApiKeyIfAbsentAsync(providerName, key)).ConfigureAwait(false);
            // An explicit vault write that won while migration was pending is
            // authoritative. Migration never replaces it with the legacy key.
            var currentVaultKey = await _vault.GetApiKeyAsync(providerName).ConfigureAwait(false);
            return string.IsNullOrEmpty(currentVaultKey)
                ? await _legacy.GetApiKeyAsync(providerName).ConfigureAwait(false)
                : currentVaultKey;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        {
            _logger.LogWarning("Could not migrate the {Provider} API key into the vault.", VaultApiKeyProvider.Canonical(providerName));
            // Adoption may have committed before the JSON cleanup failed, or a
            // newer vault writer may have won. Prefer its current authority.
            var currentVaultKey = await _vault.GetApiKeyAsync(providerName).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(currentVaultKey)) return currentVaultKey;
            try { return await _legacy.GetApiKeyAsync(providerName).ConfigureAwait(false); }
            catch (Exception readError) when (readError is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                return null;
            }
        }
    }

    private static string[] AliasVariables(string providerName) => providerName.ToUpperInvariant() switch
    {
        "CLAUDE" or "ANTHROPIC" => ["CLAUDE_API_KEY"],
        "OPENROUTER" => ["OPENROUTER"],
        "GEMINI" or "GOOGLE" => ["GEMINI_API_KEY"],
        _ => [],
    };
}
