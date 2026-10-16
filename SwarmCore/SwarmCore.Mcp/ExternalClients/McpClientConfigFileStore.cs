using System.Globalization;
using System.Text;

namespace SwarmCore.Mcp.ExternalClients;

/// <summary>A consistent read of a client configuration file.</summary>
/// <param name="Path">The file that is actually read and written (a symbolic link's final target).</param>
/// <param name="Exists">False when the file does not exist yet.</param>
/// <param name="Bytes">The exact bytes read, used for backups and change detection.</param>
/// <param name="Content">The decoded text without a byte-order mark; null when the file does not exist.</param>
/// <param name="Problem">Why the file cannot be used; when set, nothing may be written.</param>
public sealed record McpClientConfigSnapshot(
    string Path,
    bool Exists,
    byte[] Bytes,
    string? Content,
    bool HasUtf8Bom,
    string NewLine,
    string? Problem);

public enum McpClientConfigWriteOutcome
{
    Changed,
    Unchanged,
    Refused,
    Failed,
}

public sealed record McpClientConfigWriteResult(
    McpClientConfigWriteOutcome Outcome,
    string ConfigPath,
    string? BackupPath = null,
    string? Problem = null);

/// <summary>
/// Reads and rewrites another application's configuration file with the care that file
/// deserves: a timestamped backup before any change to an existing file, a same-directory
/// temporary file swapped in atomically, the original permissions kept, symbolic links
/// followed rather than replaced, and a re-check that nobody else changed the file while
/// the new text was being prepared. A file that cannot be read cleanly is never written.
/// </summary>
public sealed class McpClientConfigFileStore
{
    public const long MaximumFileBytes = 8L * 1024 * 1024;
    private const int MaximumAttempts = 3;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    private readonly string _backupLabel;
    private readonly TimeProvider _time;

    /// <param name="backupLabel">
    /// Names backups <c>&lt;file&gt;.&lt;label&gt;-backup-&lt;timestamp&gt;.bak</c> beside the file, so
    /// users can tell which app made them. Letters, digits, '-' and '_' only.
    /// </param>
    public McpClientConfigFileStore(string backupLabel = "mcp", TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupLabel);
        if (!backupLabel.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new ArgumentException("Use only letters, digits, '-' and '_'.", nameof(backupLabel));
        _backupLabel = backupLabel;
        _time = timeProvider ?? TimeProvider.System;
    }

    public McpClientConfigSnapshot Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string effective;
        try
        {
            effective = ResolveEffectivePath(System.IO.Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Unreadable(path, $"The settings file could not be opened: {ex.Message}");
        }

        try
        {
            if (Directory.Exists(effective))
                return Unreadable(effective, "The settings path is a folder, not a file.");
            if (!File.Exists(effective))
                return new McpClientConfigSnapshot(effective, false, [], null, false, "\n", null);

            byte[] bytes;
            using (var stream = new FileStream(effective, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length > MaximumFileBytes)
                    return Unreadable(effective, "The settings file is unexpectedly large, so it was left unchanged.");
                bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
            }

            if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
                return Unreadable(effective, "The settings file uses a text encoding other than UTF-8, so it was left unchanged.", bytes);

            var hasBom = bytes.AsSpan().StartsWith(Utf8Bom);
            string content;
            try
            {
                content = StrictUtf8.GetString(hasBom ? bytes.AsSpan(Utf8Bom.Length) : bytes);
            }
            catch (DecoderFallbackException)
            {
                return Unreadable(effective, "The settings file is not valid UTF-8 text, so it was left unchanged.", bytes);
            }

            return new McpClientConfigSnapshot(effective, true, bytes, content, hasBom, DetectNewLine(content), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unreadable(effective, $"The settings file could not be read: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads the file, asks <paramref name="edit"/> for the new text, and writes it back only
    /// when the edit says <see cref="McpClientConfigEditOutcome.Changed"/>.
    /// </summary>
    public McpClientConfigWriteResult Update(string path, Func<McpClientConfigSnapshot, McpClientConfigEdit> edit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(edit);

        var lastPath = path;
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            var snapshot = Read(path);
            lastPath = snapshot.Path;
            if (snapshot.Problem is not null)
                return new McpClientConfigWriteResult(McpClientConfigWriteOutcome.Refused, snapshot.Path, Problem: snapshot.Problem);

            var result = edit(snapshot);
            switch (result.Outcome)
            {
                case McpClientConfigEditOutcome.Unchanged:
                    return new McpClientConfigWriteResult(McpClientConfigWriteOutcome.Unchanged, snapshot.Path);
                case McpClientConfigEditOutcome.Refused:
                    return new McpClientConfigWriteResult(McpClientConfigWriteOutcome.Refused, snapshot.Path, Problem: result.Problem);
            }

            var bytes = Encode(result.Content ?? string.Empty, snapshot.HasUtf8Bom);
            var write = TryWrite(snapshot, bytes);
            if (write is not null)
                return write;
        }

        return new McpClientConfigWriteResult(
            McpClientConfigWriteOutcome.Failed,
            lastPath,
            Problem: "The settings file kept changing while it was being saved. Close the app that uses it and try again.");
    }

    /// <returns>A final result, or null when the file changed underneath and the update should be retried.</returns>
    private McpClientConfigWriteResult? TryWrite(McpClientConfigSnapshot snapshot, byte[] bytes)
    {
        var target = snapshot.Path;
        var directory = System.IO.Path.GetDirectoryName(target)!;
        var tempPath = System.IO.Path.Combine(
            directory,
            $".{System.IO.Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        string? backupPath = null;
        var replaced = false;
        try
        {
            Directory.CreateDirectory(directory);
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (!snapshot.Exists)
            {
                if (File.Exists(target))
                    return null;
                try
                {
                    File.Move(tempPath, target, overwrite: false);
                }
                catch (IOException) when (File.Exists(target))
                {
                    return null;
                }

                replaced = true;
                return new McpClientConfigWriteResult(McpClientConfigWriteOutcome.Changed, target);
            }

            CopyUnixMode(target, tempPath);
            if (!ReadCurrentBytes(target).AsSpan().SequenceEqual(snapshot.Bytes))
                return null;

            backupPath = WriteBackup(target, snapshot.Bytes);
            ReplaceWithRetry(tempPath, target);
            replaced = true;
            return new McpClientConfigWriteResult(McpClientConfigWriteOutcome.Changed, target, backupPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new McpClientConfigWriteResult(
                McpClientConfigWriteOutcome.Failed,
                target,
                Problem: $"The settings file could not be saved: {ex.Message}");
        }
        finally
        {
            TryDelete(tempPath);
            if (!replaced && backupPath is not null)
                TryDelete(backupPath);
        }
    }

    private string WriteBackup(string target, byte[] originalBytes)
    {
        var stamp = _time.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        for (var suffix = 1; ; suffix++)
        {
            var name = suffix == 1
                ? $"{System.IO.Path.GetFileName(target)}.{_backupLabel}-backup-{stamp}.bak"
                : $"{System.IO.Path.GetFileName(target)}.{_backupLabel}-backup-{stamp}-{suffix}.bak";
            var backup = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(target)!, name);
            try
            {
                using (var stream = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(originalBytes);
                    stream.Flush(flushToDisk: true);
                }

                CopyUnixMode(target, backup);
                return backup;
            }
            catch (IOException) when (File.Exists(backup) && suffix < 100)
            {
                // Another backup was made this second; pick the next name.
            }
        }
    }

    private static void ReplaceWithRetry(string tempPath, string target)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    // Keeps the original file's ACLs and attributes.
                    File.Replace(tempPath, target, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(tempPath, target, overwrite: true);
                }

                return;
            }
            catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
            {
                // Another app may be reading the file this instant.
                Thread.Sleep(TimeSpan.FromMilliseconds(40 * attempt));
            }
        }
    }

    private static byte[] ReadCurrentBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaximumFileBytes)
            return [];
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void CopyUnixMode(string source, string destination)
    {
        if (OperatingSystem.IsWindows())
            return;
        try
        {
            File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Best effort; the default mode is still owner-writable.
        }
    }

    private static string ResolveEffectivePath(string fullPath)
    {
        var info = new FileInfo(fullPath);
        if (info.LinkTarget is null)
            return fullPath;

        // Edit the file a dotfiles link points at instead of replacing the link itself.
        var finalTarget = info.ResolveLinkTarget(returnFinalTarget: true)
            ?? throw new IOException("The settings file is a link that could not be followed.");
        if (!finalTarget.Exists)
            throw new IOException("The settings file is a link to a file that does not exist.");
        return finalTarget.FullName;
    }

    private static byte[] Encode(string content, bool withBom)
    {
        var body = StrictUtf8.GetBytes(content);
        return withBom ? [.. Utf8Bom, .. body] : body;
    }

    private static string DetectNewLine(string content)
    {
        var index = content.IndexOf('\n');
        return index > 0 && content[index - 1] == '\r' ? "\r\n" : "\n";
    }

    private static McpClientConfigSnapshot Unreadable(string path, string problem, byte[]? bytes = null) =>
        new(path, true, bytes ?? [], null, false, "\n", problem);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort cleanup of a temporary or redundant backup file.
        }
    }
}
