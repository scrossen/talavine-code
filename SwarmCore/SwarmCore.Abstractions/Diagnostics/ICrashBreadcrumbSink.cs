namespace SwarmCore.Abstractions.Diagnostics;

/// <summary>
/// A bounded, privacy-safe lifecycle checkpoint written synchronously so it can
/// survive a native process termination that bypasses managed exception
/// handling. Callers must never include prompts, CLI output, credentials,
/// endpoints, account identifiers, or exception messages.
/// </summary>
public sealed record CrashBreadcrumbEvent
{
    public required string Component { get; init; }
    public required string Operation { get; init; }
    public required string Stage { get; init; }
    public string? Provider { get; init; }
    public string? Outcome { get; init; }
    public int? ItemCount { get; init; }
    public long? ElapsedMilliseconds { get; init; }
    public string? FailureType { get; init; }
    public int? ChildProcessId { get; init; }
}

/// <summary>
/// Best-effort synchronous crash breadcrumb sink. Implementations must never
/// allow diagnostic I/O failures to affect product work.
/// </summary>
public interface ICrashBreadcrumbSink
{
    void Write(CrashBreadcrumbEvent breadcrumb);
}

/// <summary>No-op default for reusable SwarmCore hosts without a diagnostics file.</summary>
public sealed class NullCrashBreadcrumbSink : ICrashBreadcrumbSink
{
    public static NullCrashBreadcrumbSink Instance { get; } = new();

    private NullCrashBreadcrumbSink()
    {
    }

    public void Write(CrashBreadcrumbEvent breadcrumb)
    {
    }
}
