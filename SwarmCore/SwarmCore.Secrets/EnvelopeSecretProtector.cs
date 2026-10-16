using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace SwarmCore.Secrets;

/// <summary>
/// Envelope-encryption <see cref="ISecretProtector"/>. Secrets are encrypted
/// with a random 256-bit AES-GCM data key; the DATA KEY is wrapped by the OS
/// keystore (<see cref="IOsKeyStore"/>: DPAPI on Windows, Keychain on Mac
/// Catalyst) and embedded alongside the ciphertext. There is no bare key file
/// on disk; the wrapped key is only unwrappable by this OS user/device.
///
/// Wire format for a protected value:
///   "v1:" + Base64( [4-byte BE wrappedKeyLen][wrappedKey][12-byte nonce][ciphertext][16-byte tag] )
///
/// Read compatibility (transparent, empty-in to empty-out):
///   "v1:"  AES-GCM decrypt (this class).
///   "pt:"  plaintext marker; value passed through unchanged.
///   other  unmarked value; treated as "not configured" (returns empty).
///
/// Upgrade-on-write: a "pt:" value re-protects as "v1:" the next time a caller
/// stores it, because <see cref="Protect"/> always emits "v1:" when a keystore
/// is available.
///
/// Graceful degradation: when no OS keystore is available (headless/test host),
/// <see cref="Protect"/> falls back to the "pt:" plaintext marker (with a
/// one-time warning) so the host still works; it never emits "v1:" ciphertext
/// that this machine could not later decrypt.
///
/// Fail-safe on decrypt: if a "v1:" value fails to decrypt (keystore
/// unavailable, key rotated, tampered), <see cref="Unprotect"/> returns empty
/// and logs loudly. It never mutates or returns the ciphertext, so callers must
/// never re-<see cref="Protect"/> the empty result over the only stored
/// ciphertext. A failed decrypt surfaces the credential as "needs attention"
/// rather than destroying it.
/// </summary>
public sealed class EnvelopeSecretProtector : ISecretProtector
{
    /// <summary>Prefix for AES-GCM envelope ciphertext produced by this class.</summary>
    public const string EnvelopeMarker = "v1:";

    /// <summary>Plaintext marker, recognized on read and used as fallback on write.</summary>
    public const string PlaintextMarker = "pt:";

    private const int KeySizeBytes = 32;   // AES-256
    private const int NonceSizeBytes = 12;  // AES-GCM standard nonce
    private const int TagSizeBytes = 16;    // AES-GCM tag

    /// <summary>
    /// How long a wrapped key waits after a transient keystore failure (broker not
    /// running, a Keychain prompt still open, a timeout) before the next unwrap attempt.
    /// Short enough that credentials come back soon after the keystore recovers; long
    /// enough that a dead broker is not called on every secret read.
    /// </summary>
    internal static readonly TimeSpan TransientUnwrapRetryDelay = TimeSpan.FromSeconds(5);

    private readonly IOsKeyStore _keyStore;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly object _keyLock = new();
    private readonly object _failedKeyLock = new();
    // Definitive failures only: this OS identity cannot unwrap the key (wrong user,
    // rotated key, tampered blob). Retrying cannot help until the process restarts.
    private readonly HashSet<string> _failedWrappedKeyDigests = new(StringComparer.Ordinal);
    // Transient failures: retried once the time has passed.
    private readonly Dictionary<string, DateTimeOffset> _transientUnwrapRetryAfter = new(StringComparer.Ordinal);

    private byte[]? _dataKey;        // unwrapped, cached in memory for the process lifetime
    private byte[]? _wrappedDataKey; // the wrapped form embedded in every value
    private byte[]? _readDataKey;
    private byte[]? _readWrappedDataKey;
    private bool _fallbackWarned;
    private bool _unavailableOnReadWarned;

    public EnvelopeSecretProtector(IOsKeyStore keyStore, ILogger<EnvelopeSecretProtector> logger)
        : this(keyStore, (ILogger)logger)
    {
    }

    /// <summary>For hosts that wrap this protector behind their own type and log under their own category.</summary>
    public EnvelopeSecretProtector(IOsKeyStore keyStore, ILogger logger)
        : this(keyStore, logger, TimeProvider.System)
    {
    }

    internal EnvelopeSecretProtector(IOsKeyStore keyStore, ILogger logger, TimeProvider time)
    {
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>True when values written now are envelope-encrypted rather than plaintext-marked.</summary>
    public bool IsEncrypting => _keyStore.IsAvailable;

    /// <summary>The keystore mechanism in use ("dpapi", "keychain", "none").</summary>
    public string KeyStoreProviderId => _keyStore.ProviderId;

    public string Protect(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        byte[] dataKey;
        byte[] wrappedKey;
        // The process data key, once wrapped, needs no keystore round trip; availability is
        // probed only when a key actually has to be wrapped.
        if (!TryGetWriteKey(out dataKey, out wrappedKey))
        {
            if (!_keyStore.IsAvailable)
            {
                WarnFallbackOnce();
                return PlaintextMarker + value;
            }

            try
            {
                (dataKey, wrappedKey) = GetOrCreateDataKey();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "OS keystore ({Provider}) key-wrap failed during Protect; falling back to plaintext marker.",
                    _keyStore.ProviderId);
                return PlaintextMarker + value;
            }
        }

        var plaintext = Encoding.UTF8.GetBytes(value);
        var nonce = new byte[NonceSizeBytes];
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSizeBytes];

        using (var aes = new AesGcm(dataKey, TagSizeBytes))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        var blob = new byte[4 + wrappedKey.Length + NonceSizeBytes + ciphertext.Length + TagSizeBytes];
        var span = blob.AsSpan();
        BinaryPrimitives.WriteInt32BigEndian(span[..4], wrappedKey.Length);
        var offset = 4;
        wrappedKey.CopyTo(span[offset..]); offset += wrappedKey.Length;
        nonce.CopyTo(span[offset..]); offset += NonceSizeBytes;
        ciphertext.CopyTo(span[offset..]); offset += ciphertext.Length;
        tag.CopyTo(span[offset..]);

        return EnvelopeMarker + Convert.ToBase64String(blob);
    }

    public string Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue))
            return string.Empty;

        if (protectedValue.StartsWith(EnvelopeMarker, StringComparison.Ordinal))
            return UnprotectEnvelope(protectedValue);

        if (protectedValue.StartsWith(PlaintextMarker, StringComparison.Ordinal))
            return protectedValue.Substring(PlaintextMarker.Length);

        _logger.LogDebug("Unprotect called with an unmarked value (length {Len}); returning empty.", protectedValue.Length);
        return string.Empty;
    }

    private string UnprotectEnvelope(string protectedValue)
    {
        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(protectedValue.Substring(EnvelopeMarker.Length));
        }
        catch (FormatException ex)
        {
            _logger.LogError(ex, "Protected secret is v1: but not valid base64; leaving stored ciphertext untouched. Secret needs re-entry.");
            return string.Empty;
        }

        if (blob.Length < 4)
        {
            _logger.LogError("Protected secret is a truncated v1: envelope; leaving stored ciphertext untouched. Secret needs re-entry.");
            return string.Empty;
        }

        var wrappedKeyLen = BinaryPrimitives.ReadInt32BigEndian(blob.AsSpan(0, 4));
        var minLen = 4 + (long)wrappedKeyLen + NonceSizeBytes + TagSizeBytes;
        if (wrappedKeyLen <= 0 || blob.Length < minLen)
        {
            _logger.LogError("Protected secret is a malformed v1: envelope (len {Len}, wrappedKeyLen {WrapLen}); leaving stored ciphertext untouched.", blob.Length, wrappedKeyLen);
            return string.Empty;
        }

        var offset = 4;
        var wrappedKey = blob.AsSpan(offset, wrappedKeyLen).ToArray(); offset += wrappedKeyLen;
        var nonce = blob.AsSpan(offset, NonceSizeBytes); offset += NonceSizeBytes;
        var cipherLen = blob.Length - offset - TagSizeBytes;
        var ciphertext = blob.AsSpan(offset, cipherLen); offset += cipherLen;
        var tag = blob.AsSpan(offset, TagSizeBytes);

        // A key this process already holds decrypts without consulting the keystore; only an
        // unwrap that must actually happen probes its availability.
        var dataKey = TryGetCachedDataKey(wrappedKey);
        if (dataKey is null && !_keyStore.IsAvailable)
        {
            var first = false;
            lock (_failedKeyLock)
            {
                if (!_unavailableOnReadWarned) { _unavailableOnReadWarned = true; first = true; }
            }
            if (first)
            {
                _logger.LogError(
                    "Found a v1: protected secret but no OS keystore is available on this host ({Provider}); cannot decrypt. Stored ciphertext left untouched. Further occurrences are logged at Debug.",
                    _keyStore.ProviderId);
            }
            else
            {
                _logger.LogDebug("v1: secret skipped: OS keystore ({Provider}) still unavailable.", _keyStore.ProviderId);
            }
            return string.Empty;
        }

        var wrappedKeyDigest = Convert.ToHexString(SHA256.HashData(wrappedKey));
        lock (_failedKeyLock)
        {
            if (dataKey is null)
            {
                // A definitive DPAPI/Keychain refusal is stable for this process and
                // OS identity. Avoid retrying and logging it on every read.
                if (_failedWrappedKeyDigests.Contains(wrappedKeyDigest))
                    return string.Empty;
                // A transient failure waits out its short delay before the next attempt.
                if (_transientUnwrapRetryAfter.TryGetValue(wrappedKeyDigest, out var retryAfter)
                    && _time.GetUtcNow() < retryAfter)
                    return string.Empty;
            }
        }
        try
        {
            if (dataKey is null)
            {
                dataKey = UnwrapDataKey(wrappedKey);
                lock (_failedKeyLock)
                {
                    _transientUnwrapRetryAfter.Remove(wrappedKeyDigest);
                }
            }
        }
        catch (CryptographicException ex)
        {
            // The keystore answered and refused: wrong user, rotated key or a tampered
            // blob (BrokeredKeyStore relays the app's refusal as this type too).
            bool firstFailure;
            lock (_failedKeyLock)
            {
                firstFailure = _failedWrappedKeyDigests.Add(wrappedKeyDigest);
                _transientUnwrapRetryAfter.Remove(wrappedKeyDigest);
            }
            if (firstFailure)
            {
                _logger.LogError(ex,
                    "OS keystore ({Provider}) failed to unwrap the data key for a v1: secret (key rotated / different user?). Stored ciphertext left untouched; further retries for this wrapped key are suppressed until restart.",
                    _keyStore.ProviderId);
            }
            return string.Empty;
        }
        catch (Exception ex)
        {
            // The keystore could not answer right now (broker not running, a Keychain
            // prompt still open, a timeout). The key is not known to be bad: retry it
            // shortly so credentials come back once the keystore recovers.
            bool firstFailure;
            lock (_failedKeyLock)
            {
                firstFailure = !_transientUnwrapRetryAfter.ContainsKey(wrappedKeyDigest);
                _transientUnwrapRetryAfter[wrappedKeyDigest] = _time.GetUtcNow() + TransientUnwrapRetryDelay;
            }
            if (firstFailure)
            {
                _logger.LogWarning(ex,
                    "OS keystore ({Provider}) could not unwrap the data key for a v1: secret right now; it will be retried after {RetrySeconds}s. Stored ciphertext left untouched.",
                    _keyStore.ProviderId, TransientUnwrapRetryDelay.TotalSeconds);
            }
            else
            {
                _logger.LogDebug(ex, "OS keystore ({Provider}) still cannot unwrap a v1: data key; retrying later.", _keyStore.ProviderId);
            }
            return string.Empty;
        }

        var plaintext = new byte[cipherLen];
        try
        {
            using var aes = new AesGcm(dataKey, TagSizeBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch (CryptographicException ex)
        {
            _logger.LogError(ex, "AES-GCM authentication failed decrypting a v1: secret (tampered or wrong key). Stored ciphertext left untouched.");
            return string.Empty;
        }

        return Encoding.UTF8.GetString(plaintext);
    }

    private bool TryGetWriteKey(out byte[] dataKey, out byte[] wrappedKey)
    {
        lock (_keyLock)
        {
            dataKey = _dataKey!;
            wrappedKey = _wrappedDataKey!;
            return _dataKey is not null && _wrappedDataKey is not null;
        }
    }

    /// <summary>The unwrapped key for <paramref name="wrappedKey"/> when this process already holds it.</summary>
    private byte[]? TryGetCachedDataKey(byte[] wrappedKey)
    {
        lock (_keyLock)
        {
            if (_wrappedDataKey is not null && _dataKey is not null
                && CryptographicOperations.FixedTimeEquals(_wrappedDataKey, wrappedKey))
                return _dataKey;
            if (_readWrappedDataKey is not null && _readDataKey is not null
                && CryptographicOperations.FixedTimeEquals(_readWrappedDataKey, wrappedKey))
                return _readDataKey;
            return null;
        }
    }

    private (byte[] DataKey, byte[] WrappedKey) GetOrCreateDataKey()
    {
        lock (_keyLock)
        {
            if (_dataKey is not null && _wrappedDataKey is not null)
                return (_dataKey, _wrappedDataKey);

            var raw = new byte[KeySizeBytes];
            RandomNumberGenerator.Fill(raw);
            var wrapped = _keyStore.WrapKey(raw);
            _dataKey = raw;
            _wrappedDataKey = wrapped;
            return (raw, wrapped);
        }
    }

    /// <summary>
    /// Unwrap a data key for a stored value. When the wrapped bytes match the
    /// process key the cached unwrapped key is reused; otherwise the OS keystore
    /// unwraps it (values written by a previous process run).
    /// </summary>
    private byte[] UnwrapDataKey(byte[] wrappedKey)
    {
        if (TryGetCachedDataKey(wrappedKey) is { } cached)
            return cached;

        var unwrapped = _keyStore.UnwrapKey(wrappedKey);

        // A readable historical wrapper is cached for reads but never adopted
        // for new writes: GetOrCreateDataKey wraps through the current store.
        lock (_keyLock)
        {
            _readDataKey ??= unwrapped;
            _readWrappedDataKey ??= wrappedKey;
        }

        return unwrapped;
    }

    private void WarnFallbackOnce()
    {
        if (_fallbackWarned) return;
        _fallbackWarned = true;
        _logger.LogWarning(
            "No OS keystore available ({Provider}); protecting secrets with the plaintext marker. " +
            "OS filesystem ACLs on the local app-data directory remain the effective protection.",
            _keyStore.ProviderId);
    }

    /// <summary>True if the value carries the v1: envelope marker.</summary>
    public static bool IsEnvelope(string? value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(EnvelopeMarker, StringComparison.Ordinal);
}
