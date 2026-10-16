using System.Collections.Concurrent;
using SwarmCore.Services.Abstractions;

namespace SwarmCore.Services.Adapters;

/// <summary>
/// Invokes services implemented in host code. The host registers a handler by id
/// and seeds a <see cref="ServiceKind.Builtin"/> service whose <c>Config["handler"]</c>
/// names it — so an existing in-app capability becomes a catalog service with no
/// duplicated logic. Singleton so handler registrations are shared across scopes.
/// </summary>
public sealed class BuiltinServiceAdapter : IServiceKindAdapter
{
    private readonly ConcurrentDictionary<string, Func<IReadOnlyDictionary<string, object?>, CancellationToken, Task<ServiceCallResult>>> _handlers
        = new(StringComparer.OrdinalIgnoreCase);

    public ServiceKind Kind => ServiceKind.Builtin;

    /// <summary>Register a handler the catalog can dispatch to by id (idempotent).</summary>
    public void Register(string handlerId, Func<IReadOnlyDictionary<string, object?>, CancellationToken, Task<ServiceCallResult>> handler)
        => _handlers[handlerId] = handler;

    public bool Has(string handlerId) => _handlers.ContainsKey(handlerId);

    public async Task<ServiceCallResult> InvokeAsync(ServiceDefinition service, IReadOnlyDictionary<string, object?> args, CancellationToken ct = default)
    {
        await SwarmCore.Abstractions.Security.ExecutionResourceBoundary.CheckUnclassifiedAsync("service.builtin", service.Name, ct);
        var id = service.Config.GetValueOrDefault("handler") ?? service.Name;
        return _handlers.TryGetValue(id, out var handler)
            ? await handler(args, ct)
            : ServiceCallResult.Fail($"No builtin handler '{id}'.");
    }
}
