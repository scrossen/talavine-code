using SwarmCore.Agent.Runtime;
using SwarmCore.Skills.Models;

namespace SwarmCode.Hosting;

/// <summary>
/// Fallback permission gate. Interactive mode asks y/N on the terminal; -p mode and
/// --yes auto-approve. Local coding tools normally never reach it because the
/// orchestrator marks the turn as user-requested.
/// </summary>
public sealed class ConsolePermissionBroker : IPermissionBroker
{
    private readonly bool _autoApprove;
    private readonly object _promptGate = new();

    public ConsolePermissionBroker(bool autoApprove) => _autoApprove = autoApprove;

    public IDisposable Subscribe(Action<PendingPermissionRequest> onRequest) => NoopDisposable.Instance;

    public void Respond(string requestId, bool approved, PermissionGrantScope scope) { }

    public Task<PermissionConsentResult> RequestAsync(PermissionConsentRequest request, CancellationToken ct = default)
    {
        if (_autoApprove)
            return Task.FromResult(new PermissionConsentResult { Approved = true, Scope = PermissionGrantScope.Once });

        bool approved;
        lock (_promptGate)
        {
            var permissions = string.Join(", ", request.MissingPermissions.Select(p => p.Name));
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Permission requested: {request.SkillName} / {request.FunctionName}");
            Console.Error.WriteLine($"  needs: {(permissions.Length == 0 ? "(unspecified)" : permissions)}");
            Console.Error.Write("  allow? [y/N] ");
            var answer = Console.IsInputRedirected ? null : Console.ReadLine();
            approved = answer is not null && answer.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase);
        }
        return Task.FromResult(new PermissionConsentResult { Approved = approved, Scope = PermissionGrantScope.Once });
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}
