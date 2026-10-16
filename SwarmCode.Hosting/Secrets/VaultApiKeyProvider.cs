using System.Globalization;
using SwarmCore.LlmRouting.Configuration;
using SwarmCore.Secrets;

namespace SwarmCode.Hosting.Secrets;

/// <summary>
/// LLM provider API keys stored in the secret vault under <c>llm.&lt;provider&gt;</c>,
/// envelope-protected by the OS keystore. Provider aliases (Anthropic/Claude,
/// Google/Gemini, xAI/Grok) resolve to one entry so a key typed under either
/// name reaches the router.
/// </summary>
public sealed class VaultApiKeyProvider(ISecretVault vault) : ILlmApiKeyProvider
{
    public ISecretVault Vault => vault;

    public static string Canonical(string providerName) => providerName.Trim().ToUpperInvariant() switch
    {
        "ANTHROPIC" or "CLAUDE" => "Claude",
        "GOOGLE" or "GEMINI" => "Gemini",
        "XAI" or "GROK" => "Grok",
        "HF" or "HUGGINGFACE" => "HuggingFace",
        "OPENROUTERAI" or "OPENROUTER" => "OpenRouter",
        _ => providerName.Trim(),
    };

    public static string VaultName(string providerName) => SecretNames.ForLlmProvider(Canonical(providerName));

    public Task<string?> GetApiKeyAsync(string providerName)
        => vault.GetValueAsync(VaultName(providerName));

    public async Task<string?> GetCredentialReferenceAsync(string providerName)
    {
        var entry = await vault.GetEntryAsync(VaultName(providerName)).ConfigureAwait(false);
        return entry is null ? null : $"vault:{entry.Name}:{entry.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture)}";
    }

    public async Task<LlmCredential> GetCredentialAsync(string providerName)
    {
        var key = await GetApiKeyAsync(providerName).ConfigureAwait(false);
        if (string.IsNullOrEmpty(key)) return new LlmCredential(null, null);
        return new LlmCredential(key, await GetCredentialReferenceAsync(providerName).ConfigureAwait(false));
    }

    public Task SetApiKeyAsync(string providerName, string apiKey)
    {
        var canonical = Canonical(providerName);
        return vault.SetAsync(new SecretWriteRequest
        {
            Name = SecretNames.ForLlmProvider(canonical),
            Value = apiKey.Trim(),
            Kind = SecretKinds.ApiKey,
            Notes = $"{canonical} API key",
        });
    }

    public Task<bool> TrySetApiKeyIfAbsentAsync(string providerName, string apiKey)
    {
        var canonical = Canonical(providerName);
        return vault.TryCreateAsync(new SecretWriteRequest
        {
            Name = SecretNames.ForLlmProvider(canonical), Value = apiKey.Trim(),
            Kind = SecretKinds.ApiKey, Notes = $"{canonical} API key",
        });
    }

    public Task<bool> ClearApiKeyAsync(string providerName) => vault.DeleteAsync(VaultName(providerName));

    public Task<bool> HasApiKeyAsync(string providerName) => vault.HasAsync(VaultName(providerName)); // metadata only: no last-used write
}
