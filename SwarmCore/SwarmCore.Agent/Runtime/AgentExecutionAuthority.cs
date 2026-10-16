namespace SwarmCore.Agent.Runtime;

/// <summary>Host-selected model authority for a whole execution, not a tool grant or user identity.</summary>
public sealed record AgentExecutionAuthority(Guid EmployeeId, Guid ToolProfileId, string ActingAgentId,
    string RunId, string ProfileBinding, string TenantBinding, Guid SourceAccountId,
    string Provider, string Model, string TaskId);

public interface IAgentExecutionAuthorityAccessor
{
    AgentExecutionAuthority? Current { get; }
    IDisposable Enter(AgentExecutionAuthority authority, Func<CancellationToken, ValueTask<bool>> validateLive);
    ValueTask<bool> ValidateCurrentAsync(CancellationToken ct);
}

/// <summary>Enter synchronously in the calling flow. Disposal also revokes inherited child flows.</summary>
public sealed class AsyncLocalAgentExecutionAuthorityAccessor : IAgentExecutionAuthorityAccessor
{
    private readonly AsyncLocal<Lease?> current = new();
    public AgentExecutionAuthority? Current => current.Value is { Active: true } lease ? lease.Authority : null;

    public IDisposable Enter(AgentExecutionAuthority authority, Func<CancellationToken, ValueTask<bool>> validateLive)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(validateLive);
        if (Current is not null) throw new UnauthorizedAccessException("Nested execution authority is not allowed.");
        var lease = new Lease(authority, validateLive, current.Value, this);
        current.Value = lease;
        return lease;
    }

    public async ValueTask<bool> ValidateCurrentAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var lease = current.Value;
        if (lease is not { Active: true }) return false;
        var valid = await lease.ValidateLive(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return valid && lease.Active && ReferenceEquals(current.Value, lease);
    }

    private sealed class Lease(AgentExecutionAuthority authority, Func<CancellationToken, ValueTask<bool>> validateLive,
        Lease? prior, AsyncLocalAgentExecutionAuthorityAccessor owner) : IDisposable
    {
        private int active = 1;
        public bool Active => Volatile.Read(ref active) == 1;
        public AgentExecutionAuthority Authority { get; } = authority;
        public Func<CancellationToken, ValueTask<bool>> ValidateLive { get; } = validateLive;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref active, 0) == 0) return;
            if (ReferenceEquals(owner.current.Value, this)) owner.current.Value = prior;
        }
    }
}
