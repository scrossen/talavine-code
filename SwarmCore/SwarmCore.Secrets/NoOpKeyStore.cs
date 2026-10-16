namespace SwarmCore.Secrets;

/// <summary>
/// Default <see cref="IOsKeyStore"/> for hosts without a real OS keystore
/// (headless services, unit tests, unsupported platforms). Reports unavailable so
/// <see cref="EnvelopeSecretProtector"/> falls back to the legacy plaintext path
/// instead of producing ciphertext nothing on this machine could ever decrypt.
/// </summary>
public sealed class NoOpKeyStore : IOsKeyStore
{
    public bool IsAvailable => false;

    public string ProviderId => "none";

    public byte[] WrapKey(byte[] plaintextKey)
        => throw new InvalidOperationException("No OS keystore is available on this host.");

    public byte[] UnwrapKey(byte[] wrappedKey)
        => throw new InvalidOperationException("No OS keystore is available on this host.");
}
