using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SwarmCore.Agent.WorkspaceData;

/// <summary>Owner-reviewed native principal mapping, never supplied by an agent tool.</summary>
public sealed record AgentWorkspaceParticipantBinding(string AgentId, string NativePrincipalId,
    string ManualSha256, string RoleSha256, string CapabilitySha256,
    bool CanRead, bool CanWrite, long Version, bool IsRetired = false);

/// <summary>Shared bounded enrollment/CAS rules; hosts authenticate operators and own persistence.</summary>
public static class AgentWorkspaceEnrollmentPolicy
{
    private static readonly JsonSerializerOptions RosterJson = new()
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 };
    public const int MaximumParticipants = 64;
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static Guid ScopeId(string application, string tenant, string runBindingSha256)
    {
        Identity(application); Identity(tenant); Fingerprint(runBindingSha256);
        runBindingSha256 = runBindingSha256.ToUpperInvariant();
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { application, tenant, runBindingSha256 })))[..16]);
    }
    public static void Identity(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl))
            throw new AgentWorkspaceDatabaseValidationException("Enrollment identity is invalid.");
    }
    public static void Fingerprint(string value)
    {
        if (value is not { Length: 64 } || value.Any(c => !Uri.IsHexDigit(c)))
            throw new AgentWorkspaceDatabaseValidationException("Enrollment requires an exact SHA256 fingerprint.");
    }
    public static IReadOnlyList<AgentWorkspaceParticipantBinding> Freeze(IEnumerable<AgentWorkspaceParticipantBinding> participants)
    {
        ArgumentNullException.ThrowIfNull(participants);
        var bounded = participants.Take(MaximumParticipants + 1).ToArray();
        if (bounded.Length > MaximumParticipants || bounded.Any(p => p is null))
            throw new AgentWorkspaceDatabaseValidationException("Enrollment participants exceed the bound or contain null entries.");
        var frozen = bounded.Select(p => p with { }).OrderBy(p => p.AgentId, StringComparer.Ordinal).ToArray();
        if (frozen.Length is < 1 or > MaximumParticipants || frozen.GroupBy(p => p.AgentId, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() != 1)
            || frozen.GroupBy(p => p.NativePrincipalId, StringComparer.Ordinal).Any(g => g.Count() != 1))
            throw new AgentWorkspaceDatabaseValidationException("Enrollment participants must be explicit, bounded and unique.");
        foreach (var p in frozen)
        {
            Identity(p.AgentId); Identity(p.NativePrincipalId); Fingerprint(p.ManualSha256); Fingerprint(p.RoleSha256); Fingerprint(p.CapabilitySha256);
            if (p.Version <= 0 || p.CanWrite && !p.CanRead || p.IsRetired && (p.CanRead || p.CanWrite))
                throw new AgentWorkspaceDatabaseValidationException("Enrollment participant permissions/version are invalid.");
        }
        return frozen;
    }
    public static string CanonicalRoster(IEnumerable<AgentWorkspaceParticipantBinding> participants)
        => JsonSerializer.Serialize(Freeze(participants));
    public static IReadOnlyList<AgentWorkspaceParticipantBinding> ReadRoster(string json, string sha256)
    {
        if (Encoding.UTF8.GetByteCount(json) > 65536 || Hash(json) != sha256) throw new AgentWorkspaceDatabaseConflictException("Enrollment roster integrity changed.");
        var parsed = JsonSerializer.Deserialize<AgentWorkspaceParticipantBinding[]>(json, RosterJson)
            ?? throw new AgentWorkspaceDatabaseConflictException("Enrollment roster is missing.");
        var frozen = Freeze(parsed);
        if (CanonicalRoster(frozen) != json) throw new AgentWorkspaceDatabaseConflictException("Enrollment roster is not canonical.");
        return frozen;
    }
    public static void RequireVersion(long current, long expected)
    {
        if (current <= 0 || expected != current) throw new AgentWorkspaceDatabaseConflictException("Enrollment version changed; read back before a new command.");
    }
}
