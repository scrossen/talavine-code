using System.Diagnostics;
using SwarmCore.Skills.Abstractions;
using SwarmCore.Skills.Models;
using Microsoft.Extensions.Logging;

namespace SwarmCore.Skills.Core;

/// <summary>
/// Default implementation of ISkillExecutor.
/// </summary>
public class SkillExecutor : ISkillExecutor
{
    private readonly ISkillRegistry _registry;
    private readonly ISkillSecurityPolicy _securityPolicy;
    private readonly ISkillPermissionConsentService _consentService;
    private readonly SkillRegistryConfiguration _configuration;
    private readonly ILogger<SkillExecutor> _logger;
    private readonly SemaphoreSlim _executionThrottle;

    public SkillExecutor(
        ISkillRegistry registry,
        ISkillSecurityPolicy securityPolicy,
        ISkillPermissionConsentService consentService,
        SkillRegistryConfiguration configuration,
        ILogger<SkillExecutor> logger)
    {
        _registry = registry;
        _securityPolicy = securityPolicy;
        _consentService = consentService;
        _configuration = configuration;
        _logger = logger;
        _executionThrottle = new SemaphoreSlim(configuration.MaxConcurrentExecutions);
    }

    public async Task<SkillResult> ExecuteAsync(
        string skillId,
        string functionName,
        SkillExecutionContext context,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        // Whether the call was handed to the sandbox core. A failure raised on that path
        // (a throwing skill, a timeout) must report WasSandboxed the same way a call
        // through ExecuteSandboxedAsync does; the catch blocks below cannot see which
        // branch ran unless we remember it.
        var sandboxed = false;

        try
        {
            var authorization = await AuthorizeAsync(skillId, functionName, context, ct);
            if (authorization.Failure is not null)
            {
                return authorization.Failure;
            }

            // If sandbox is required, use sandboxed execution. Authorization has
            // already passed on this path, so hand off to the core without
            // re-running the policy/consent pipeline (which would otherwise prompt
            // the user a second time for the same call).
            if (authorization.RequiresSandbox)
            {
                sandboxed = true;
                return await ExecuteSandboxedCoreAsync(
                    authorization.Skill, skillId, functionName, authorization.Context, options: null, stopwatch, ct);
            }

            return await RunWithThrottleAsync(
                authorization.Skill,
                skillId,
                functionName,
                authorization.Context,
                _configuration.DefaultExecutionTimeout,
                sandboxed: false,
                stopwatch,
                ct);
        }
        catch (OperationCanceledException)
        {
            return CancellationResult(skillId, functionName, stopwatch, sandboxed, ct);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex,
                "Skill execution failed: {SkillId}.{Function}",
                skillId, functionName);

            return SkillResult.Fail(ex.Message, ex.ToString()) with
            {
                SkillId = skillId,
                FunctionName = functionName,
                ExecutionTime = stopwatch.Elapsed,
                WasSandboxed = sandboxed
            };
        }
    }

    public async Task<SkillResult> ExecuteSandboxedAsync(
        string skillId,
        string functionName,
        SkillExecutionContext context,
        SandboxOptions? options = null,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // The public sandboxed entry point runs exactly the same enabled →
            // function → policy → consent pipeline as ExecuteAsync. Previously it
            // skipped the policy and consent checks entirely, so a caller could
            // reach a skill's elevated capability by choosing this overload.
            var authorization = await AuthorizeAsync(skillId, functionName, context, ct);
            if (authorization.Failure is not null)
            {
                return authorization.Failure with { WasSandboxed = true };
            }

            return await ExecuteSandboxedCoreAsync(
                authorization.Skill, skillId, functionName, authorization.Context, options, stopwatch, ct);
        }
        catch (OperationCanceledException)
        {
            return CancellationResult(skillId, functionName, stopwatch, sandboxed: true, ct);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex,
                "Sandboxed skill execution failed: {SkillId}.{Function}",
                skillId, functionName);

            return SkillResult.Fail(ex.Message, ex.ToString()) with
            {
                SkillId = skillId,
                FunctionName = functionName,
                ExecutionTime = stopwatch.Elapsed,
                WasSandboxed = true
            };
        }
    }

    /// <summary>
    /// Outcome of the shared authorization pipeline. When <see cref="Failure"/> is
    /// non-null the caller must return it; otherwise <see cref="Skill"/> and the
    /// (possibly widened) <see cref="Context"/> are ready to execute.
    /// </summary>
    private sealed record AuthorizationOutcome(
        ISkill Skill,
        SkillExecutionContext Context,
        bool RequiresSandbox,
        SkillResult? Failure)
    {
        public static AuthorizationOutcome Denied(SkillResult failure) =>
            new(null!, null!, false, failure);
    }

    /// <summary>
    /// The single authorization pipeline shared by every public execution entry point:
    /// skill lookup → enabled → function → per-user access → Standard auto-grant →
    /// security policy → consent for missing elevated permissions → re-check.
    /// </summary>
    private async Task<AuthorizationOutcome> AuthorizeAsync(
        string skillId,
        string functionName,
        SkillExecutionContext context,
        CancellationToken ct)
    {
        // Get the skill
        var skill = _registry.GetSkill(skillId);
        if (skill is null)
        {
            return AuthorizationOutcome.Denied(SkillResult.Fail($"Skill not found: {skillId}"));
        }

        // Check if skill is enabled
        if (!_registry.IsEnabled(skillId))
        {
            return AuthorizationOutcome.Denied(SkillResult.Fail($"Skill is disabled: {skillId}"));
        }

        // Check if function exists
        var function = _registry.GetFunction(skillId, functionName);
        if (function is null)
        {
            return AuthorizationOutcome.Denied(SkillResult.Fail($"Function not found: {skillId}.{functionName}"));
        }

        // Per-user access. Only consulted when the caller identifies a user; the
        // default policy answers true, so this adds no prompt to any existing flow.
        if (!string.IsNullOrWhiteSpace(context.UserId)
            && !await _securityPolicy.CanUserAccessSkillAsync(skill, context.UserId, ct))
        {
            return AuthorizationOutcome.Denied(
                SkillResult.Fail($"Security check failed: user {context.UserId} may not use skill {skillId}"));
        }

        context = WithStandardPermissionsGranted(skill, context);

        // Security check
        var securityCheck = await _securityPolicy.CheckExecutionAllowedAsync(skill, context, ct);

        // If elevated permissions are missing, request user consent via the UI
        if (!securityCheck.IsAllowed && securityCheck.MissingPermissions.Count > 0)
        {
            var consentRequest = new PermissionConsentRequest
            {
                SkillId = skillId,
                SkillName = skill.Name,
                FunctionName = functionName,
                SessionId = context.SessionId,
                MissingPermissions = securityCheck.MissingPermissions
            };

            var consent = await _consentService.RequestConsentAsync(consentRequest, ct);
            if (consent.Approved)
            {
                // Re-build context with the newly granted permissions
                context = WithPermissions(
                    context,
                    context.GrantedPermissions.Concat(securityCheck.MissingPermissions.Select(p => p.PermissionId)));

                // Re-run security check — should pass now
                securityCheck = await _securityPolicy.CheckExecutionAllowedAsync(skill, context, ct);
            }
        }

        if (!securityCheck.IsAllowed)
        {
            return AuthorizationOutcome.Denied(
                SkillResult.Fail($"Security check failed: {securityCheck.DenialReason}"));
        }

        return new AuthorizationOutcome(skill, context, securityCheck.RequiresSandbox, Failure: null);
    }

    /// <summary>
    /// Auto-grant the Standard-level permissions the skill requires. Standard permissions are
    /// "generally safe operations" and do not need explicit grants from internal callers;
    /// Elevated+ permissions still require the caller (or consent) to include them. Shared by
    /// execution and <see cref="CanExecuteAsync"/> so the pre-check answers what execution does.
    /// </summary>
    private static SkillExecutionContext WithStandardPermissionsGranted(ISkill skill, SkillExecutionContext context)
    {
        var missingStandard = skill.RequiredPermissions
            .Where(p => p.Level == PermissionLevel.Standard && !context.HasPermission(p))
            .Select(p => p.PermissionId)
            .ToList();
        return missingStandard.Count == 0
            ? context
            : WithPermissions(context, context.GrantedPermissions.Concat(missingStandard));
    }

    private static SkillExecutionContext WithPermissions(
        SkillExecutionContext context,
        IEnumerable<string> permissions) => new()
    {
        UserId = context.UserId,
        SessionId = context.SessionId,
        Parameters = context.Parameters,
        GrantedPermissions = permissions.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        AllowSandboxEscape = context.AllowSandboxEscape,
        CorrelationId = context.CorrelationId,
        Metadata = context.Metadata,
        CancellationToken = context.CancellationToken
    };

    /// <summary>
    /// Sandboxed execution for an already-authorized call. "Sandbox" here means grant
    /// filtering (see <see cref="SandboxOptions"/>): the context handed to the skill is
    /// narrowed, not the process it runs in.
    /// </summary>
    private async Task<SkillResult> ExecuteSandboxedCoreAsync(
        ISkill skill,
        string skillId,
        string functionName,
        SkillExecutionContext context,
        SandboxOptions? options,
        Stopwatch stopwatch,
        CancellationToken ct)
    {
        options ??= new SandboxOptions();

        // Create sandboxed context. The sandbox exists to deny AMBIENT
        // capability, not to revoke a permission the user explicitly
        // granted: network/file-write are Restricted+, so their presence
        // in GrantedPermissions means the consent flow already approved
        // them for a skill that declared the need. Honor those grants;
        // only strip a network/write capability the skill never declared
        // as required (a stray ambient grant), and surface that strip so
        // the failure is never a silent, inscrutable capability error.
        var declaredPermissionIds = skill.RequiredPermissions
            .Select(p => p.PermissionId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Each option flag governs only its own permission family. AllowNetwork
        // used to short-circuit the whole filter, so enabling it also retained an
        // undeclared filesystem.write grant.
        bool ShouldRetain(string permissionId)
        {
            if (permissionId.Equals(SkillPermission.NetworkAccess.PermissionId, StringComparison.OrdinalIgnoreCase))
            {
                return options.AllowNetwork || declaredPermissionIds.Contains(permissionId);
            }

            if (permissionId.Equals(SkillPermission.FileSystemWrite.PermissionId, StringComparison.OrdinalIgnoreCase))
            {
                return options.AllowFileSystem || declaredPermissionIds.Contains(permissionId);
            }

            // Retain every other grant (Standard reads, and elevated grants that
            // went through review + consent on the way in).
            return true;
        }

        var sandboxedPermissions = context.GrantedPermissions.Where(ShouldRetain).ToList();
        var strippedPermissions = context.GrantedPermissions
            .Where(p => !ShouldRetain(p))
            .ToList();
        if (strippedPermissions.Count > 0)
        {
            _logger.LogWarning(
                "Sandbox for skill {SkillId} withheld undeclared ambient permissions: {Permissions}",
                skillId,
                string.Join(", ", strippedPermissions));
        }

        var sandboxedContext = new SkillExecutionContext
        {
            UserId = context.UserId,
            SessionId = context.SessionId,
            Parameters = context.Parameters,
            GrantedPermissions = sandboxedPermissions,
            AllowSandboxEscape = false,
            CorrelationId = context.CorrelationId,
            Metadata = context.Metadata,
            CancellationToken = ct
        };

        return await RunWithThrottleAsync(
            skill, skillId, functionName, sandboxedContext, options.Timeout, sandboxed: true, stopwatch, ct);
    }

    /// <summary>
    /// Take a throttle slot, run the skill under a hard timeout, and always give the slot
    /// back. The timeout is enforced with <see cref="Task.WaitAsync(CancellationToken)"/>
    /// rather than trusting the skill to observe its token: a skill (or the LLM call
    /// under it) that ignores cancellation used to hold a slot forever, and
    /// <see cref="SkillRegistryConfiguration.MaxConcurrentExecutions"/> hung skills
    /// blocked every skill in the process.
    /// </summary>
    private async Task<SkillResult> RunWithThrottleAsync(
        ISkill skill,
        string skillId,
        string functionName,
        SkillExecutionContext context,
        TimeSpan timeout,
        bool sandboxed,
        Stopwatch stopwatch,
        CancellationToken ct)
    {
        await _executionThrottle.WaitAsync(ct);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            if (sandboxed)
            {
                _logger.LogInformation(
                    "Executing skill {SkillId}.{Function} in sandbox for user {UserId}",
                    skillId, functionName, context.UserId ?? "anonymous");
            }
            else if (_configuration.LogExecutions)
            {
                _logger.LogInformation(
                    "Executing skill {SkillId}.{Function} for user {UserId}",
                    skillId, functionName, context.UserId ?? "anonymous");
            }

            var execution = skill.ExecuteAsync(functionName, context, timeoutCts.Token);
            SkillResult result;
            try
            {
                result = await execution.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!execution.IsCompleted)
            {
                // The skill is still running after we gave up on it. Observe its
                // eventual fault so it does not surface as an unobserved-task
                // exception, then report the timeout/cancellation to the caller.
                _ = execution.ContinueWith(
                    static t => _ = t.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                throw;
            }

            stopwatch.Stop();

            return WrapUntrusted(result, skill) with
            {
                SkillId = skillId,
                FunctionName = functionName,
                ExecutionTime = stopwatch.Elapsed,
                WasSandboxed = sandboxed
            };
        }
        finally
        {
            _executionThrottle.Release();
        }
    }

    /// <summary>
    /// Distinguish the caller giving up from the executor's own timeout. Both arrive as
    /// <see cref="OperationCanceledException"/>, but only the latter is a "timed out".
    /// </summary>
    private SkillResult CancellationResult(
        string skillId,
        string functionName,
        Stopwatch stopwatch,
        bool sandboxed,
        CancellationToken callerToken)
    {
        stopwatch.Stop();

        if (callerToken.IsCancellationRequested)
        {
            _logger.LogInformation(
                "Skill execution cancelled by caller: {SkillId}.{Function} after {Elapsed}ms",
                skillId, functionName, stopwatch.ElapsedMilliseconds);

            return SkillResult.Fail(sandboxed ? "Sandboxed execution cancelled" : "Execution cancelled") with
            {
                SkillId = skillId,
                FunctionName = functionName,
                ExecutionTime = stopwatch.Elapsed,
                WasSandboxed = sandboxed
            };
        }

        _logger.LogWarning(
            "Skill execution timed out: {SkillId}.{Function} after {Elapsed}ms",
            skillId, functionName, stopwatch.ElapsedMilliseconds);

        return SkillResult.Fail(sandboxed ? "Sandboxed execution timed out" : "Execution timed out") with
        {
            SkillId = skillId,
            FunctionName = functionName,
            ExecutionTime = stopwatch.Elapsed,
            WasSandboxed = sandboxed
        };
    }

    // Marks results from below-Verified skills as untrusted data and, if the payload is a
    // string (the common file-based-skill case), wraps it in a <tool_output> delimiter so
    // orchestrators that reflect skill output back into another LLM context have a clear
    // boundary. See OWASP Agentic Skills Top 10 AST01 and Simon Willison's "lethal trifecta."
    private static SkillResult WrapUntrusted(SkillResult result, ISkill skill)
    {
        if (!result.Success) return result;
        if (skill.TrustLevel >= SkillTrustLevel.Verified) return result;

        var wrapped = result.Result;
        if (wrapped is string text)
        {
            wrapped = $"<tool_output skill=\"{skill.SkillId}\" trust=\"{skill.TrustLevel}\">\n{NeutralizeToolOutputDelimiters(text)}\n</tool_output>";
        }

        return result with { Result = wrapped, IsUntrusted = true };
    }

    /// <summary>
    /// Escapes literal <c>&lt;tool_output</c> / <c>&lt;/tool_output</c> sequences inside an
    /// untrusted payload so the payload cannot close the wrapper early or forge a
    /// second, higher-trust block.
    /// </summary>
    internal static string NeutralizeToolOutputDelimiters(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("tool_output", StringComparison.OrdinalIgnoreCase))
            return text;

        return ToolOutputDelimiterPattern.Replace(text, m => "&lt;" + m.Value[1..]);
    }

    private static readonly System.Text.RegularExpressions.Regex ToolOutputDelimiterPattern = new(
        @"<\s*/?\s*tool_output\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    public async Task<ExecutionCheckResult> CanExecuteAsync(
        string skillId,
        string functionName,
        SkillExecutionContext context,
        CancellationToken ct = default)
    {
        // Get the skill
        var skill = _registry.GetSkill(skillId);
        if (skill is null)
        {
            return ExecutionCheckResult.Denied($"Skill not found: {skillId}");
        }

        // Check if skill is enabled
        if (!_registry.IsEnabled(skillId))
        {
            return ExecutionCheckResult.Denied($"Skill is disabled: {skillId}");
        }

        // Check if function exists
        var function = _registry.GetFunction(skillId, functionName);
        if (function is null)
        {
            return ExecutionCheckResult.Denied($"Function not found: {skillId}.{functionName}");
        }

        if (!string.IsNullOrWhiteSpace(context.UserId)
            && !await _securityPolicy.CanUserAccessSkillAsync(skill, context.UserId, ct))
        {
            return ExecutionCheckResult.Denied($"User {context.UserId} may not use skill {skillId}");
        }

        // Security check
        // Judge the same context ExecuteAsync would run with, or a pre-check reports
        // a Standard permission as missing for a call that runs without prompting.
        var securityCheck = await _securityPolicy.CheckExecutionAllowedAsync(
            skill, WithStandardPermissionsGranted(skill, context), ct);
        if (!securityCheck.IsAllowed)
        {
            return new ExecutionCheckResult
            {
                IsAllowed = false,
                DenialReason = securityCheck.DenialReason,
                MissingPermissions = securityCheck.MissingPermissions,
                Warnings = securityCheck.Warnings
            };
        }

        return ExecutionCheckResult.Allowed(securityCheck.RequiresSandbox);
    }
}
