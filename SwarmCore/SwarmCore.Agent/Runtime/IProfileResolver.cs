using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.ToolUseEngine;

namespace SwarmCore.Agent.Runtime;

/// <summary>
/// Picks the <see cref="AgentProfileSnapshot"/> for a turn and resolves the
/// tool catalog down to the profile's enabled tool ids.
/// </summary>
public interface IProfileResolver
{
    /// <summary>
    /// Resolve the profile that should drive this turn.
    /// <list type="number">
    ///   <item>If <paramref name="explicitProfileId"/> is set, use it.</item>
    ///   <item>Otherwise use the conversation's configured profile.</item>
    ///   <item>Otherwise use the default profile.</item>
    ///   <item>If no default exists, throw — the seeder should have run.</item>
    /// </list>
    /// </summary>
    Task<ResolvedProfile> ResolveAsync(string conversationId, Guid? explicitProfileId = null, CancellationToken ct = default);
}

public sealed record ResolvedProfile(AgentProfileSnapshot Snapshot, IReadOnlyList<ToolDescriptor> Tools);

/// <summary>Optional actual resolver provenance for read-only admission evidence; never replaces execution admission.</summary>
public interface IProfileAdmissionResolver
{
    Task<AdmissionResolvedProfile> ResolveAdmissionAsync(string conversationId, Guid explicitProfileId, CancellationToken ct = default);
}
public sealed record AdmissionResolvedProfile(ResolvedProfile Resolved, bool ExactRequestedProfile,
    bool WorkspaceBindingRequired, bool WorkspaceBindingKnown, bool WorkspaceBound);
