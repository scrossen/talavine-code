using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace SwarmCore.Coding.Native;

/// <summary>
/// Endpoints and framing for a current-user-only local RPC between a host and the MCP bridge process a native coding agent
/// starts (the same shape the host computer broker uses). On Unix .NET maps a named pipe to <c>$TMPDIR/CoreFxPipe_&lt;name&gt;</c>,
/// which can overflow the socket-path limit, so a too-long default falls back to a short socket inside a private (0700)
/// directory. Each host service uses its own name prefix, and <see cref="IsValid"/> only accepts endpoints of that shape.
/// </summary>
public static class LocalPipeEndpoint
{
    private const string DirectoryPrefix = "swp-";
    private const string SocketFileName = "s";
    private const string RuntimePipePrefix = "CoreFxPipe_";
    private const int MaxEndpointLength = 4096;

    /// <summary>A fresh endpoint and, when a private directory was created, the directory to remove on disposal.</summary>
    public static (string Endpoint, string? PrivateDirectory) Create(string namePrefix)
    {
        ValidatePrefix(namePrefix);
        var name = namePrefix + Guid.NewGuid().ToString("N");
        if (OperatingSystem.IsWindows() || Fits(Path.Combine(Path.GetTempPath(), RuntimePipePrefix + name)))
            return (name, null);
        foreach (var root in new[] { Path.GetTempPath(), "/tmp" })
        {
            var directory = Path.Combine(root, DirectoryPrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant());
            var socket = Path.Combine(directory, SocketFileName);
            if (!Fits(socket)) continue;
            try { Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            return (socket, directory);
        }
        throw new IOException("No local directory is short enough for a private host socket.");
    }

    /// <summary>Accepts a generated name with <paramref name="namePrefix"/>, or a socket inside a private directory this class creates.</summary>
    public static bool IsValid(string? endpoint, string namePrefix)
    {
        if (string.IsNullOrEmpty(endpoint) || endpoint.Length > MaxEndpointLength) return false;
        if (!Path.IsPathRooted(endpoint))
            return endpoint.StartsWith(namePrefix, StringComparison.Ordinal) && endpoint.Length <= 128 && !endpoint.Contains('/') && !endpoint.Contains('\\');
        if (OperatingSystem.IsWindows() || endpoint.Contains("..", StringComparison.Ordinal)) return false;
        if (!string.Equals(Path.GetFileName(endpoint), SocketFileName, StringComparison.Ordinal)) return false;
        var directory = Path.GetDirectoryName(endpoint);
        if (directory is null || !Path.GetFileName(directory).StartsWith(DirectoryPrefix, StringComparison.Ordinal)) return false;
        try
        {
            var info = new DirectoryInfo(directory);
            return info.Exists && info.LinkTarget is null
                && File.GetUnixFileMode(directory) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Reads one length-prefixed frame (little-endian int32 length, then the bytes).</summary>
    public static async Task<byte[]> ReadFrameAsync(Stream stream, int maxLength, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > maxLength) throw new InvalidDataException("Local RPC frame exceeds its size bound.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        return bytes;
    }

    /// <summary>Writes one length-prefixed frame.</summary>
    public static async Task WriteFrameAsync(Stream stream, byte[] bytes, int maxLength, CancellationToken ct)
    {
        if (bytes.Length <= 0 || bytes.Length > maxLength) throw new InvalidDataException("Local RPC frame exceeds its size bound.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static void ValidatePrefix(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix) || prefix.Length > 48 || !prefix.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new ArgumentException("Endpoint prefixes are short plain identifiers.", nameof(prefix));
    }

    private static bool Fits(string path)
    {
        try { _ = new UnixDomainSocketEndPoint(path); return true; }
        catch (ArgumentOutOfRangeException) { return false; }
    }
}
