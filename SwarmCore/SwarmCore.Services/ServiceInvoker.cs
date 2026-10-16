using SwarmCore.Services.Abstractions;

namespace SwarmCore.Services;

/// <summary>Routes a service call to the adapter registered for its kind.</summary>
public sealed class ServiceInvoker : IServiceInvoker
{
    private readonly Dictionary<ServiceKind, IServiceKindAdapter> _adapters;

    public ServiceInvoker(IEnumerable<IServiceKindAdapter> adapters)
        => _adapters = adapters.GroupBy(a => a.Kind).ToDictionary(g => g.Key, g => g.Last());

    public async Task<ServiceCallResult> InvokeAsync(ServiceDefinition service, IReadOnlyDictionary<string, object?> args, CancellationToken ct = default)
    {
        await SwarmCore.Abstractions.Security.ExecutionResourceBoundary.CheckUnclassifiedAsync("service", service.Name, ct);
        if (!service.Enabled) return ServiceCallResult.Fail($"Service '{service.Name}' is disabled.");
        if (!_adapters.TryGetValue(service.Kind, out var adapter))
            return ServiceCallResult.Fail($"No adapter for service kind {service.Kind}.");
        return await adapter.InvokeAsync(service, args, ct);
    }
}
