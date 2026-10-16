namespace SwarmCore.Secrets;

/// <summary>Opt-in encrypted-only boundary; unavailable protection never persists or reads legacy plaintext.</summary>
public sealed class EncryptionRequiredSecretProtector(ISecretProtector inner) : ISecretProtector
{
    public string Protect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var encrypted = inner.Protect(value);
        if (!encrypted.StartsWith(EnvelopeSecretProtector.EnvelopeMarker, StringComparison.Ordinal)
            || !string.Equals(inner.Unprotect(encrypted), value, StringComparison.Ordinal))
            throw new InvalidOperationException("Encrypted credential storage is unavailable. Enable the operating-system keystore before storing credentials.");
        return encrypted;
    }
    public string Unprotect(string? value) => value?.StartsWith(EnvelopeSecretProtector.EnvelopeMarker, StringComparison.Ordinal) == true
        ? inner.Unprotect(value) : string.Empty;
}
