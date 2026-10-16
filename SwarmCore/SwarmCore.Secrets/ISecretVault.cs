namespace SwarmCore.Secrets;

/// <summary>Well-known secret kinds. Free-form strings are allowed; these keep hosts consistent.</summary>
public static class SecretKinds
{
    public const string Generic = "generic";
    public const string ApiKey = "api-key";
    public const string Password = "password";
    public const string Token = "token";
}

/// <summary>
/// Metadata about one stored secret. Never carries the value or the notes (password-manager
/// exports keep recovery codes and TOTP seeds there); read notes with
/// <see cref="ISecretVault.GetNotesAsync"/>.
/// </summary>
public sealed record SecretEntry(
    string Name,
    string Kind,
    string? Site,
    string? UsernameHint,
    bool HasNotes,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    DateTime? LastUsedUtc,
    bool IsEncrypted);

/// <summary>One write to the vault. The value is held only until it is protected.</summary>
public sealed record SecretWriteRequest
{
    public required string Name { get; init; }
    public required string Value { get; init; }
    public string Kind { get; init; } = SecretKinds.Generic;
    public string? Site { get; init; }
    public string? Username { get; init; }
    public string? Notes { get; init; }
}

/// <summary>A site login: username plus password, both unprotected for the caller that needs them.</summary>
public sealed record SecretLogin(string Name, string? Username, string Password)
{
    public override string ToString() => $"SecretLogin({Name}, redacted)";
}

/// <summary>
/// Named secret storage with values protected at rest by an <see cref="ISecretProtector"/>.
/// Names are the model-visible handle (<c>{{secret:name}}</c>); values are injected
/// by host adapters at use time and never returned to a model.
/// </summary>
public interface ISecretVault
{
    /// <summary>Creates or replaces a secret. The name is normalized with <see cref="SecretNames.Normalize"/>.</summary>
    Task<SecretEntry> SetAsync(SecretWriteRequest request, CancellationToken ct = default);

    /// <summary>Creates only if the name is absent. False means already present or unsupported;
    /// the compatibility default never overwrites a secret.</summary>
    Task<bool> TryCreateAsync(SecretWriteRequest request, CancellationToken ct = default)
        => Task.FromResult(false);

    /// <summary>The secret value, or null when absent or undecryptable on this host. Records last use.</summary>
    Task<string?> GetValueAsync(string name, CancellationToken ct = default);

    /// <summary>Username and password for a stored login, or null when absent or undecryptable.</summary>
    Task<SecretLogin?> GetLoginAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// The secret's notes, unprotected for the caller that needs them, or null when there are
    /// none (or they cannot be decrypted on this host). Records last use like
    /// <see cref="GetValueAsync"/>. Vaults without notes keep the default.
    /// </summary>
    Task<string?> GetNotesAsync(string name, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    Task<SecretEntry?> GetEntryAsync(string name, CancellationToken ct = default);

    Task<bool> HasAsync(string name, CancellationToken ct = default);

    Task<bool> DeleteAsync(string name, CancellationToken ct = default);

    /// <summary>Every entry's metadata, ordered by name. Values are never included.</summary>
    Task<IReadOnlyList<SecretEntry>> ListAsync(CancellationToken ct = default);
}

/// <summary>Optional checked writes for editors that may race another vault writer. Null expected means create only.</summary>
public interface IConditionalSecretVault
{
    Task<bool> TrySetAsync(SecretWriteRequest request, SecretEntry? expected, CancellationToken ct = default);
    Task<bool> TryDeleteAsync(SecretEntry expected, CancellationToken ct = default);
}
