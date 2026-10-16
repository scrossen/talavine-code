namespace SwarmCore.Services.Abstractions;

/// <summary>
/// Knows how to invoke one <see cref="ServiceKind"/>. Registering a new adapter
/// teaches the catalog a new kind of capability without touching the catalog.
/// </summary>
public interface IServiceKindAdapter
{
    ServiceKind Kind { get; }

    /// <summary>Invoke the service with JSON-ish args, returning a string (JSON/text) result.</summary>
    Task<ServiceCallResult> InvokeAsync(
        ServiceDefinition service,
        IReadOnlyDictionary<string, object?> args,
        CancellationToken ct = default);
}
