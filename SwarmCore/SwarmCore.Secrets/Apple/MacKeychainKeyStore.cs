#if MACOS
using System.Security.Cryptography;
using Foundation;
using Microsoft.Extensions.Logging;
using Security;
using SwarmCore.Secrets;

namespace SwarmCore.Secrets.Apple;

/// <summary>
/// macOS (AppKit) <see cref="IOsKeyStore"/> backed by Keychain Services, following the
/// Talavine keystore strategy. Shared source: each macOS app head links this file
/// (a net11.0 class library cannot compile against the Apple bindings, so it is
/// empty there) and passes its own persisted Keychain service name. Keychain stores secrets rather than returning encrypted
/// blobs, so this store keeps one stable 256-bit key-encryption-key (KEK) in the keychain
/// and implements <see cref="WrapKey"/>/<see cref="UnwrapKey"/> as AES-GCM under that KEK.
/// The KEK persists across launches (login keychain, scoped to this app's identity), so
/// envelope-protected values written on a prior run still decrypt, matching DPAPI
/// CurrentUser behaviour on Windows.
///
/// Wrapped-blob layout: [12-byte nonce][ciphertext(32)][16-byte tag].
///
/// If Keychain access fails (locked keychain, missing entitlement) the store reports
/// <see cref="IsAvailable"/> = false and the Mac vault refuses plaintext fallback.
/// </summary>
public sealed class MacKeychainKeyStore : IOsKeyStore, IOsKeyStoreStatus
{
    private const string AccountName = "envelope-kek-v1";
    private const int KekSizeBytes = 32;
    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;

    private readonly object _kekLock = new();
    private readonly ILogger<MacKeychainKeyStore> _logger;
    // Persisted Apple identity: existing keychain items live under this service name.
    // Renaming it orphans every secret written so far.
    private readonly string _serviceName;
    private readonly bool _available;
    private byte[]? _kek;
    private OsKeyStoreUnavailableReason _unavailableReason = OsKeyStoreUnavailableReason.Unavailable;

    public MacKeychainKeyStore(ILogger<MacKeychainKeyStore> logger, string serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName)) throw new ArgumentException("A Keychain service name is required.", nameof(serviceName));
        _logger = logger;
        _serviceName = serviceName;
        try
        {
            _kek = LoadOrCreateKek();
            _available = _kek is not null;
            if (_available) _unavailableReason = OsKeyStoreUnavailableReason.None;
            else logger.LogWarning("Keychain KEK load/create failed; encrypted credential storage is unavailable.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Keychain unavailable on this host; encrypted credential storage is unavailable.");
            _available = false;
        }
    }

    public bool IsAvailable => _available;

    public OsKeyStoreUnavailableReason UnavailableReason => _unavailableReason;

    public string ProviderId => "keychain";

    public byte[] WrapKey(byte[] plaintextKey)
    {
        var kek = RequireKek();
        var nonce = new byte[NonceSizeBytes];
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[plaintextKey.Length];
        var tag = new byte[TagSizeBytes];
        using (var aes = new AesGcm(kek, TagSizeBytes))
        {
            aes.Encrypt(nonce, plaintextKey, ciphertext, tag);
        }
        return [.. nonce, .. ciphertext, .. tag];
    }

    public byte[] UnwrapKey(byte[] wrappedKey)
    {
        if (wrappedKey.Length < NonceSizeBytes + TagSizeBytes)
            throw new CryptographicException("Wrapped key blob is truncated.");
        var kek = RequireKek();
        var cipherLen = wrappedKey.Length - NonceSizeBytes - TagSizeBytes;
        var plaintext = new byte[cipherLen];
        using var aes = new AesGcm(kek, TagSizeBytes);
        aes.Decrypt(wrappedKey.AsSpan(0, NonceSizeBytes), wrappedKey.AsSpan(NonceSizeBytes, cipherLen), wrappedKey.AsSpan(NonceSizeBytes + cipherLen, TagSizeBytes), plaintext);
        return plaintext;
    }

    private byte[] RequireKek()
    {
        lock (_kekLock) return _kek ?? throw new InvalidOperationException("Keychain KEK is unavailable.");
    }

    private byte[]? LoadOrCreateKek()
    {
        lock (_kekLock)
        {
            var existing = QueryKek(out var queryStatus);
            if (queryStatus == SecStatusCode.Success)
            {
                if (existing?.Length == KekSizeBytes) return existing;
                _logger.LogError("Keychain KEK query returned {ActualBytes} bytes; expected {ExpectedBytes}. Refusing to replace the authoritative key.", existing?.Length ?? 0, KekSizeBytes);
                return null;
            }
            if (queryStatus != SecStatusCode.ItemNotFound) return FailPrimaryKek("query", queryStatus);

            var raw = new byte[KekSizeBytes];
            RandomNumberGenerator.Fill(raw);
            var status = StoreKek(raw);
            if (status == SecStatusCode.Success)
            {
                _logger.LogInformation("Created device-local Keychain KEK for envelope secret protection.");
                return raw;
            }

            // DuplicateItem: another process of this app created the KEK between query and add.
            // Never return the fresh `raw`; re-read the stored key that earlier ciphertext used.
            if (status == SecStatusCode.DuplicateItem)
            {
                var concurrent = QueryKek(out var duplicateQueryStatus);
                if (duplicateQueryStatus == SecStatusCode.Success && concurrent?.Length == KekSizeBytes) return concurrent;
                return FailPrimaryKek("re-query after duplicate add", duplicateQueryStatus);
            }
            return FailPrimaryKek("add", status);
        }
    }

    private byte[]? FailPrimaryKek(string operation, SecStatusCode status)
    {
        if (status == SecStatusCode.MissingEntitlement) _unavailableReason = OsKeyStoreUnavailableReason.MissingAppEntitlement;
        var remediation = status == SecStatusCode.MissingEntitlement
            ? "The signed app is missing an authorized application identifier/keychain access group; inspect codesign entitlements and the provisioning profile."
            : "The login keychain may be locked or unavailable; retry after signing in and unlocking the keychain.";
        _logger.LogWarning("Keychain KEK {Operation} failed with {Status} ({StatusCode}): {StatusDescription}. {Remediation}",
            operation, status, (long)status, status.GetStatusDescription(), remediation);
        return null;
    }

    private SecRecord BaseQuery()
    {
        var record = new SecRecord(SecKind.GenericPassword) { Service = _serviceName, Account = AccountName };
        // Without this flag AppKit searches the legacy login keychain and would
        // create a different KEK, orphaning the existing Catalyst vault.
        var library = ObjCRuntime.Dlfcn.dlopen("/System/Library/Frameworks/Security.framework/Security", 0);
        try
        {
            var key = ObjCRuntime.Dlfcn.GetStringConstant(library, "kSecUseDataProtectionKeychain")
                ?? throw new InvalidOperationException("The data-protection Keychain is unavailable.");
            record.ToDictionary()[key] = NSNumber.FromBoolean(true);
        }
        finally { ObjCRuntime.Dlfcn.dlclose(library); }
        return record;
    }

    private byte[]? QueryKek(out SecStatusCode status)
    {
        using var query = BaseQuery();
        using var record = SecKeyChain.QueryAsRecord(query, out status);
        return status == SecStatusCode.Success && record?.ValueData is NSData data ? data.ToArray() : null;
    }

    private SecStatusCode StoreKek(byte[] kek)
    {
        using var record = BaseQuery();
        record.ValueData = NSData.FromArray(kek);
        // Available after first unlock; never synced to iCloud Keychain so ciphertext stays device-local.
        record.Accessible = SecAccessible.AfterFirstUnlockThisDeviceOnly;
        return SecKeyChain.Add(record);
    }
}
#endif
