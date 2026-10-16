using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Text;
using SwarmCore.Agent.Continual;

namespace SwarmCore.Missions;

/// <summary>Strict text decoding and same-directory atomic replacement shared by local file tools.</summary>
public static class AtomicTextFile
{
    private static readonly SemaphoreSlim[] WriteGates = Enumerable.Range(0, 128).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    public sealed record Snapshot(byte[] Bytes, string Content, Encoding Encoding, int PreambleLength)
    {
        public string Sha256 => Convert.ToHexString(SHA256.HashData(Bytes));
        public byte[] Encode(string content) => [.. Bytes.AsSpan(0, PreambleLength).ToArray(), .. Encoding.GetBytes(content)];
    }

    public static Snapshot Decode(byte[] bytes, bool truncated = false)
    {
        Encoding encoding = new UTF8Encoding(false, true);
        var bom = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 })) { encoding = new UTF32Encoding(false, true, true); bom = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xFE, 0xFF })) { encoding = new UTF32Encoding(true, true, true); bom = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) { encoding = new UnicodeEncoding(false, true, true); bom = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) { encoding = new UnicodeEncoding(true, true, true); bom = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) bom = 3;

        var decoder = encoding.GetDecoder();
        var chars = new char[encoding.GetMaxCharCount(bytes.Length - bom)];
        decoder.Convert(bytes.AsSpan(bom), chars, !truncated, out _, out var count, out _);
        var text = new string(chars, 0, count);
        if (text.Contains('\0')) throw new InvalidDataException("Binary or BOM-less Unicode file: use a binary-aware tool; no text write was performed.");
        return new Snapshot(bytes, text, encoding, bom);
    }

    public static async Task<Snapshot?> ReadAsync(string path, CancellationToken ct)
        => File.Exists(path) ? Decode(await File.ReadAllBytesAsync(path, ct)) : null;

    /// <summary>
    /// Writes a complete temporary sibling, verifies the current byte digest, then replaces the target.
    /// The digest detects concurrent edits; filesystem replacement is atomic, but external rename races
    /// cannot be eliminated by portable filesystem APIs.
    /// </summary>
    public static async Task WriteAsync(string path, byte[] bytes, string expectedSha256, CancellationToken ct)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var hash = unchecked((uint)comparer.GetHashCode(Path.GetFullPath(path)));
        var gate = WriteGates[hash % (uint)WriteGates.Length];
        await gate.WaitAsync(ct);
        try { await WriteUnderGateAsync(path, bytes, expectedSha256, ct); }
        finally { gate.Release(); }
    }

    private static async Task WriteUnderGateAsync(string path, byte[] bytes, string expectedSha256, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                // Copy a restricted destination's DACL before any content reaches its temporary sibling.
                if (OperatingSystem.IsWindows() && expectedSha256 != "missing")
                    new FileInfo(temporary).SetAccessControl(new FileInfo(path).GetAccessControl(AccessControlSections.Access));
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await output.WriteAsync(bytes, ct);
                await output.FlushAsync(ct);
                output.Flush(flushToDisk: true);
            }
            if (expectedSha256.Equals("missing", StringComparison.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                try { File.Move(temporary, path, overwrite: false); }
                catch (IOException) when (File.Exists(path)) { throw Conflict(); }
            }
            else
            {
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        await using var current = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                        var actual = Convert.ToHexString(await SHA256.HashDataAsync(current, ct));
                        if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase)) throw Conflict();
                        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, File.GetUnixFileMode(path));
                        ct.ThrowIfCancellationRequested();
                        File.Replace(temporary, path, destinationBackupFileName: null);
                        break;
                    }
                    catch (FileNotFoundException) { throw Conflict(); }
                    catch (IOException error) when (OperatingSystem.IsWindows() && attempt < 6 && File.Exists(temporary)
                        && ((error.HResult & 0xffff) is 32 or 33 or 1175))
                    {
                        // Windows can briefly refuse removal during replacement. Never retry a
                        // stale write: reopen and compare the original digest on every attempt.
                        await Task.Delay(TimeSpan.FromMilliseconds(10 << attempt), ct);
                    }
                }
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static MissionActionSkippedException Conflict() => new("write-base-conflict: the target changed since it was read; inspect and integrate the current changes.");
}
