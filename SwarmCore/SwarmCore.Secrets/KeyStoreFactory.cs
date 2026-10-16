using Microsoft.Extensions.Logging;

namespace SwarmCore.Secrets;

/// <summary>
/// Picks the <see cref="IOsKeyStore"/> for the running host: DPAPI on Windows,
/// otherwise the no-op store (legacy plaintext path). A Mac Catalyst app head
/// registers its Keychain store instead, a target a plain class library cannot
/// compile against.
/// </summary>
public static class KeyStoreFactory
{
    public static IOsKeyStore Create(ILoggerFactory loggerFactory, string applicationScope)
        => Create(loggerFactory, applicationScope, legacyReadScopes: null);

    /// <summary>As <see cref="Create(ILoggerFactory, string)"/>, also unwrapping keys wrapped under earlier shipped scopes.</summary>
    public static IOsKeyStore Create(ILoggerFactory loggerFactory, string applicationScope, IEnumerable<string>? legacyReadScopes)
    {
        if (OperatingSystem.IsWindows())
            return new WindowsDpapiKeyStore(loggerFactory.CreateLogger<WindowsDpapiKeyStore>(), applicationScope, legacyReadScopes);
        return new NoOpKeyStore();
    }
}
