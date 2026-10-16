namespace SwarmCore.Secrets;

/// <summary>OS-backed encryption for secrets that must be stored at rest.</summary>
public interface ISecretProtector
{
    /// <summary>
    /// Encrypt the supplied value for storage. Returns an empty string when the
    /// input is null or empty so callers can persist the result directly.
    /// </summary>
    string Protect(string? value);

    /// <summary>
    /// Decrypt a previously protected value. Returns an empty string when the
    /// cipher text is null/empty or cannot be decrypted (for example after key rotation).
    /// </summary>
    string Unprotect(string? protectedValue);
}
