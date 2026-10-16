namespace SwarmCore.Services.Abstractions;

/// <summary>Ranks candidate services against a natural-language need.</summary>
public interface IServiceSelector
{
    Task<IReadOnlyList<ServiceDefinition>> SelectAsync(
        string query,
        IReadOnlyList<ServiceDefinition> candidates,
        ServiceKind? kind,
        int limit,
        CancellationToken ct = default);
}

/// <summary>Dispatches a call to the adapter registered for the service's kind.</summary>
public interface IServiceInvoker
{
    Task<ServiceCallResult> InvokeAsync(ServiceDefinition service, IReadOnlyDictionary<string, object?> args, CancellationToken ct = default);
}

/// <summary>
/// The single façade for the capability catalog. Users and agents add services
/// (optionally with a secret), search them by what they're for, and call them —
/// the catalog stores, selects, and routes to the right adapter.
/// </summary>
public interface IServiceCatalog
{
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>Add or update a service. If <paramref name="secret"/> is given it's stored under the service's <see cref="ServiceDefinition.CredentialRef"/>.</summary>
    Task<ServiceDefinition> AddAsync(ServiceDefinition service, string? secret = null, CancellationToken ct = default);

    Task<IReadOnlyList<ServiceDefinition>> ListAsync(CancellationToken ct = default);

    /// <summary>Find the services best matching a need, optionally filtered by kind.</summary>
    Task<IReadOnlyList<ServiceDefinition>> SearchAsync(string query, ServiceKind? kind = null, int limit = 8, CancellationToken ct = default);

    Task<ServiceDefinition?> GetAsync(string idOrName, CancellationToken ct = default);

    Task DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>Invoke a service by id or name with arguments.</summary>
    Task<ServiceCallResult> CallAsync(string idOrName, IReadOnlyDictionary<string, object?> args, CancellationToken ct = default);

    /// <summary>
    /// Invoke the exact host-authorized snapshot without reloading a mutable definition. This is
    /// the policy-safe path for agent tools; it deliberately does not overwrite catalog metadata.
    /// </summary>
    Task<ServiceCallResult> CallAuthorizedSnapshotAsync(
        ServiceDefinition authorizedSnapshot,
        IReadOnlyDictionary<string, object?> args,
        CancellationToken ct = default);
}
