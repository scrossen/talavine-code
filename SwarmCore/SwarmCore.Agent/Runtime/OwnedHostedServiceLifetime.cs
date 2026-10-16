using Microsoft.Extensions.Hosting;

namespace SwarmCore.Agent.Runtime;

/// <summary>Owns selective hosted-service startup when a tool host deliberately does not start a full IHost.</summary>
public sealed class OwnedHostedServiceLifetime(IServiceProvider provider,
    Func<CancellationToken, Task>? beforeProviderDisposal = null) : IAsyncDisposable
{
    private readonly List<(IHostedService Service, bool OwnsInstance)> _started = [];
    private bool _disposed;

    public async Task StartAsync(IHostedService service, bool ownsInstance, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _started.Add((service, ownsInstance));
        await service.StartAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        List<Exception> failures = [];
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        for (var i = _started.Count - 1; i >= 0; i--)
        {
            var (service, ownsInstance) = _started[i];
            try { await service.StopAsync(shutdown.Token); }
            catch (Exception ex) { failures.Add(ex); }
            if (!ownsInstance) continue;
            try
            {
                if (service is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
                else if (service is IDisposable disposable) disposable.Dispose();
            }
            catch (Exception ex) { failures.Add(ex); }
        }
        try { if (beforeProviderDisposal is not null) await beforeProviderDisposal(shutdown.Token); }
        catch (Exception ex) { failures.Add(ex); }
        try
        {
            if (provider is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
            else if (provider is IDisposable disposable) disposable.Dispose();
        }
        catch (Exception ex) { failures.Add(ex); }
        if (failures.Count > 0) throw new AggregateException("Selected host services failed to shut down cleanly.", failures);
    }
}
