using System.Security.Cryptography;

namespace SwarmCore.Abstractions.Storage;

/// <summary>Immutable, atomic writes. Re-ingesting identical bytes reuses the same file on every OS.</summary>
public static class ContentAddressedAttachmentFile
{
    // A scanner, an indexer or a racing writer can hold a file briefly. Only those sharing
    // violations are retried, and boundedly, so a persistent holder still surfaces as the error.
    private const int SharingRetries = 9;

    public static async Task<(string Path, string Hash, long Size)> WriteAsync(
        string directory, string extension, Func<Stream, Task> write, CancellationToken ct, string? expectedHash = null)
    {
        Directory.CreateDirectory(directory);
        if (extension.Length > 16 || extension.Any(c => c != '.' && !char.IsAsciiLetterOrDigit(c))) extension = ".bin";
        var temporary = Path.Combine(directory, $".attachment-{Guid.NewGuid():N}.tmp");
        try
        {
            string hash;
            long size;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous))
            {
                using var sha = SHA256.Create();
                await using var hashing = new CryptoStream(output, sha, CryptoStreamMode.Write, leaveOpen: true);
                await write(hashing);
                await hashing.FlushFinalBlockAsync(ct);
                await output.FlushAsync(ct);
                output.Flush(flushToDisk: true);
                hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
                size = output.Length;
            }
            ct.ThrowIfCancellationRequested();
            if (expectedHash is not null && !string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Downloaded attachment did not match its original fingerprint.");
            var destination = Path.Combine(directory, hash + extension.ToLowerInvariant());
            for (var attempt = 0; !await TryPublishAsync(temporary, destination, hash, size, attempt < SharingRetries, ct); attempt++)
                await Task.Delay(RetryDelay(attempt), ct);
            return (destination, hash, size);
        }
        finally { await DeleteTemporaryAsync(temporary); }
    }

    /// <summary>True once the destination holds these bytes; false asks for a retry after a transient conflict.</summary>
    private static async Task<bool> TryPublishAsync(
        string temporary, string destination, string hash, long size, bool canRetry, CancellationToken ct)
    {
        try
        {
            // Never replaces a file: the first complete copy is published and every other writer verifies it.
            File.Move(temporary, destination);
            return true;
        }
        catch (IOException) when (File.Exists(destination)) { }
        catch (IOException error) when (canRetry && IsSharingViolation(error)) { return false; }
        try
        {
            // Never trust a filename as proof of bytes, or overwrite a conflicting file. Sharing delete admits
            // the handle a winning writer's rename keeps on the file it just published until its move returns
            // on Windows. Write access still conflicts, so the bytes cannot change while they are hashed.
            await using var existing = new FileStream(destination, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous);
            if (existing.Length == size
                && Convert.ToHexString(await SHA256.HashDataAsync(existing, ct)).ToLowerInvariant() == hash)
                return true;
        }
        // A file removed since the move failed is published by the retry.
        catch (IOException error) when (canRetry && (error is FileNotFoundException || IsSharingViolation(error)))
        {
            return false;
        }
        throw new IOException("Attachment content path contains different bytes.");
    }

    // Best effort: a scanner still holding the temporary file must not replace a verified publish, or the
    // failure being reported, with a different error. A leftover temporary name is never content-addressed.
    private static async Task DeleteTemporaryAsync(string temporary)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { File.Delete(temporary); return; }
            catch (IOException error) when (attempt < SharingRetries && IsSharingViolation(error)) { }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
            await Task.Delay(RetryDelay(attempt));
        }
    }

    private static TimeSpan RetryDelay(int attempt) => TimeSpan.FromMilliseconds(Math.Min(10 << attempt, 500));

    private static bool IsSharingViolation(IOException error) => TransientFileLock.IsSharingViolation(error);
}
