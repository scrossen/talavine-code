namespace SwarmCore.Agent.Runtime;

/// <summary>
/// Runs several <see cref="IToolInvocationGuard"/>s in order for the engine's single guard seam.
/// The first block wins and stops evaluation (later guards are not consulted, so a host's
/// content-safety guard listed first keeps its verdict); an escalation is remembered and returned
/// when no later guard blocks. A host lists its guards in the order their verdicts should apply,
/// for example Talavine's <c>FenceTaintToolGuard</c> before the coding harness's repeated-call guard.
/// The composite adds no policy of its own.
/// </summary>
public sealed class CompositeToolInvocationGuard : IToolInvocationGuard
{
    private readonly IReadOnlyList<IToolInvocationGuard> _guards;

    public CompositeToolInvocationGuard(IEnumerable<IToolInvocationGuard> guards)
    {
        ArgumentNullException.ThrowIfNull(guards);
        _guards = guards.Where(g => g is not null).ToArray();
        if (_guards.Any(g => ReferenceEquals(g, this)))
            throw new ArgumentException("A composite guard cannot contain itself.", nameof(guards));
    }

    public CompositeToolInvocationGuard(params IToolInvocationGuard[] guards)
        : this((IEnumerable<IToolInvocationGuard>)guards)
    {
    }

    /// <summary>The guards in evaluation order.</summary>
    public IReadOnlyList<IToolInvocationGuard> Guards => _guards;

    public async Task<ToolGuardDecision> InspectAsync(ToolInvocationRequest request, CancellationToken ct = default)
    {
        ToolGuardDecision? escalation = null;
        foreach (var guard in _guards)
        {
            ct.ThrowIfCancellationRequested();
            var decision = await guard.InspectAsync(request, ct).ConfigureAwait(false);
            if (!decision.Allowed) return decision;
            if (decision.RequiresApproval) escalation ??= decision;
        }
        return escalation ?? ToolGuardDecision.Allow();
    }
}

/// <summary>
/// Applies an inner guard only while the current turn runs in a host-selected workspace
/// (<see cref="CurrentTurnContext.WorkspaceRoot"/>), i.e. a coding turn. Hosts whose ordinary
/// turns must keep their existing guard behavior use it to add coding-only robustness checks
/// (such as stuck-call detection) without changing every other turn.
/// </summary>
public sealed class WorkspaceTurnToolGuard(IToolInvocationGuard inner, ICurrentTurnContextAccessor? turns = null) : IToolInvocationGuard
{
    public Task<ToolGuardDecision> InspectAsync(ToolInvocationRequest request, CancellationToken ct = default)
    {
        var current = turns?.Current ?? AsyncLocalCurrentTurnContextAccessor.Ambient;
        return string.IsNullOrWhiteSpace(current?.WorkspaceRoot)
            ? Task.FromResult(ToolGuardDecision.Allow())
            : inner.InspectAsync(request, ct);
    }
}
