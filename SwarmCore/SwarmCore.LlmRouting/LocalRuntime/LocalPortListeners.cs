using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SwarmCore.LlmRouting.LocalRuntime;

/// <summary>A listening endpoint and the process the operating system says owns it.</summary>
public sealed record LocalPortListenerOwner(IPEndPoint Endpoint, int ProcessId);

/// <summary>Lists the local endpoints that listen on a TCP port.</summary>
public interface ILocalPortListenerSource
{
    /// <summary>
    /// Every listening endpoint on <paramref name="port"/>, on any address
    /// family and address. Null when the platform cannot list listeners;
    /// callers treat that as unknown, never as a free port.
    /// </summary>
    IReadOnlyList<IPEndPoint>? GetListeners(int port);

    /// <summary>
    /// The owning process of every listener on <paramref name="port"/>, where
    /// the operating system reports it (Windows). Null when unknown.
    /// </summary>
    IReadOnlyList<LocalPortListenerOwner>? GetListenerOwners(int port) => null;
}

/// <summary>
/// The operating system's TCP listener table. Windows lists a dual-stack
/// socket twice, as 0.0.0.0 and as [::]; other platforms list it once, as [::].
/// </summary>
public sealed class SystemLocalPortListenerSource : ILocalPortListenerSource
{
    public static SystemLocalPortListenerSource Instance { get; } = new();

    public IReadOnlyList<IPEndPoint>? GetListeners(int port)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Where(endpoint => endpoint.Port == port)
                .Distinct()
                .ToArray();
        }
        catch (Exception ex) when (ex is NetworkInformationException
                                       or PlatformNotSupportedException
                                       or InvalidOperationException
                                       or UnauthorizedAccessException
                                       or IOException)
        {
            return null;
        }
    }

    public IReadOnlyList<LocalPortListenerOwner>? GetListenerOwners(int port)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var ipv4 = ReadWindowsListenerOwners(AddressFamilyIPv4, port);
            var ipv6 = ReadWindowsListenerOwners(AddressFamilyIPv6, port);
            return ipv4 is null || ipv6 is null ? null : [.. ipv4, .. ipv6];
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            return null;
        }
    }

    private const int AddressFamilyIPv4 = 2;
    private const int AddressFamilyIPv6 = 23;
    private const int TcpTableOwnerPidListener = 3;
    private const uint ErrorInsufficientBuffer = 122;
    private const int IPv4RowBytes = 24;
    private const int IPv6RowBytes = 56;

    [SupportedOSPlatform("windows")]
    private static List<LocalPortListenerOwner>? ReadWindowsListenerOwners(int addressFamily, int port)
    {
        var size = 0;
        var result = GetExtendedTcpTable(IntPtr.Zero, ref size, order: false, addressFamily, TcpTableOwnerPidListener, 0);
        for (var attempt = 0; attempt < 4 && result == ErrorInsufficientBuffer; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                result = GetExtendedTcpTable(buffer, ref size, order: false, addressFamily, TcpTableOwnerPidListener, 0);
                if (result == ErrorInsufficientBuffer) continue;
                if (result != 0) return null;
                return ReadRows(buffer, addressFamily, port);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        // An empty table can report success for the size query.
        return result == 0 ? [] : null;
    }

    private static List<LocalPortListenerOwner> ReadRows(IntPtr table, int addressFamily, int port)
    {
        var owners = new List<LocalPortListenerOwner>();
        var count = Marshal.ReadInt32(table);
        var ipv6 = addressFamily == AddressFamilyIPv6;
        var rowBytes = ipv6 ? IPv6RowBytes : IPv4RowBytes;
        // MIB_TCPROW_OWNER_PID: state, local address, local port, remote address, remote port, owner.
        // MIB_TCP6ROW_OWNER_PID: local address[16], scope, local port, remote address[16], scope, remote port, state, owner.
        var addressOffset = ipv6 ? 0 : 4;
        var portOffset = ipv6 ? 20 : 8;
        var ownerOffset = ipv6 ? 52 : 20;
        for (var index = 0; index < count; index++)
        {
            var row = IntPtr.Add(table, sizeof(int) + index * rowBytes);
            var portBytes = new byte[2];
            Marshal.Copy(IntPtr.Add(row, portOffset), portBytes, 0, 2);
            if ((portBytes[0] << 8 | portBytes[1]) != port) continue;

            var address = new byte[ipv6 ? 16 : 4];
            Marshal.Copy(IntPtr.Add(row, addressOffset), address, 0, address.Length);
            owners.Add(new LocalPortListenerOwner(
                new IPEndPoint(new IPAddress(address), port),
                Marshal.ReadInt32(row, ownerOffset)));
        }

        return owners;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily,
        int tableClass,
        uint reserved);
}
