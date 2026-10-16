using System.Security.Cryptography;
using System.Text;

namespace SwarmCore.Abstractions.Knowledge;

/// <summary>
/// Well-known provenance GUIDs for knowledge a supervised agent learned itself.
///
/// <para>
/// Plays the role the notes anchor plays for note-mined
/// knowledge and the documents anchor plays for
/// document-mined knowledge: the anchor is stamped into
/// <c>ContextFact.SourceAccountId</c> / <c>KnowledgeEntity.AccountId</c> so an
/// agent's learned facts live in their own partition with zero schema change,
/// recallable via <c>ContextPolicy.AllowedSourceAccountIds</c> and purgeable
/// via <c>ForgetBySourceAccountIdAsync</c> when the agent is decommissioned.
/// </para>
///
/// <para>
/// The anchor is derived deterministically from the agent's email so every
/// device converges on the same partition without coordination. The first four
/// bytes are a fixed marker, so agent partitions are recognizable
/// (<see cref="IsAgentAnchor"/>) without a lookup table — the context engine
/// uses this to keep agent-learned facts OUT of the owner's default context
/// packs: they only surface in packs that explicitly allowlist that agent's
/// anchor.
/// </para>
/// </summary>
public static class AgentKnowledgeAnchors
{
    /// <summary>Fixed marker occupying the first GUID field of every agent anchor.</summary>
    private const uint Marker = 0xa93e4a7c;

    /// <summary>
    /// The marker as it appears in the canonical "D"-format GUID string — the
    /// first 8 hex digits. Stores that persist GUIDs as TEXT can recognize an
    /// agent anchor in SQL with a case-insensitive prefix match on this value
    /// (some historical writers stored upper-case GUID text), mirroring
    /// <see cref="IsAgentAnchor"/>.
    /// </summary>
    public const string MarkerTextPrefix = "a93e4a7c";

    private const string HashPrefix = "agent-knowledge-anchor:";

    /// <summary>
    /// Deterministic anchor GUID for one agent, keyed by its (case-insensitive)
    /// email address.
    /// </summary>
    public static Guid ForAgent(string agentEmail)
    {
        if (string.IsNullOrWhiteSpace(agentEmail))
            throw new ArgumentException("Agent email is required to derive a knowledge anchor.", nameof(agentEmail));

        var normalized = agentEmail.Trim().ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(HashPrefix + normalized));
        return new Guid(
            unchecked((int)Marker),
            BitConverter.ToInt16(hash, 0),
            BitConverter.ToInt16(hash, 2),
            hash[4], hash[5], hash[6], hash[7],
            hash[8], hash[9], hash[10], hash[11]);
    }

    /// <summary>True when <paramref name="id"/> is an agent knowledge anchor.</summary>
    public static bool IsAgentAnchor(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        return id.TryWriteBytes(bytes) && BitConverter.ToUInt32(bytes) == Marker;
    }
}
