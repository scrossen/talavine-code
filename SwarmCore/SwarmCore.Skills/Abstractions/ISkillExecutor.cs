using SwarmCore.Skills.Models;

namespace SwarmCore.Skills.Abstractions;

/// <summary>
/// Executes skills with appropriate security context.
/// </summary>
public interface ISkillExecutor
{
    /// <summary>
    /// Execute a skill function with security checks.
    /// </summary>
    /// <param name="skillId">The skill to execute.</param>
    /// <param name="functionName">The function to call.</param>
    /// <param name="context">Execution context.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<SkillResult> ExecuteAsync(
        string skillId,
        string functionName,
        SkillExecutionContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Execute a skill function in sandboxed mode. Runs the same enabled → function →
    /// security-policy → consent pipeline as <see cref="ExecuteAsync"/>; the only
    /// difference is that the context handed to the skill is narrowed per
    /// <see cref="SandboxOptions"/>.
    /// </summary>
    /// <param name="skillId">The skill to execute.</param>
    /// <param name="functionName">The function to call.</param>
    /// <param name="context">Execution context.</param>
    /// <param name="options">Sandbox options.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<SkillResult> ExecuteSandboxedAsync(
        string skillId,
        string functionName,
        SkillExecutionContext context,
        SandboxOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// Check if a skill function can be executed in the given context.
    /// </summary>
    /// <param name="skillId">The skill ID.</param>
    /// <param name="functionName">The function name.</param>
    /// <param name="context">Execution context.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ExecutionCheckResult> CanExecuteAsync(
        string skillId,
        string functionName,
        SkillExecutionContext context,
        CancellationToken ct = default);
}

/// <summary>
/// Options for sandboxed skill execution.
///
/// <para>
/// <b>What "sandbox" means today.</b> The executor's sandbox is <em>grant filtering</em>:
/// the <see cref="Models.SkillExecutionContext.GrantedPermissions"/> handed to the skill
/// are narrowed to what the skill declared plus what these options allow, and a hard
/// timeout is enforced. It is not process, memory, or network isolation — a skill still
/// runs in-process with the host's ambient OS capabilities. Do not read
/// <see cref="Models.SkillResult.WasSandboxed"/> as evidence of stronger containment.
/// </para>
/// </summary>
public class SandboxOptions
{
    /// <summary>
    /// Maximum execution time. Enforced: the executor stops waiting after this
    /// interval and releases its concurrency slot even if the skill ignores its
    /// cancellation token.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum memory usage in bytes. <b>Reserved / advisory.</b> Not enforced by the
    /// default executor; carried for a future isolation host.
    /// </summary>
    public long? MaxMemoryBytes { get; init; }

    /// <summary>
    /// When true, an ambient <c>network.access</c> grant is retained even if the skill
    /// did not declare it. When false (default), that grant is withheld unless declared.
    /// Governs only the network permission family; it does not open any socket policy.
    /// </summary>
    public bool AllowNetwork { get; init; } = false;

    /// <summary>
    /// When true, an ambient <c>filesystem.write</c> grant is retained even if the
    /// skill did not declare it. When false (default), that grant is withheld unless
    /// declared. Governs only the file-system permission family; it does not restrict
    /// what the process itself can touch on disk.
    /// </summary>
    public bool AllowFileSystem { get; init; } = false;

    /// <summary>
    /// Allowed external domains. <b>Reserved / advisory.</b> Not enforced by the default
    /// executor, which has no network interposition layer.
    /// </summary>
    public List<string> AllowedDomains { get; init; } = [];
}

/// <summary>
/// Result of checking if execution is allowed.
/// </summary>
public class ExecutionCheckResult
{
    /// <summary>
    /// Whether execution is allowed.
    /// </summary>
    public bool IsAllowed { get; init; }

    /// <summary>
    /// Reason if not allowed.
    /// </summary>
    public string? DenialReason { get; init; }

    /// <summary>
    /// Whether sandbox is required.
    /// </summary>
    public bool RequiresSandbox { get; init; }

    /// <summary>
    /// Missing permissions that are required.
    /// </summary>
    public List<SkillPermission> MissingPermissions { get; init; } = [];

    /// <summary>
    /// Warnings about the execution.
    /// </summary>
    public List<string> Warnings { get; init; } = [];

    public static ExecutionCheckResult Allowed(bool requiresSandbox = false) => new()
    {
        IsAllowed = true,
        RequiresSandbox = requiresSandbox
    };

    public static ExecutionCheckResult Denied(string reason) => new()
    {
        IsAllowed = false,
        DenialReason = reason
    };
}
