namespace SwarmCore.Abstractions.Security;

/// <summary>Host-owned ambient dispatch fence for adapters below the agent dependency layer.</summary>
public static class ExecutionResourceBoundary
{
    private static readonly AsyncLocal<Func<string, string, CancellationToken, Task>?> Fence = new();
    private static readonly AsyncLocal<Func<ExecutionResourceAction, CancellationToken, Task>?> ClassifiedFence = new();
    public static Func<string, string, CancellationToken, Task>? Current { get => Fence.Value; set => Fence.Value = value; }
    public static Func<ExecutionResourceAction, CancellationToken, Task>? Classified { get => ClassifiedFence.Value; set => ClassifiedFence.Value = value; }
    public static bool IsActive => Current is not null;
    public static Task CheckUnclassifiedAsync(string surface, string resource, CancellationToken ct)
        => Current?.Invoke(surface, resource, ct) ?? Task.CompletedTask;
    public static Task CheckAsync(ExecutionResourceAction action, CancellationToken ct)
        => Classified?.Invoke(action, ct) ?? CheckUnclassifiedAsync(action.Surface, action.Identity, ct);
}

/// <summary>Produced by compiled/host-configured adapters, never by tool annotations or model arguments.</summary>
public sealed record ExecutionResourceAction(string Surface, string Kind, string Identity, string Effects);
