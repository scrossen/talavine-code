using System.Text;
using SwarmCore.Services.Abstractions;

namespace SwarmCore.Services.Scripting;

/// <summary>
/// Observable, cancellation-aware filesystem surface for authored C#.
/// Binary data stays inside the host process, so a service can transform or
/// upload an artifact without copying base64 through an LLM/tool transcript.
/// </summary>
public sealed class ScriptFiles
{
    private readonly IServiceFileWorkspace? _workspace;
    private readonly CancellationToken _cancellation;
    private readonly Action<string>? _activity;
    private readonly long? _maxWorkspaceBytes;

    public ScriptFiles(
        IServiceFileWorkspace? workspace,
        CancellationToken cancellation,
        Action<string>? activity = null,
        long? maxWorkspaceBytes = null)
    {
        _workspace = workspace;
        _cancellation = cancellation;
        _activity = activity;
        _maxWorkspaceBytes = maxWorkspaceBytes is > 0 ? maxWorkspaceBytes : null;
    }

    /// <summary>
    /// Refuse a write that would push the workspace past its quota. Null quota (the
    /// default, and what Talavine's authored services use) means unbounded. The
    /// check is a directory walk, so it only runs when a host actually set a cap.
    /// </summary>
    private void EnsureQuota(string resolvedPath, long incomingBytes)
    {
        if (_maxWorkspaceBytes is not { } cap) return;
        var root = RequireWorkspace().Root;
        if (!Directory.Exists(root)) return;

        long used = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                var info = new FileInfo(file);
                // Replacing a file frees its current bytes.
                if (!string.Equals(info.FullName, resolvedPath, StringComparison.OrdinalIgnoreCase))
                    used += info.Length;
            }
            catch (FileNotFoundException) { /* raced with a delete; ignore */ }
        }

        if (used + incomingBytes > cap)
        {
            throw new InvalidOperationException(
                $"This app's file workspace is full ({used + incomingBytes:N0} bytes would exceed the {cap:N0}-byte limit). "
                + "Delete files you no longer need with Files.Delete, or keep less data on disk.");
        }
    }

    public string Root => RequireWorkspace().Root;

    public bool Exists(string path)
    {
        _cancellation.ThrowIfCancellationRequested();
        var resolved = Resolve(path);
        var exists = File.Exists(resolved) || Directory.Exists(resolved);
        Observe($"checked {Display(path)}");
        return exists;
    }

    public long GetLength(string path)
    {
        _cancellation.ThrowIfCancellationRequested();
        var resolved = Resolve(path);
        var length = new FileInfo(resolved).Length;
        Observe($"inspected {Display(path)} ({length} bytes)");
        return length;
    }

    public async Task<byte[]> ReadAllBytesAsync(string path)
    {
        var resolved = Resolve(path);
        var bytes = await File.ReadAllBytesAsync(resolved, _cancellation).ConfigureAwait(false);
        Observe($"read {Display(path)} ({bytes.Length} bytes)");
        return bytes;
    }

    public async Task<string> ReadAllTextAsync(string path)
    {
        var resolved = Resolve(path);
        var text = await File.ReadAllTextAsync(resolved, Encoding.UTF8, _cancellation).ConfigureAwait(false);
        Observe($"read {Display(path)} ({text.Length} chars)");
        return text;
    }

    public async Task WriteAllBytesAsync(string path, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var resolved = Resolve(path);
        EnsureQuota(resolved, bytes.Length);
        EnsureParentDirectory(resolved);
        await File.WriteAllBytesAsync(resolved, bytes, _cancellation).ConfigureAwait(false);
        Observe($"wrote {Display(path)} ({bytes.Length} bytes)");
    }

    public async Task WriteAllTextAsync(string path, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var resolved = Resolve(path);
        EnsureQuota(resolved, System.Text.Encoding.UTF8.GetByteCount(text));
        EnsureParentDirectory(resolved);
        // BOM-less UTF-8: written text is routinely re-read as BYTES (attachment
        // saves, uploads), where Encoding.UTF8's preamble would leak a stray
        // U+FEFF into downstream consumers. Reads still tolerate a BOM.
        await File.WriteAllTextAsync(resolved, text, Utf8NoBom, _cancellation).ConfigureAwait(false);
        Observe($"wrote {Display(path)} ({text.Length} chars)");
    }

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public string[] ListFiles(string path = ".", string pattern = "*", bool recursive = false)
    {
        _cancellation.ThrowIfCancellationRequested();
        var resolved = Resolve(path);
        var files = Directory.GetFiles(
            resolved,
            string.IsNullOrWhiteSpace(pattern) ? "*" : pattern,
            recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
        Observe($"listed {Display(path)} ({files.Length} files)");
        return files;
    }

    public void CreateDirectory(string path)
    {
        _cancellation.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Resolve(path));
        Observe($"created directory {Display(path)}");
    }

    public void Copy(string sourcePath, string destinationPath, bool overwrite = false)
    {
        _cancellation.ThrowIfCancellationRequested();
        var destination = Resolve(destinationPath);
        EnsureQuota(destination, new FileInfo(Resolve(sourcePath)).Length);
        EnsureParentDirectory(destination);
        File.Copy(Resolve(sourcePath), destination, overwrite);
        Observe($"copied {Display(sourcePath)} to {Display(destinationPath)}");
    }

    public void Move(string sourcePath, string destinationPath, bool overwrite = false)
    {
        _cancellation.ThrowIfCancellationRequested();
        var destination = Resolve(destinationPath);
        EnsureParentDirectory(destination);
        File.Move(Resolve(sourcePath), destination, overwrite);
        Observe($"moved {Display(sourcePath)} to {Display(destinationPath)}");
    }

    public void Delete(string path)
    {
        _cancellation.ThrowIfCancellationRequested();
        var resolved = Resolve(path);
        if (File.Exists(resolved))
        {
            File.Delete(resolved);
        }
        else if (Directory.Exists(resolved))
        {
            Directory.Delete(resolved, recursive: false);
        }
        Observe($"deleted {Display(path)}");
    }

    private IServiceFileWorkspace RequireWorkspace()
        => _workspace ?? throw new InvalidOperationException(
            "This host did not configure an authored-service file workspace.");

    private string Resolve(string path)
    {
        _cancellation.ThrowIfCancellationRequested();
        return RequireWorkspace().Resolve(path);
    }

    private static void EnsureParentDirectory(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }
    }

    private void Observe(string message) => _activity?.Invoke(message);

    private static string Display(string path)
    {
        var trimmed = path?.Trim() ?? "";
        return trimmed.Length <= 160 ? trimmed : trimmed[..160];
    }
}
