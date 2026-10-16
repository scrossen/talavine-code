using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SwarmCore.Knowledge;

/// <summary>
/// Opaque host-owned partition for a knowledge graph. A partition can represent
/// a desktop profile account, a CRM tenant, or another isolation boundary
/// without teaching the shared engine about the host's database key type.
/// </summary>
public sealed record KnowledgePartition
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public string Kind { get; }
    public string Id { get; }

    public KnowledgePartition(string kind, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var normalizedKind = kind.Trim().ToLowerInvariant();
        var normalizedId = id.Trim();
        KnowledgeContractValidation.RequireBounded(
            normalizedKind,
            KnowledgeContractLimits.PartitionKindLength,
            nameof(kind));
        KnowledgeContractValidation.RequireBounded(
            normalizedId,
            KnowledgeContractLimits.PartitionIdLength,
            nameof(id));
        if (normalizedKind.Any(char.IsControl) || normalizedId.Any(char.IsControl))
            throw new ArgumentException("Knowledge partition keys cannot contain control characters.");
        ValidateStrictUtf8(normalizedKind, nameof(kind));
        ValidateStrictUtf8(normalizedId, nameof(id));

        Kind = normalizedKind;
        Id = normalizedId;
    }

    public static KnowledgePartition Create(string kind, string id) => new(kind, id);

    public string StableKey => KnowledgeStableKey.Encode(Kind, Id);

    /// <summary>
    /// Frozen v1 binary identity used by hosts in composite keys. Length-prefixing strict UTF-8
    /// components prevents delimiter and malformed-Unicode collisions.
    /// </summary>
    public byte[] IdentityHash()
    {
        using var stream = new MemoryStream();
        WriteLengthPrefixed(stream, "swarmcore-knowledge-partition-v1");
        WriteLengthPrefixed(stream, Kind);
        WriteLengthPrefixed(stream, Id);
        return SHA256.HashData(stream.ToArray());
    }

    private static void WriteLengthPrefixed(Stream stream, string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        stream.Write(length);
        stream.Write(bytes);
    }

    private static void ValidateStrictUtf8(string value, string parameterName)
    {
        try { _ = StrictUtf8.GetByteCount(value); }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("Knowledge partition keys contain malformed Unicode.", parameterName, exception);
        }
    }
}
