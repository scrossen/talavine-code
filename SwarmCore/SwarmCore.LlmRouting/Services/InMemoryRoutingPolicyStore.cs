using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting.Services;

/// <summary>
/// Process-local routing policy store. The default registered by SwarmCore so the router
/// never null-checks the policy. Apps register a SQLite-backed implementation over this
/// default when they want the user's policy to persist across restarts.
/// </summary>
public sealed class InMemoryRoutingPolicyStore : IRoutingPolicyStore
{
    private RoutingPolicy _current = RoutingPolicy.Default;
    private string _revision = Guid.NewGuid().ToString("N");
    private readonly object _gate = new();

    public Task<RoutingPolicy> GetAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_current);
        }
    }

    public Task SetAsync(RoutingPolicy policy, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _current = policy ?? RoutingPolicy.Default;
            _revision = Guid.NewGuid().ToString("N");
        }
        return Task.CompletedTask;
    }

    public Task<RoutingPolicySnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(new RoutingPolicySnapshot(_current, _revision));
        }
    }

    public Task<RoutingPolicyConditionalWriteResult> TrySetAsync(
        RoutingPolicy policy,
        RoutingPolicySnapshot expected,
        string mutationRevision,
        CancellationToken ct = default)
    {
        mutationRevision = RoutingPolicyStorageCodec.ValidateRevision(mutationRevision);
        lock (_gate)
        {
            if (_current != expected.Policy
                || !string.Equals(_revision, expected.Revision, StringComparison.Ordinal))
            {
                return Task.FromResult(new RoutingPolicyConditionalWriteResult(
                    RoutingPolicyConditionalWriteStatus.Conflict,
                    _current,
                    _revision));
            }

            _current = policy ?? RoutingPolicy.Default;
            _revision = mutationRevision;
            return Task.FromResult(new RoutingPolicyConditionalWriteResult(
                RoutingPolicyConditionalWriteStatus.Applied,
                _current,
                _revision));
        }
    }
}
