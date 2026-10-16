using System.Buffers.Binary;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace SwarmCore.Secrets;

/// <summary>Private key-wrap protocol. The KEK and credential values never cross this connection.</summary>
public static class KeyStoreBrokerProtocol
{
    public const string EndpointFileName = "swarmcore-keystore.endpoint";
    internal const byte Wrap = 1, Unwrap = 2, Ok = 0, Error = 1;
    internal const int MaxPayload = 16 * 1024;
    internal static async Task WriteAsync(Stream stream, byte kind, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (payload.Length > MaxPayload) throw new InvalidDataException("Invalid key-wrap frame length.");
        var header = new byte[5]; header[0] = kind;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1), payload.Length);
        await stream.WriteAsync(header, ct); await stream.WriteAsync(payload, ct); await stream.FlushAsync(ct);
    }
    internal static async Task<(byte Kind, byte[] Payload)?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[5];
        var count = await stream.ReadAtLeastAsync(header, 5, false, ct);
        if (count == 0) return null;
        if (count != 5) throw new InvalidDataException("Truncated key-wrap frame.");
        var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
        if (length is < 0 or > MaxPayload) throw new InvalidDataException("Invalid key-wrap frame length.");
        var payload = new byte[length]; await stream.ReadExactlyAsync(payload, ct);
        return (header[0], payload);
    }

    /// <summary>Darwin authenticates the socket's same-user peer PID; the observed executable must match an owned path.</summary>
    public static bool IsPeerExecutable(Socket socket, string expectedExecutable, int? expectedPid = null)
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsMacCatalyst()) return false;
        try
        {
            if (getpeereid((int)socket.Handle, out var uid, out _) != 0 || uid != geteuid()) return false;
            uint size = 4;
            // Darwin sys/un.h: SOL_LOCAL=0 and LOCAL_PEERPID=0x002.
            if (getsockopt((int)socket.Handle, 0, 2, out var pid, ref size) != 0 || size != 4 || pid <= 0
                || expectedPid is { } required && pid != required) return false;
            var bytes = new byte[4096];
            var length = proc_pidpath(pid, bytes, (uint)bytes.Length);
            if (length <= 0) return false;
            var end = Array.IndexOf(bytes, (byte)0); if (end < 0) end = Math.Min(length, bytes.Length);
            var actual = Encoding.UTF8.GetString(bytes, 0, end);
            return string.Equals(PhysicalPath(actual), PhysicalPath(expectedExecutable), StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or IOException or UnauthorizedAccessException)
        { return false; }
    }
    private static string PhysicalPath(string path)
    {
        var resolved = realpath(path, IntPtr.Zero);
        if (resolved == IntPtr.Zero) throw new IOException("The peer executable cannot be resolved.");
        try { return Marshal.PtrToStringUTF8(resolved)!; }
        finally { free(resolved); }
    }
    [DllImport("libc")] private static extern int getpeereid(int fd, out uint uid, out uint gid);
    [DllImport("libc")] private static extern uint geteuid();
    [DllImport("libc")] private static extern int getsockopt(int fd, int level, int option, out int value, ref uint length);
    [DllImport("/usr/lib/libproc.dylib")] private static extern int proc_pidpath(int pid, byte[] buffer, uint size);
    [DllImport("libc")] private static extern IntPtr realpath(string path, IntPtr buffer);
    [DllImport("libc")] private static extern void free(IntPtr buffer);
}
