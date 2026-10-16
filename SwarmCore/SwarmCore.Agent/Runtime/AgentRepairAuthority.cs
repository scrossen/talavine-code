using SwarmCore.Agent.Catalog;

namespace SwarmCore.Agent.Runtime;

/// <summary>
/// Host-granted authority for an unattended turn whose job is to keep the
/// system working: repair a local LLM runtime, pull a missing model, restart a
/// wedged dependency, rewrite a broken routing preference. Under this
/// authority the engine executes side-effecting tools — including
/// <c>shell.exec</c> and file writes — without a consent card, because nobody
/// is present to answer one and the alternative is a task that silently
/// stalls until someone comes back to the machine.
/// <para>
/// It is not "approve everything". <see cref="RepairAuthorityPolicy"/> still
/// refuses irreversible actions, anything that reads or sends the user's
/// communications, credential changes, and outbound sends that are not part
/// of the local-dependency repair surface; those fail fast with an
/// explanation the model can relay, instead of hanging. Only construct this
/// from caller-owned host state, never from model output.
/// </para>
/// </summary>
public sealed class AgentRepairAuthority
{
    private AgentRepairAuthority(
        string authorityId,
        string reason,
        IReadOnlyList<string> allowedExternalSendPrefixes,
        IReadOnlyList<string> deniedToolPrefixes)
    {
        AuthorityId = authorityId;
        Reason = reason;
        AllowedExternalSendPrefixes = allowedExternalSendPrefixes;
        DeniedToolPrefixes = deniedToolPrefixes;
    }

    /// <summary>Who granted this (for example <c>log-storm-supervisor</c>). Sanitized, audit-friendly.</summary>
    public string AuthorityId { get; }

    /// <summary>Short human-readable reason recorded on every approval context.</summary>
    public string Reason { get; }

    /// <summary>
    /// Tool-id prefixes whose <see cref="ToolSideEffect.ExternalSend"/> calls are
    /// part of the repair surface (local routing control, diagnostics). Any
    /// other ExternalSend tool is refused.
    /// </summary>
    public IReadOnlyList<string> AllowedExternalSendPrefixes { get; }

    /// <summary>Tool ids or id prefixes refused regardless of side effect.</summary>
    public IReadOnlyList<string> DeniedToolPrefixes { get; }

    /// <summary>
    /// Authority for a host-initiated repair of the system's own dependencies.
    /// Extra denied prefixes and extra external-send allowances let a host
    /// widen or narrow the default surface without changing policy code.
    /// </summary>
    public static AgentRepairAuthority SystemRepair(
        string authorityId,
        string reason,
        IEnumerable<string>? additionalDeniedToolPrefixes = null,
        IEnumerable<string>? additionalAllowedExternalSendPrefixes = null)
    {
        if (string.IsNullOrWhiteSpace(authorityId))
        {
            throw new ArgumentException("Repair authority id is required.", nameof(authorityId));
        }

        var denied = RepairAuthorityPolicy.DefaultDeniedToolPrefixes
            .Concat(additionalDeniedToolPrefixes ?? [])
            .Where(prefix => !string.IsNullOrWhiteSpace(prefix))
            .Select(prefix => prefix.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var allowedSend = RepairAuthorityPolicy.DefaultAllowedExternalSendPrefixes
            .Concat(additionalAllowedExternalSendPrefixes ?? [])
            .Where(prefix => !string.IsNullOrWhiteSpace(prefix))
            .Select(prefix => prefix.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new AgentRepairAuthority(
            Sanitize(authorityId),
            string.IsNullOrWhiteSpace(reason) ? "system repair" : reason.Trim(),
            allowedSend,
            denied);
    }

    private static string Sanitize(string value)
    {
        var chars = value.Trim()
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ':' or '.' ? c : '-')
            .ToArray();
        var text = new string(chars);
        return text.Length <= 64 ? text : text[..64];
    }
}

public sealed record RepairAuthorityDecision(bool Allowed, string Reason)
{
    public static RepairAuthorityDecision Allow(string reason) => new(true, reason);
    public static RepairAuthorityDecision Deny(string reason) => new(false, reason);
}

/// <summary>
/// The rules behind <see cref="AgentRepairAuthority"/>. Deliberately a small,
/// readable table: what an autonomous repair may do is a product decision and
/// should be reviewable at a glance.
/// </summary>
public static class RepairAuthorityPolicy
{
    /// <summary>
    /// ExternalSend tools that are really local control surfaces: routing
    /// control, diagnostics, provider settings, and the runtime primitives.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultAllowedExternalSendPrefixes =
    [
        "model-scout.",
        "diagnostic.",
        "llm.",
        "shell.",
        "fs.",
        "code.",
        "skills.",
        "catalog.",
    ];

    /// <summary>
    /// Refused under repair authority regardless of declared side effect:
    /// credentials, communications, money, user content, and outbound sends.
    /// Irreversible (<see cref="ToolSideEffect.Destructive"/>) actions and any
    /// tool classified as communication data access are refused by rule, not
    /// by this list.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultDeniedToolPrefixes =
    [
        "llm.connect_provider",
        "llm.disconnect_provider",
        "http.request",
        "backup.",
        "contacts.",
        "calendar.",
        "outbox.",
        "channels.",
        "mcp.remove",
        "mcp.disable",
        "workers.",
        "agents.send_instruction",
        // Answering or dismissing a pending approval is the person's decision.
        "humantasks.resolve",
        "humantasks.dismiss",
        "devblog.",
        "message.",
        "messaging.",
        "shared.email-",
        "shared.user-browser-",
        "shared.vibes-delete-record",
        "spam.",
        "inbox.",
        "attention.",
        "context.forget_fact",
        "servicecatalog.",
        "modelintel.",
        "vibes.delete",
        "templates.delete",
        "roles.delete",
        "agentprofiles.delete",
        "artifact.delete",
        "automations.delete",
        "skills.delete",
    ];

    public static RepairAuthorityDecision Evaluate(
        AgentRepairAuthority authority,
        ToolDescriptor descriptor,
        ToolSideEffect effectiveSideEffect,
        bool guardEscalated,
        string? guardReason)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(descriptor);

        if (guardEscalated)
        {
            return RepairAuthorityDecision.Deny(
                $"the safety guard flagged this call as possibly content-authored ({guardReason ?? "no detail"}), and there is no one present to confirm it");
        }

        var toolId = descriptor.ToolId ?? string.Empty;
        foreach (var prefix in authority.DeniedToolPrefixes)
        {
            if (toolId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return RepairAuthorityDecision.Deny(
                    $"'{toolId}' touches credentials, communications, money, or user content, which an autonomous repair may not change");
            }
        }

        if (descriptor.DataAccess == ToolDataAccessClass.Communication)
        {
            return RepairAuthorityDecision.Deny(
                $"'{toolId}' accesses the user's communications, which an autonomous repair may not read or send");
        }

        switch (effectiveSideEffect)
        {
            case ToolSideEffect.ReadOnly:
                return RepairAuthorityDecision.Allow("read-only");

            case ToolSideEffect.WritesLocal:
                return RepairAuthorityDecision.Allow("reversible local write within repair authority");

            case ToolSideEffect.Exec:
                return RepairAuthorityDecision.Allow("local execution within repair authority");

            case ToolSideEffect.ExternalSend:
                foreach (var prefix in authority.AllowedExternalSendPrefixes)
                {
                    if (toolId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return RepairAuthorityDecision.Allow("local control surface within repair authority");
                    }
                }

                return RepairAuthorityDecision.Deny(
                    $"'{toolId}' sends data outside this machine, which is not part of repairing a local dependency");

            case ToolSideEffect.Destructive:
                return RepairAuthorityDecision.Deny(
                    $"'{toolId}' is irreversible; an autonomous repair may only take reversible actions");

            default:
                return RepairAuthorityDecision.Deny($"'{toolId}' has an unrecognized side-effect class");
        }
    }
}
