using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SwarmCore.Services.Abstractions;
using SwarmCore.Services.Adapters;

namespace SwarmCore.Services;

/// <summary>
/// Default <see cref="IServiceCatalog"/>: stores services, selects them by need,
/// keeps credentials apart from definitions, and routes calls to the right
/// adapter. Every call is logged (provenance + outcome) for visibility.
/// </summary>
public sealed class ServiceCatalog : IServiceCatalog
{
    private readonly IServiceStore _store;
    private readonly ICredentialStore _credentials;
    private readonly IServiceSelector _selector;
    private readonly IServiceInvoker _invoker;
    private readonly ILogger<ServiceCatalog> _logger;

    public ServiceCatalog(
        IServiceStore store, ICredentialStore credentials, IServiceSelector selector,
        IServiceInvoker invoker, ILogger<ServiceCatalog>? logger = null)
    {
        _store = store;
        _credentials = credentials;
        _selector = selector;
        _invoker = invoker;
        _logger = logger ?? NullLogger<ServiceCatalog>.Instance;
    }

    public Task InitializeAsync(CancellationToken ct = default) => _store.InitializeAsync(ct);

    public async Task<ServiceDefinition> AddAsync(ServiceDefinition service, string? secret = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (string.IsNullOrWhiteSpace(service.Name))
            throw new ArgumentException("Service name is required.", nameof(service));

        var sameName = await _store.GetByNameAsync(service.Name.Trim(), ct).ConfigureAwait(false);
        if (sameName is not null && !string.Equals(sameName.Id, service.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"A service named '{service.Name}' already exists. Inspect/revise it instead of creating a duplicate.");
        }

        if (service.Kind == ServiceKind.CSharp)
        {
            var source = service.Config.GetValueOrDefault("source") ?? "";
            var validation = CSharpServiceAdapter.Validate(source);
            if (!validation.Ok)
                throw new InvalidOperationException(
                    "C# service validation failed: " + (validation.Error ?? "source did not compile."));
        }

        ServiceRevisionManager.EnsureInitialized(service);
        if (!string.IsNullOrEmpty(secret))
        {
            service.CredentialRef ??= "svc-" + service.Id;
            await _credentials.SetAsync(service.CredentialRef!, secret, ct).ConfigureAwait(false);
        }
        await _store.SaveAsync(service, ct).ConfigureAwait(false);
        _logger.LogInformation("Service added: {Name} ({Kind}) by {By}", service.Name, service.Kind, service.CreatedBy);
        return service;
    }

    public Task<IReadOnlyList<ServiceDefinition>> ListAsync(CancellationToken ct = default) => _store.ListAsync(ct);

    public async Task<IReadOnlyList<ServiceDefinition>> SearchAsync(string query, ServiceKind? kind = null, int limit = 8, CancellationToken ct = default)
    {
        var all = await _store.ListAsync(ct).ConfigureAwait(false);
        return await _selector.SelectAsync(query, all, kind, limit, ct).ConfigureAwait(false);
    }

    public async Task<ServiceDefinition?> GetAsync(string idOrName, CancellationToken ct = default)
        => await _store.GetAsync(idOrName, ct).ConfigureAwait(false)
           ?? await _store.GetByNameAsync(idOrName, ct).ConfigureAwait(false);

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        var svc = await _store.GetAsync(id, ct).ConfigureAwait(false);
        if (svc?.CredentialRef is { } cred) await _credentials.DeleteAsync(cred, ct).ConfigureAwait(false);
        await _store.DeleteAsync(id, ct).ConfigureAwait(false);
    }

    public async Task<ServiceCallResult> CallAsync(string idOrName, IReadOnlyDictionary<string, object?> args, CancellationToken ct = default)
    {
        var svc = await GetAsync(idOrName, ct).ConfigureAwait(false);
        if (svc is null) return ServiceCallResult.Fail($"No service named '{idOrName}'.");

        var result = await _invoker.InvokeAsync(svc, args, ct).ConfigureAwait(false);
        var semanticFailure = result.Ok
            ? ServiceResultSemantics.DetectFailure(result.Output)
            : null;
        if (semanticFailure is not null)
        {
            result = ServiceCallResult.Fail($"{semanticFailure}: {Bound(result.Output, 2_000)}");
        }
        svc.LastCalledUtc = DateTime.UtcNow;
        svc.LastCallSucceeded = result.Ok;
        svc.LastCallError = result.Ok ? null : Bound(result.Error, 2_000);
        svc.ConsecutiveFailures = result.Ok ? 0 : svc.ConsecutiveFailures + 1;
        try
        {
            await _store.SaveAsync(svc, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to persist service-call health for {Name}", svc.Name);
        }
        _logger.LogInformation("Service call {Name} ({Kind}) → {Outcome}", svc.Name, svc.Kind, result.Ok ? "ok" : "error");
        return result;
    }

    public async Task<ServiceCallResult> CallAuthorizedSnapshotAsync(
        ServiceDefinition authorizedSnapshot,
        IReadOnlyDictionary<string, object?> args,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(authorizedSnapshot);
        var result = await _invoker.InvokeAsync(authorizedSnapshot, args, ct).ConfigureAwait(false);
        var semanticFailure = result.Ok ? ServiceResultSemantics.DetectFailure(result.Output) : null;
        if (semanticFailure is not null)
            result = ServiceCallResult.Fail($"{semanticFailure}: {Bound(result.Output, 2_000)}");
        _logger.LogInformation(
            "Authorized service snapshot call {Name} ({Kind}) → {Outcome}",
            authorizedSnapshot.Name,
            authorizedSnapshot.Kind,
            result.Ok ? "ok" : "error");
        return result;
    }

    private static string? Bound(string? value, int max)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
