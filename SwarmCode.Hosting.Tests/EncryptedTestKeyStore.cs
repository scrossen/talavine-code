using System.Security.Cryptography;
using SwarmCore.Secrets;

namespace SwarmCode.Hosting.Tests;

// Keeps host composition fixtures encrypted on every platform without using a personal keystore.
internal sealed class EncryptedTestKeyStore : IOsKeyStore
{
    private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
    public bool IsAvailable => true;
    public string ProviderId => "encrypted-test";
    public byte[] WrapKey(byte[] plaintextKey)
    {
        var nonce = RandomNumberGenerator.GetBytes(12); var cipher = new byte[plaintextKey.Length]; var tag = new byte[16];
        using var aes = new AesGcm(key, 16); aes.Encrypt(nonce, plaintextKey, cipher, tag);
        return [.. nonce, .. cipher, .. tag];
    }
    public byte[] UnwrapKey(byte[] wrappedKey)
    {
        var size = wrappedKey.Length - 28; var plaintext = new byte[size];
        using var aes = new AesGcm(key, 16); aes.Decrypt(wrappedKey.AsSpan(0, 12), wrappedKey.AsSpan(12, size), wrappedKey.AsSpan(12 + size, 16), plaintext);
        return plaintext;
    }
}
