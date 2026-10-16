namespace SwarmCore.Secrets;

/// <summary>
/// Platform key-wrap primitive used by envelope encryption. The OS keystore
/// wraps (encrypts) a locally generated 256-bit data key so the data key can be
/// persisted at rest without a bare key file on disk. On Windows this is DPAPI
/// (<c>ProtectedData</c>, CurrentUser scope); on Mac Catalyst it is Keychain
/// Services. The wrapped key is bound to this OS user/device and cannot be
/// unwrapped elsewhere. That is the point.
///
/// Hosts register the platform implementation; a headless or test host without a
/// usable keystore keeps <see cref="NoOpKeyStore"/>, which makes
/// <see cref="EnvelopeSecretProtector"/> fall back to the legacy plaintext marker
/// (with a logged warning) instead of throwing.
/// </summary>
public interface IOsKeyStore
{
    /// <summary>True when this keystore can actually wrap/unwrap on the current machine.</summary>
    bool IsAvailable { get; }

    /// <summary>Short stable identifier for the wrap mechanism ("dpapi", "keychain", "none").</summary>
    string ProviderId { get; }

    /// <summary>Encrypt (wrap) a raw data key for storage. Throws when unavailable.</summary>
    byte[] WrapKey(byte[] plaintextKey);

    /// <summary>Decrypt (unwrap) a previously wrapped data key. Throws when unavailable or foreign.</summary>
    byte[] UnwrapKey(byte[] wrappedKey);
}

/// <summary>Why an <see cref="IOsKeyStore"/> reports <see cref="IOsKeyStore.IsAvailable"/> = false.</summary>
public enum OsKeyStoreUnavailableReason
{
    None,

    /// <summary>The OS keystore exists but refused or failed the request (for example a locked keychain).</summary>
    Unavailable,

    /// <summary>The OS rejected this executable's signature or entitlement; only a correctly signed build can recover.</summary>
    MissingAppEntitlement,
}

/// <summary>Optional capability for keystores that can explain why they are unavailable.</summary>
public interface IOsKeyStoreStatus
{
    OsKeyStoreUnavailableReason UnavailableReason { get; }
}
