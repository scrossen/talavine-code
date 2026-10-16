using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;

namespace SwarmCore.Coding.Permissions;

/// <summary>How much a person wants to approve before a coding turn's tools act.</summary>
public enum CodingPermissionMode
{
    /// <summary>Everything the request needs runs without a prompt (a direct request authorizes its own tools).</summary>
    Auto,
    /// <summary>
    /// Reads and local workspace edits run; shell and code execution, external sends, destructive actions, git commit/push
    /// and computer/browser control ask first.
    /// </summary>
    AskRisky,
    /// <summary>Every tool with a side effect asks first.</summary>
    Ask,
}

/// <summary>The person's standing rules (<c>Coding:Permissions</c>): tool-id globs, <c>*</c> matching any run of characters.</summary>
public sealed class CodingPermissionRules
{
    public const string SectionName = "Coding:Permissions";

    /// <summary>Tools that run without a prompt in any mode (unless also denied).</summary>
    public List<string> Allow { get; set; } = [];

    /// <summary>Tools that never run. Deny always wins over allow and over the mode.</summary>
    public List<string> Deny { get; set; } = [];
}

public enum CodingPermissionVerdict { Run, Ask, Deny }

public sealed record CodingPermissionDecision(CodingPermissionVerdict Verdict, string Reason)
{
    public bool Runs => Verdict == CodingPermissionVerdict.Run;
}

/// <summary>
/// One request's permission policy: the person's mode plus allow and deny rules. Host-neutral: the host chooses the
/// mode and rules and puts the policy on <see cref="Routing.CodingTurnRequest.Permissions"/>; the harness applies it
/// through <see cref="CodingPermissionGuard"/>. A policy only narrows what the turn's authority allows; it never widens it.
/// </summary>
public sealed record CodingPermissionPolicy
{
    public static CodingPermissionPolicy AutoNoRules { get; } = new();

    public CodingPermissionMode Mode { get; init; } = CodingPermissionMode.Auto;
    public IReadOnlyList<string> Allow { get; init; } = [];
    public IReadOnlyList<string> Deny { get; init; } = [];

    public static CodingPermissionPolicy From(CodingPermissionMode mode, CodingPermissionRules? rules) => new()
    {
        Mode = mode,
        Allow = Clean(rules?.Allow),
        Deny = Clean(rules?.Deny),
    };

    /// <summary>
    /// True when some tool may ask or be refused. Native lanes run their own tools outside the host's broker and guard,
    /// so a host does not use them for such a policy (see <see cref="NativeLaneBlock"/>).
    /// </summary>
    public bool Restricts => Mode != CodingPermissionMode.Auto || Deny.Count > 0;

    /// <summary>Why native lanes are not used under this policy, or null when they may run.</summary>
    public string? NativeLaneBlock => Mode != CodingPermissionMode.Auto
        ? "native lanes run their own tools without approval prompts, so they are not used while the permission mode asks before actions"
        : Deny.Count > 0
            ? "native lanes run their own tools, which the deny rules in Coding:Permissions cannot hold, so they are not used while deny rules exist"
            : null;

    /// <summary>The verdict for one call of <paramref name="toolId"/> whose resolved side effect is <paramref name="sideEffect"/>.</summary>
    public CodingPermissionDecision Decide(string toolId, ToolSideEffect sideEffect)
    {
        if (FirstMatch(Deny, toolId) is { } denied)
            return new(CodingPermissionVerdict.Deny,
                $"{toolId} is refused by the person's permission rules (Coding:Permissions:Deny \"{denied}\"). Do not retry it or work around it with another tool; "
                + "continue without it or tell the person what you need.");
        if (sideEffect == ToolSideEffect.ReadOnly) return new(CodingPermissionVerdict.Run, "read-only");
        if (FirstMatch(Allow, toolId) is { } allowed) return new(CodingPermissionVerdict.Run, $"allowed by \"{allowed}\"");
        return Mode switch
        {
            CodingPermissionMode.Ask => new(CodingPermissionVerdict.Ask, "the permission mode asks before every action"),
            CodingPermissionMode.AskRisky when IsRisky(toolId, sideEffect) => new(CodingPermissionVerdict.Ask, "the permission mode asks before risky actions"),
            _ => new(CodingPermissionVerdict.Run, "the permission mode runs this action"),
        };
    }

    /// <summary>
    /// What <see cref="CodingPermissionMode.AskRisky"/> asks for: command and code execution, external sends, destructive
    /// actions, git commit/push, and computer/browser control. Local workspace edits are not risky. The agents tools are
    /// not risky either: an agent works under the same policy as the session that started it.
    /// </summary>
    public static bool IsRisky(string toolId, ToolSideEffect sideEffect)
    {
        if (Matches("agents.*", toolId)) return false;
        if (RiskyToolIds.Any(p => Matches(p, toolId))) return true;
        return sideEffect is not (ToolSideEffect.ReadOnly or ToolSideEffect.WritesLocal);
    }

    /// <summary>Tool-id globs that are risky whatever side effect their descriptor declares.</summary>
    public static IReadOnlyList<string> RiskyToolIds { get; } =
        ["shell.*", "git.commit*", "git.push*", "browser.*", "browser_*", "computer.*", "computer_*", "apps.*"];

    /// <summary>Case-insensitive glob over tool ids: <c>*</c> matches any run of characters, everything else is literal.</summary>
    public static bool Matches(string pattern, string toolId)
    {
        if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrEmpty(toolId)) return false;
        var regex = "^" + string.Join(".*", pattern.Trim().Split('*').Select(Regex.Escape)) + "$";
        return Regex.IsMatch(toolId, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }

    private static string? FirstMatch(IReadOnlyList<string> patterns, string toolId)
        => patterns.FirstOrDefault(p => Matches(p, toolId));

    private static IReadOnlyList<string> Clean(IEnumerable<string>? patterns)
        => (patterns ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Stable names used in preferences and settings: auto, ask-risky, ask.</summary>
    public static string Name(CodingPermissionMode mode) => mode switch
    {
        CodingPermissionMode.AskRisky => "ask-risky",
        CodingPermissionMode.Ask => "ask",
        _ => "auto",
    };

    public static bool TryParse(string? text, out CodingPermissionMode mode)
    {
        switch (text?.Trim().ToLowerInvariant().Replace("_", "-"))
        {
            case "auto": mode = CodingPermissionMode.Auto; return true;
            case "ask-risky" or "askrisky" or "risky": mode = CodingPermissionMode.AskRisky; return true;
            case "ask": mode = CodingPermissionMode.Ask; return true;
            default: mode = CodingPermissionMode.Auto; return false;
        }
    }

    /// <summary>The stricter of two modes (an agent never runs with less caution than the session that started it).</summary>
    public static CodingPermissionMode Stricter(CodingPermissionMode a, CodingPermissionMode b) => (CodingPermissionMode)Math.Max((int)a, (int)b);
}

/// <summary>
/// The policy each running coding turn carries, keyed by conversation. The orchestrator records it for the turn so
/// <see cref="CodingPermissionGuard"/> (which sees only the conversation id) applies the request's own policy.
/// </summary>
public sealed class ActiveCodingPermissions
{
    private readonly ConcurrentDictionary<string, CodingPermissionPolicy> _active = new(StringComparer.Ordinal);

    public CodingPermissionPolicy? Get(string? conversationId)
        => conversationId is not null && _active.TryGetValue(conversationId, out var policy) ? policy : null;

    public IDisposable Enter(string conversationId, CodingPermissionPolicy policy)
    {
        _active[conversationId] = policy;
        return new Exit(this, conversationId, policy);
    }

    private sealed class Exit(ActiveCodingPermissions owner, string conversationId, CodingPermissionPolicy policy) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            owner._active.TryRemove(new KeyValuePair<string, CodingPermissionPolicy>(conversationId, policy));
        }
    }
}

/// <summary>
/// Applies the running turn's <see cref="CodingPermissionPolicy"/> at the engine's invocation-guard seam: a denied tool
/// is refused with a reason the model reads, an action the mode asks about is escalated to the host's permission broker
/// (even on a user-requested turn), and everything else passes to the next guard. A turn without a policy is unaffected.
/// The engine consults the guard only for calls with a side effect; denied read-only tools are withheld from the profile
/// (<see cref="CodingProfileResolver"/>).
/// </summary>
public sealed class CodingPermissionGuard(ActiveCodingPermissions active) : IToolInvocationGuard
{
    public Task<ToolGuardDecision> InspectAsync(ToolInvocationRequest request, CancellationToken ct = default)
    {
        if (active.Get(request.ConversationId) is not { } policy) return Task.FromResult(ToolGuardDecision.Allow());
        var decision = policy.Decide(request.ToolId, request.EffectiveSideEffect);
        return Task.FromResult(decision.Verdict switch
        {
            CodingPermissionVerdict.Deny => ToolGuardDecision.Block(decision.Reason),
            CodingPermissionVerdict.Ask => ToolGuardDecision.Escalate(decision.Reason),
            _ => ToolGuardDecision.Allow(),
        });
    }
}
