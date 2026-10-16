using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace SwarmCore.Secrets;

/// <summary>Helper key-wrap adapter. Discovery is profile-local; authenticated peers hold no app KEK.</summary>
public sealed class BrokeredKeyStore(string dataDirectory, Func<Socket, KeyStoreBrokerServer.Endpoint, bool> authenticatePeer)
    : IOsKeyStore, IOsKeyStoreStatus
{
    private readonly object gate = new();
    // A positive probe is a connect plus peer authentication; reuse it briefly so a burst of
    // credential reads does not pay that round trip (or a hung broker's timeout) each time.
    private const long AvailableProbeLifetimeMs = 30_000;
    private long availableUntil;
    public string ProviderId => "keychain-broker";
    public OsKeyStoreUnavailableReason UnavailableReason => IsAvailable ? OsKeyStoreUnavailableReason.None : OsKeyStoreUnavailableReason.Unavailable;
    public bool IsAvailable
    {
        get
        {
            if (Environment.TickCount64 < Interlocked.Read(ref availableUntil)) return true;
            try
            {
                using var socket = Connect();
                Interlocked.Exchange(ref availableUntil, Environment.TickCount64 + AvailableProbeLifetimeMs);
                return true;
            }
            catch (Exception ex) when (IsUnavailable(ex))
            {
                Interlocked.Exchange(ref availableUntil, 0);
                return false;
            }
        }
    }
    public byte[] WrapKey(byte[] plaintextKey)
    {
        if (plaintextKey.Length != 32) throw new CryptographicException("Only envelope data keys can be wrapped.");
        return Exchange(KeyStoreBrokerProtocol.Wrap, plaintextKey);
    }
    public byte[] UnwrapKey(byte[] wrappedKey) => Exchange(KeyStoreBrokerProtocol.Unwrap, wrappedKey);
    private Socket Connect()
    {
        var file = Path.Combine(dataDirectory, KeyStoreBrokerProtocol.EndpointFileName);
        if (new FileInfo(file).Length is <= 0 or > 16 * 1024
            || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(dataDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("No owned key-wrap endpoint.");
        var endpoint = JsonSerializer.Deserialize<KeyStoreBrokerServer.Endpoint>(File.ReadAllText(file));
        if (endpoint is not { ProcessId: > 0 } || !Path.IsPathFullyQualified(endpoint.SocketPath) || !Path.IsPathFullyQualified(endpoint.ExecutablePath))
            throw new IOException("No valid key-wrap endpoint.");
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint.SocketPath), deadline.Token).AsTask().GetAwaiter().GetResult();
            if (!authenticatePeer(socket, endpoint)) throw new IOException("Key-wrap peer authentication failed.");
            return socket;
        }
        catch { socket.Dispose(); throw; }
    }
    private byte[] Exchange(byte kind, byte[] payload)
    {
        lock (gate)
        {
            using var socket = Connect();
            using var stream = new NetworkStream(socket, false);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            KeyStoreBrokerProtocol.WriteAsync(stream, kind, payload, deadline.Token).GetAwaiter().GetResult();
            var response = KeyStoreBrokerProtocol.ReadAsync(stream, deadline.Token).GetAwaiter().GetResult();
            if (response is not { } frame) throw new IOException("The key-wrap broker closed the connection.");
            if (frame.Kind != KeyStoreBrokerProtocol.Ok || kind == KeyStoreBrokerProtocol.Unwrap && frame.Payload.Length != 32)
            {
                CryptographicOperations.ZeroMemory(frame.Payload);
                throw new CryptographicException("The key-wrap broker refused the operation.");
            }
            return frame.Payload;
        }
    }
    private static bool IsUnavailable(Exception ex) => ex is IOException or SocketException or JsonException or UnauthorizedAccessException
        or ArgumentException or OperationCanceledException or NotSupportedException;
}
