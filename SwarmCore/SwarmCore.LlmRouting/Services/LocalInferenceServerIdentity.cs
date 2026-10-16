using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace SwarmCore.LlmRouting.Services;

/// <summary>Operator-observed local listener, not a claim made by the HTTP server.</summary>
public sealed record LocalInferenceServerIdentity(int ProcessId, DateTimeOffset ProcessStartedAtUtc,
    string ExecutablePath, string ExecutableSha256, string ListenerAddress, int ListenerPort);

/// <summary>
/// Binds reviewed inference to the current OS listener and executable. A different
/// platform needs its own OS-backed implementation; API metadata is not a fallback.
/// This does not attest process memory or cryptographically authenticate model weights.
/// </summary>
public static class LocalInferenceServerIdentityValidator
{
    public static void Validate(LocalInferenceServerIdentity identity, string endpoint)
    {
        var uri = DirectTextHttpAdmissionValidator.ReviewedEndpoint(endpoint);
        if (identity.ProcessId <= 0 || identity.ListenerPort != uri.Port
            || !IPAddress.TryParse(identity.ListenerAddress, out var listenerAddress)
            || !Path.IsPathFullyQualified(identity.ExecutablePath)
            || string.IsNullOrEmpty(identity.ExecutableSha256) || identity.ExecutableSha256.Length != 64
            || identity.ExecutableSha256.Any(c => !Uri.IsHexDigit(c))
            || identity.ProcessStartedAtUtc.Offset != TimeSpan.Zero
            || identity.ProcessStartedAtUtc > DateTimeOffset.UtcNow)
            throw Denied();
        if (!OperatingSystem.IsWindows()) throw Denied();
        try
        {
            var address = IPAddress.Parse(uri.Host.Trim('[', ']'));
            var before = FindListener(address, uri.Port);
            if (before.ProcessId != identity.ProcessId || !before.Address.Equals(listenerAddress)) throw Denied();
            AssertNoReparsePoint(identity.ExecutablePath);
            using var process = Process.GetProcessById(identity.ProcessId);
            var started = process.StartTime.ToUniversalTime();
            var image = process.MainModule?.FileName;
            if (started.Ticks != identity.ProcessStartedAtUtc.UtcTicks
                || !string.Equals(image, identity.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(InferenceAdmissionValidator.HashFile(identity.ExecutablePath),
                    identity.ExecutableSha256, StringComparison.OrdinalIgnoreCase)) throw Denied();
            // Hashing and process inspection are not an atomic listener snapshot.
            // Reject replacement/restart discovered at the second OS observation.
            process.Refresh();
            var after = FindListener(address, uri.Port);
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != started.Ticks
                || before != after) throw Denied();
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException
            or ArgumentException or NotSupportedException or UnauthorizedAccessException)
        { throw Denied(); }
    }

    private static Listener FindListener(IPAddress address, int port)
    {
        var ipv6 = address.AddressFamily == AddressFamily.InterNetworkV6;
        var family = ipv6 ? 23 : 2;
        var size = 0;
        var first = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3, 0);
        if (first != 122 || size is < 4 or > 8388608) throw Denied();
        var table = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(table, ref size, false, family, 3, 0) != 0) throw Denied();
            var count = Marshal.ReadInt32(table);
            var rowSize = ipv6 ? 56 : 24;
            if (count is < 0 or > 65536 || 4L + (long)count * rowSize > size) throw Denied();
            var matches = new List<Listener>();
            for (var i = 0; i < count; i++)
            {
                var row = IntPtr.Add(table, 4 + i * rowSize);
                var portOffset = ipv6 ? 20 : 8;
                var observedPort = (Marshal.ReadByte(row, portOffset) << 8) | Marshal.ReadByte(row, portOffset + 1);
                if (observedPort != port) continue;
                IPAddress observedAddress;
                if (ipv6)
                {
                    var bytes = new byte[16];
                    Marshal.Copy(row, bytes, 0, bytes.Length);
                    observedAddress = new IPAddress(bytes, (uint)Marshal.ReadInt32(row, 16));
                }
                else observedAddress = new IPAddress(BitConverter.GetBytes(Marshal.ReadInt32(row, 4)));
                if (!observedAddress.Equals(address)
                    && !observedAddress.Equals(ipv6 ? IPAddress.IPv6Any : IPAddress.Any)) continue;
                matches.Add(new(observedAddress, Marshal.ReadInt32(row, ipv6 ? 52 : 20)));
            }
            return matches.Count == 1 ? matches[0] : throw Denied();
        }
        finally { Marshal.FreeHGlobal(table); }
    }

    private static void AssertNoReparsePoint(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw Denied();
    }
    private static UnauthorizedAccessException Denied() => DirectTextHttpAdmissionValidator.Denied(
        "The current OS listener, process creation time or reviewed executable identity was not established.");
    private sealed record Listener(IPAddress Address, int ProcessId);
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily, int tableClass, uint reserved);
}
