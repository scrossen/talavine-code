using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace SwarmCore.Secrets;

/// <summary>
/// Windows <see cref="IOsKeyStore"/> backed by DPAPI (<see cref="ProtectedData"/>,
/// CurrentUser scope). Wraps the envelope data key so the wrapped blob is only
/// unwrappable by the current Windows user on this machine; there is no bare key
/// file on disk. Every same-machine, same-user process of one product wraps under
/// the same DPAPI key and the same application-scope entropy, so a console host and
/// a desktop app sharing a data directory read each other's "v1:" secrets.
///
/// Availability is probed once at construction; if DPAPI throws (for example a
/// broken user profile) the store reports unavailable and the protector falls
/// back to the legacy plaintext path.
///
/// A product that renamed its scope keeps the old one as a read-only fallback
/// (<c>legacyReadScopes</c>): new keys are always wrapped under the current scope,
/// while keys wrapped under a shipped earlier scope still unwrap. Every attempt keeps
/// configured protection scope (CurrentUser by default), so foreign-user, foreign-device or corrupt blobs still fail.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDpapiKeyStore : IOsKeyStore
{
    private readonly byte[] _entropy;
    private readonly byte[][] _legacyEntropies;
    private readonly DataProtectionScope _scope;
    private readonly bool _available;

    /// <param name="logger">Probe diagnostics.</param>
    /// <param name="applicationScope">
    /// Stable product label mixed into DPAPI as additional entropy so a stray
    /// CurrentUser-scope DPAPI consumer cannot unwrap this product's blobs. It is a
    /// persisted protocol identifier: changing it strands existing secrets.
    /// </param>
    public WindowsDpapiKeyStore(ILogger<WindowsDpapiKeyStore> logger, string applicationScope)
        : this((ILogger)logger, applicationScope, legacyReadScopes: null)
    {
    }

    /// <param name="logger">Probe diagnostics.</param>
    /// <param name="applicationScope">Current scope; every new wrap uses it (see the other constructor).</param>
    /// <param name="legacyReadScopes">Earlier shipped scopes tried, in order, only when unwrapping under the current scope fails.</param>
    public WindowsDpapiKeyStore(ILogger logger, string applicationScope, IEnumerable<string>? legacyReadScopes)
        : this(logger, applicationScope, legacyReadScopes, DataProtectionScope.CurrentUser)
    {
    }

    /// <param name="logger">Probe diagnostics.</param>
    /// <param name="applicationScope">Current scope; every new wrap uses it.</param>
    /// <param name="legacyReadScopes">Earlier shipped scopes tried only when unwrapping under the current scope fails.</param>
    /// <param name="protectionScope">
    /// <see cref="DataProtectionScope.CurrentUser"/> for interactive hosts. A host that must
    /// also read its keys from a password-less logon (an S4U boot-time task cannot open the
    /// user's DPAPI master key) uses <see cref="DataProtectionScope.LocalMachine"/>: keys
    /// stay bound to this computer and the store's file permissions still gate who reads them.
    /// </param>
    public WindowsDpapiKeyStore(ILogger logger, string applicationScope, IEnumerable<string>? legacyReadScopes, DataProtectionScope protectionScope)
    {
        _scope = protectionScope;
        if (string.IsNullOrWhiteSpace(applicationScope))
            throw new ArgumentException("An application scope is required.", nameof(applicationScope));
        _entropy = EntropyFor(applicationScope);
        _legacyEntropies = (legacyReadScopes ?? [])
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Select(EntropyFor)
            .Where(entropy => !entropy.AsSpan().SequenceEqual(_entropy))
            .ToArray();
        _available = ProbeAvailability(logger);
    }

    /// <summary>The DPAPI entropy bytes for a scope: UTF-8 of <c>"{scope}.SecretProtector.v1"</c>.</summary>
    public static byte[] EntropyFor(string applicationScope)
        => Encoding.UTF8.GetBytes(applicationScope.Trim() + ".SecretProtector.v1");

    public bool IsAvailable => _available;

    public string ProviderId => "dpapi";

    public byte[] WrapKey(byte[] plaintextKey)
        => ProtectedData.Protect(plaintextKey, _entropy, _scope);

    public byte[] UnwrapKey(byte[] wrappedKey)
    {
        try
        {
            return ProtectedData.Unprotect(wrappedKey, _entropy, _scope);
        }
        catch (CryptographicException) when (_legacyEntropies.Length > 0)
        {
            for (var i = 0; i < _legacyEntropies.Length; i++)
            {
                try { return ProtectedData.Unprotect(wrappedKey, _legacyEntropies[i], _scope); }
                catch (CryptographicException) when (i < _legacyEntropies.Length - 1) { }
            }
            throw; // unreachable: the last legacy attempt rethrows its own failure
        }
    }

    private bool ProbeAvailability(ILogger logger)
    {
        try
        {
            var probe = new byte[16];
            RandomNumberGenerator.Fill(probe);
            var wrapped = ProtectedData.Protect(probe, _entropy, _scope);
            var roundTripped = ProtectedData.Unprotect(wrapped, _entropy, _scope);
            var ok = CryptographicOperations.FixedTimeEquals(probe, roundTripped);
            if (!ok)
                logger.LogWarning("DPAPI round-trip probe mismatch; secret protection will fall back to the legacy path.");
            return ok;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "DPAPI unavailable on this host; secret protection will fall back to the legacy path.");
            return false;
        }
    }
}
