using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SwarmCore.Secrets;

/// <summary>Host-owned key-wrap server with an explicit peer authentication policy and bounded data-key operations.</summary>
public sealed class KeyStoreBrokerServer(IOsKeyStore keyStore, string dataDirectory,
    Func<Socket, bool> authenticatePeer, ILogger<KeyStoreBrokerServer> logger) : IAsyncDisposable
{
    public sealed record Endpoint(int ProcessId, string SocketPath, string ExecutablePath);
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentDictionary<Socket, Task> clients = new();
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private bool disposed;
    private Socket? listener;
    private Task? accept;
    private string? ownedDirectory;
    public string? SocketPath { get; private set; }
    public string? StartupFailure { get; private set; }
    private string EndpointPath => Path.Combine(dataDirectory, KeyStoreBrokerProtocol.EndpointFileName);

    public async Task<bool> StartAsync(CancellationToken ct = default)
    {
        await lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (listener is not null) return true;
        return await Task.Run(() =>
        {
            if (!keyStore.IsAvailable) { StartupFailure = "The OS keystore is unavailable."; return false; }
            try
            {
                ownedDirectory = Directory.CreateTempSubdirectory("sc-key-").FullName;
                if (System.Text.Encoding.UTF8.GetByteCount(Path.Combine(ownedDirectory, "k")) >= 104)
                {
                    Directory.Delete(ownedDirectory);
                    // Unix-domain socket limits apply on Windows too. Process-owned test
                    // or host temp roots can be longer than the socket address permits.
                    var shortRoot = OperatingSystem.IsWindows()
                        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp")
                        : "/tmp";
                    var shortPath = Path.Combine(shortRoot, "sc-key-" + Guid.NewGuid().ToString("N"));
                    if (System.Text.Encoding.UTF8.GetByteCount(Path.Combine(shortPath, "k")) >= 104)
                        throw new ArgumentException("No private socket path fits the platform limit.");
                    ownedDirectory = OperatingSystem.IsWindows()
                        ? Directory.CreateDirectory(shortPath).FullName
                        : Directory.CreateDirectory(shortPath,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute).FullName;
                }
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(ownedDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                SocketPath = Path.Combine(ownedDirectory, "k");
                listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                listener.Bind(new UnixDomainSocketEndPoint(SocketPath)); listener.Listen(16);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(SocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                Directory.CreateDirectory(dataDirectory);
                var temporary = EndpointPath + "." + Guid.NewGuid().ToString("N");
                try
                {
                    File.WriteAllText(temporary, JsonSerializer.Serialize(new Endpoint(Environment.ProcessId, SocketPath, Environment.ProcessPath!)));
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    File.Move(temporary, EndpointPath, true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                accept = AcceptAsync(stop.Token);
                return true;
            }
            catch (Exception ex) when (ex is IOException or SocketException or UnauthorizedAccessException or ArgumentException)
            {
                StartupFailure = "The private key-wrap broker could not start.";
                logger.LogWarning("Private key-wrap broker unavailable ({FailureType}).", ex.GetType().Name);
                Cleanup(); return false;
            }
        }, ct).ConfigureAwait(false);
        }
        finally { lifecycle.Release(); }
    }
    private async Task AcceptAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var socket = await listener!.AcceptAsync(ct);
                if (clients.Count >= 16) { socket.Dispose(); continue; }
                var task = ServeAsync(socket, ct); clients[socket] = task;
                _ = task.ContinueWith(completed => { clients.TryRemove(socket, out var ignored); }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested) { }
        catch (SocketException) when (ct.IsCancellationRequested) { }
    }
    private async Task ServeAsync(Socket socket, CancellationToken ct)
    {
        using (socket)
        {
            try
            {
                if (!authenticatePeer(socket)) return;
                await using var stream = new NetworkStream(socket, false);
                while (!ct.IsCancellationRequested)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    deadline.CancelAfter(TimeSpan.FromSeconds(10));
                    var frame = await KeyStoreBrokerProtocol.ReadAsync(stream, deadline.Token);
                    if (frame is null) return;
                    var (kind, payload) = frame.Value;
                    byte[] response = [];
                    byte status = KeyStoreBrokerProtocol.Ok;
                    try
                    {
                        if (kind == KeyStoreBrokerProtocol.Wrap && payload.Length == 32) response = keyStore.WrapKey(payload);
                        else if (kind == KeyStoreBrokerProtocol.Unwrap && payload.Length is >= 28 and <= KeyStoreBrokerProtocol.MaxPayload)
                        {
                            response = keyStore.UnwrapKey(payload);
                            if (response.Length != 32) throw new CryptographicException("Invalid data key.");
                        }
                        else throw new CryptographicException("Invalid key-wrap operation.");
                    }
                    catch (Exception)
                    {
                        CryptographicOperations.ZeroMemory(response);
                        response = "Key-wrap operation refused."u8.ToArray(); status = KeyStoreBrokerProtocol.Error;
                    }
                    try { await KeyStoreBrokerProtocol.WriteAsync(stream, status, response, deadline.Token); }
                    finally { CryptographicOperations.ZeroMemory(payload); CryptographicOperations.ZeroMemory(response); }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            { /* Closed, refused and expired clients do not expose data-key contents. */ }
        }
    }
    private void Cleanup()
    {
        listener?.Dispose(); listener = null;
        try
        {
            if (File.Exists(EndpointPath) && JsonSerializer.Deserialize<Endpoint>(File.ReadAllText(EndpointPath))?.SocketPath == SocketPath)
                File.Delete(EndpointPath);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        if (ownedDirectory is not null)
        {
            try { Directory.Delete(ownedDirectory, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        SocketPath = null;
    }
    public async ValueTask DisposeAsync()
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
        if (disposed) return;
        disposed = true;
        await stop.CancelAsync(); listener?.Dispose();
        foreach (var socket in clients.Keys) socket.Dispose();
        if (accept is not null) await accept.ConfigureAwait(false);
        await Task.WhenAll(clients.Values).ConfigureAwait(false);
        Cleanup(); stop.Dispose();
        }
        finally { lifecycle.Release(); }
    }
}
