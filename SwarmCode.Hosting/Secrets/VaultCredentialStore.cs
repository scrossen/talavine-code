using SwarmCore.Secrets;
using SwarmCore.Services.Abstractions;

namespace SwarmCode.Hosting.Secrets;

/// <summary>
/// Exposes the vault through SwarmCore's <see cref="ICredentialStore"/> seam so tools that
/// already understand <c>{{secret:name}}</c> (http.request headers, shell.exec command and
/// environment) resolve against the same database the user fills from Settings, /secret,
/// and CSV import. Values flow to the invoking adapter only, never to a model.
/// </summary>
public sealed class VaultCredentialStore(ISecretVault vault) : ICredentialStore
{
    public ISecretVault Vault => vault;

    public Task SetAsync(string name, string secret, CancellationToken ct = default)
        => vault.SetAsync(new SecretWriteRequest { Name = name, Value = secret }, ct);

    public Task<string?> GetAsync(string name, CancellationToken ct = default)
        => vault.GetValueAsync(name, ct);

    public Task<bool> HasAsync(string name, CancellationToken ct = default)
        => vault.HasAsync(name, ct);

    public Task DeleteAsync(string name, CancellationToken ct = default)
        => vault.DeleteAsync(name, ct);

    public async Task<IReadOnlyList<string>> ListNamesAsync(CancellationToken ct = default)
        => (await vault.ListAsync(ct).ConfigureAwait(false)).Select(e => e.Name).ToList();

    /// <summary>A vault entry is bound to its site (set by CSV import or when the secret is saved with one).</summary>
    public async Task<IReadOnlyList<string>> GetBoundHostsAsync(string name, CancellationToken ct = default)
    {
        var site = (await vault.GetEntryAsync(name, ct).ConfigureAwait(false))?.Site;
        return string.IsNullOrWhiteSpace(site) ? [] : [site];
    }

    /// <summary>A secret saved without a site (for example with /secret) takes the first host it is sent to.</summary>
    public Task<bool> TryBindHostOnFirstUseAsync(string name, string host, CancellationToken ct = default)
        => vault.TrySetSiteAsync(name, host, onlyWhenUnset: true, ct);
}
