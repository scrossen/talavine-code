using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SwarmCore.Abstractions.Processes;

namespace SwarmCore.Mcp.Core;

/// <summary>
/// Keeps the SDK's JSON-RPC stream transport while launching through the shared
/// Apple-safe process host. The returned session owns the child, including failed initialization.
/// </summary>
internal sealed class ChildProcessStdioTransport(StdioClientTransportOptions options) : IClientTransport
{
    public string Name => options.Name ?? options.Command;

    internal static ProcessStartInfo BuildStartInfo(StdioClientTransportOptions options)
    {
        var start = new ProcessStartInfo
        {
            FileName = options.Command,
            WorkingDirectory = options.WorkingDirectory ?? string.Empty,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in options.Arguments ?? []) start.ArgumentList.Add(argument);
        if (!options.InheritEnvironmentVariables) start.Environment.Clear();
        if (options.EnvironmentVariables is not null)
            foreach (var (key, value) in options.EnvironmentVariables) start.Environment[key] = value;
        return start;
    }

    public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var child = ChildProcessLauncher.Start(BuildStartInfo(options));
        try
        {
            // Drain stderr without retaining or logging server text, which may contain credentials.
            child.BeginErrorReadLine();
            cancellationToken.ThrowIfCancellationRequested();
            var streams = new StreamClientTransport(child.StandardInput.BaseStream, child.StandardOutput.BaseStream);
            var session = await streams.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return new OwnedSession(session, child, options.ShutdownTimeout);
        }
        catch
        {
            try { await StopChildAsync(child, options.ShutdownTimeout).ConfigureAwait(false); }
            finally { child.Dispose(); }
            throw;
        }
    }

    private static async Task StopChildAsync(IChildProcess child, TimeSpan grace)
    {
        try { child.StandardInput.Close(); } catch (IOException) { } catch (ObjectDisposedException) { }
        using var graceful = new CancellationTokenSource(grace);
        try { await child.WaitForExitAsync(graceful.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            child.Kill(entireProcessTree: true);
            using var killed = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await child.WaitForExitAsync(killed.Token).ConfigureAwait(false);
        }
    }

    private sealed class OwnedSession(ITransport inner, IChildProcess child, TimeSpan shutdownTimeout) : ITransport
    {
        private int _disposed;
        public string? SessionId => inner.SessionId;
        public ChannelReader<JsonRpcMessage> MessageReader => inner.MessageReader;
        public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
            => inner.SendMessageAsync(message, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { await StopChildAsync(child, shutdownTimeout).ConfigureAwait(false); }
            finally
            {
                try { await inner.DisposeAsync().ConfigureAwait(false); }
                finally { child.Dispose(); }
            }
        }
    }
}
