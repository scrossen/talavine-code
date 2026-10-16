using Microsoft.Extensions.Options;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;
using SwarmCore.Agent.ToolUseEngine;
using SwarmCore.Coding.Prompting;

namespace SwarmCore.Coding;

/// <summary>
/// Single coding profile built from <see cref="CodingOptions"/>; tools come from
/// the capability catalog filtered by the configured enabled ids (empty = all).
/// </summary>
public sealed class CodingProfileResolver(ICapabilityCatalog catalog, IOptions<CodingOptions> options, Permissions.ActiveCodingPermissions? activePermissions = null) : IProfileResolver
{
    public Task<ResolvedProfile> ResolveAsync(string conversationId, Guid? explicitProfileId = null, CancellationToken ct = default)
    {
        var o = options.Value;
        // The running turn's policy (the host's merged user + repository rules) outranks the configured rules; a turn without
        // a policy, or a resolve outside a turn, sees the configured Coding:Permissions.
        var deny = activePermissions?.Get(conversationId)?.Deny ?? o.Permissions.Deny;
        var snapshot = new AgentProfileSnapshot
        {
            Id = o.ProfileId,
            Name = o.ProfileName,
            SystemPrompt = CodingSystemPrompt.Build(o),
            EnabledToolIds = o.EnabledToolIds,
            MaxIterations = o.MaxIterations,
            HardTimeout = o.HardTimeout,
        };
        IReadOnlyList<ToolDescriptor> tools = catalog.All();
        if (o.EnabledToolIds.Count > 0)
        {
            var enabled = new HashSet<string>(o.EnabledToolIds, StringComparer.OrdinalIgnoreCase);
            tools = tools.Where(t => enabled.Contains(t.ToolId)).ToList();
        }
        // The engine consults the permission guard only for calls with a side effect, so a denied tool that is (or may
        // resolve to) read-only is not offered at all; other denied tools stay visible and the guard refuses them with a reason.
        if (deny.Count > 0)
        {
            tools = tools.Where(t => (t.SideEffect != ToolSideEffect.ReadOnly && t.ResolveSideEffect is null)
                || !deny.Any(p => Permissions.CodingPermissionPolicy.Matches(p, t.ToolId))).ToList();
            snapshot = snapshot with
            {
                SystemPrompt = snapshot.SystemPrompt + "\n\nThe person turned these tools off (Coding:Permissions:Deny): "
                    + string.Join(", ", deny.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()))
                    + ". Do not try to reach the same effect another way; work without them or say what you need.",
            };
        }
        return Task.FromResult(new ResolvedProfile(snapshot, tools));
    }
}
