using System.Security.Cryptography;
using System.Text.Json;

namespace SwarmCore.Services.DataTransfer;

/// <summary>Transport artifacts contain recipient-encrypted bytes, including account credentials.</summary>
public static class DataTransferEncryption
{
    public const int MaximumBytes = 8 * 1024 * 1024;
    private sealed record Envelope(int Version, byte[] Key, byte[] Nonce, byte[] Tag, byte[] Ciphertext);

    public static byte[] Seal(DataTransferRequest request, string recipientPublicKey)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(request);
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            if (plaintext.Length > MaximumBytes) throw new InvalidDataException("Selection is too large; transfer a smaller selection.");
            using var rsa = RSA.Create();
            rsa.ImportFromPem(recipientPublicKey);
            if (rsa.KeySize < 2048) throw new CryptographicException("Recipient key is too small.");
            var nonce = RandomNumberGenerator.GetBytes(12);
            var tag = new byte[16];
            var cipher = new byte[plaintext.Length];
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, plaintext, cipher, tag);
            return JsonSerializer.SerializeToUtf8Bytes(new Envelope(1,
                rsa.Encrypt(key, RSAEncryptionPadding.OaepSHA256), nonce, tag, cipher));
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plaintext); }
    }

    public static DataTransferRequest Open(byte[] bytes, RSA recipientKey)
    {
        if (bytes.Length > MaximumBytes * 2) throw new InvalidDataException("Transfer exceeds the size limit.");
        var envelope = JsonSerializer.Deserialize<Envelope>(bytes) ?? throw new InvalidDataException("Invalid transfer.");
        if (envelope.Version != 1 || envelope.Ciphertext.Length > MaximumBytes)
            throw new InvalidDataException("Unsupported transfer.");
        var key = recipientKey.Decrypt(envelope.Key, RSAEncryptionPadding.OaepSHA256);
        var plaintext = new byte[envelope.Ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(envelope.Nonce, envelope.Ciphertext, envelope.Tag, plaintext);
            return JsonSerializer.Deserialize<DataTransferRequest>(plaintext) ?? throw new InvalidDataException("Invalid transfer.");
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plaintext); }
    }
}
