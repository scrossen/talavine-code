namespace SwarmCore.Services;

/// <summary>The kind of capability a service exposes — selects which adapter invokes it.</summary>
public enum ServiceKind
{
    /// <summary>An HTTP/REST endpoint described by a URL template + auth.</summary>
    Http = 0,

    /// <summary>An LLM call routed through the host's ILlmRouter.</summary>
    Llm = 1,

    /// <summary>A compiled C# function (Roslyn) — pure compute plus the web channel.</summary>
    CSharp = 2,

    /// <summary>A tool exposed by an MCP server.</summary>
    Mcp = 3,

    /// <summary>A capability implemented in host code and registered by id.</summary>
    Builtin = 4,
}

/// <summary>How much a service can affect the world — used for audit and (optionally) gating.</summary>
public enum ServiceSideEffect
{
    ReadOnly = 0,
    WritesLocal = 1,
    ExternalSend = 2,
    Destructive = 3,
}

/// <summary>
/// A self-describing, credentialed capability in the catalog. The
/// <see cref="Description"/> and <see cref="WhenToUse"/> are what a user or agent
/// reads to pick it; <see cref="Config"/> tells the kind-adapter how to invoke it;
/// <see cref="CredentialRef"/> names a secret resolved at call time (the secret
/// value itself is never stored here and never shown to a model).
/// </summary>
public sealed class ServiceDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Name { get; set; } = string.Empty;
    public ServiceKind Kind { get; set; } = ServiceKind.Http;

    /// <summary>What the service does.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>When this is the best option to reach for (read by the selector/agent).</summary>
    public string WhenToUse { get; set; } = string.Empty;

    public List<string> Tags { get; set; } = new();

    /// <summary>Kind-specific configuration. Never holds secret values (use <see cref="CredentialRef"/>).</summary>
    public Dictionary<string, string> Config { get; set; } = new();

    /// <summary>Name of the secret (in the credential store) this service authenticates with, if any.</summary>
    public string? CredentialRef { get; set; }

    public ServiceSideEffect SideEffect { get; set; } = ServiceSideEffect.ReadOnly;

    public bool Enabled { get; set; } = true;

    /// <summary>"user" or "agent" — provenance, for audit and trust.</summary>
    public string CreatedBy { get; set; } = "user";

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Version currently copied into <see cref="Config"/> and used for calls.
    /// Zero on definitions created before versioning; the catalog materializes
    /// their existing config as revision 1 on first save/inspection.
    /// </summary>
    public int ActiveRevision { get; set; }

    /// <summary>
    /// Immutable source/config history for safe test-before-activation repair.
    /// Credentials remain references on the parent definition and never enter
    /// revision config.
    /// </summary>
    public List<ServiceRevision> Revisions { get; set; } = new();

    public DateTime? LastCalledUtc { get; set; }
    public bool? LastCallSucceeded { get; set; }
    public string? LastCallError { get; set; }
    public int ConsecutiveFailures { get; set; }

    /// <summary>A model-safe view: identity + description + non-secret config. Never includes credentials.</summary>
    public string DescribeForModel()
    {
        var tags = Tags.Count > 0 ? $" [{string.Join(", ", Tags)}]" : "";
        var auth = string.IsNullOrEmpty(CredentialRef) ? "" : " (authenticated)";
        return $"{Name} ({Kind}){auth}{tags}: {Description}. When to use: {WhenToUse}".Trim();
    }
}

public enum ServiceRevisionState
{
    Candidate = 0,
    Active = 1,
    Superseded = 2,
    Rejected = 3,
}

/// <summary>One immutable service configuration revision and its test receipt.</summary>
public sealed class ServiceRevision
{
    public int Version { get; set; }
    public Dictionary<string, string> Config { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public ServiceRevisionState State { get; set; } = ServiceRevisionState.Candidate;
    public string CreatedBy { get; set; } = "agent";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string? ChangeReason { get; set; }
    public DateTime? TestedUtc { get; set; }
    public bool? TestPassed { get; set; }
    public string? TestSummary { get; set; }
    public DateTime? ActivatedUtc { get; set; }
}

/// <summary>The outcome of invoking a service.</summary>
public sealed record ServiceCallResult(bool Ok, string? Output, string? Error)
{
    public static ServiceCallResult Success(string? output) => new(true, output, null);
    public static ServiceCallResult Fail(string error) => new(false, null, error);
}
