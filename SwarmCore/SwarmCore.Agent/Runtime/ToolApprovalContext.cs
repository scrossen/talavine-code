namespace SwarmCore.Agent.Runtime;

/// <summary>
/// The real permission decision attached by <c>ToolUseAgentEngine</c> to one
/// tool invocation. This is ambient only because <see cref="Catalog.ToolDescriptor"/>
/// predates contextual delegates; it is always scoped and restored by the engine.
/// </summary>
public sealed record ToolApprovalContext(
    string RequestId,
    bool Approved,
    string Authority,
    string? Scope = null,
    CurrentTurnToolAccessState? ToolAccessState = null,
    string? UserMessage = null);

/// <summary>Async-flow-local approval evidence for the tool currently executing.</summary>
public static class ToolApprovalContextAccessor
{
    private static readonly AsyncLocal<ToolApprovalContext?> Storage = new();

    public static ToolApprovalContext? Current => Storage.Value;

    /// <summary>The engine's approved user objective covers ordinary nested work.
    /// App agents retain their separate persisted resource delegation.</summary>
    public static bool HasDirectUserRequestAuthority
        => Current is { Approved: true } approval
           && string.Equals(approval.Authority, "user-request", StringComparison.OrdinalIgnoreCase)
           && AsyncLocalCurrentTurnContextAccessor.Ambient?.ApplicationId is null;

    public static IDisposable Push(ToolApprovalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var prior = Storage.Value;
        Storage.Value = context;
        return new Restore(prior);
    }

    private sealed class Restore(ToolApprovalContext? prior) : IDisposable
    {
        private ToolApprovalContext? _prior = prior;
        public void Dispose()
        {
            var prior = Interlocked.Exchange(ref _prior, null);
            Storage.Value = prior;
        }
    }
}
