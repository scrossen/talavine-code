using System.Text;

namespace SwarmCore.Knowledge;

public static class KnowledgeContract
{
    // v6: commitment, decision and event nodes are scoped (counterparty or thread) instead of
    // keyed by their statement text alone.
    public const int CurrentProjectionVersion = 6;
    // Reopen previously complete ledgers so every account re-projects onto the current
    // projection, including accounts with no incremental backlog.
    public const int CurrentLedgerVersion = 4;
}

public static class KnowledgeContractLimits
{
    public const int PartitionKindLength = 64;
    public const int PartitionIdLength = 1024;
    public const int SourceKindLength = 64;
    public const int SourceStableIdLength = 1024;
    public const int SourceInstanceIdLength = 1024;
    public const int SourceTitleLength = 512;
    public const int SourceUriLength = 2048;
    public const int SourceExcerptLength = 2000;
    public const int EntityCanonicalKeyLength = 512;
    public const int EntityNameLength = 512;
    public const int EntityDescriptionLength = 4096;
    public const int EntityAliasesJsonLength = 8192;
    public const int RelationPredicateLength = 160;
    public const int EntitiesPerBundle = 4096;
    public const int RelationsPerBundle = 16384;
    public const int EvidenceLocatorLength = 1024;
    public const int EvidenceAssessorLength = 512;
    public const int ArtifactCanonicalKeyLength = 512;
    public const int ArtifactTitleLength = 512;
    public const int ArtifactStatementLength = 8192;
    public const int ArtifactDomainLength = 160;
    public const int ArtifactActorKeyLength = 512;
    public const int ArtifactAuthorityScopeLength = 512;
    public const int EvidenceLinksPerArtifact = 256;
    public const int ApprovalsPerArtifact = 64;
    public const int RelationsPerArtifact = 256;
    public const int ArtifactSearchTextLength = 1024;
    public const int ArtifactQueryLimit = 500;
    public const int ApprovalKeyLength = 512;
    public const int ArtifactFacetTextLength = 4096;
    public const int ArtifactFacetItemLength = 1024;
    public const int ArtifactFacetListItems = 128;
    public const int ArtifactAlternatives = 64;
    public const int ArtifactUpdates = 256;
}

internal static class KnowledgeContractValidation
{
    public static string RequiredTrimmed(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A non-empty value is required.", parameterName);
        return value.Trim();
    }

    public static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static void RequireBounded(string value, int maximumLength, string parameterName)
    {
        if (value.Length > maximumLength)
            throw new ArgumentException($"Value cannot exceed {maximumLength} characters.", parameterName);
    }

    public static void RequireOptionalBounded(string? value, int maximumLength, string parameterName)
    {
        if (value is not null)
            RequireBounded(value, maximumLength, parameterName);
    }

    public static void RequireSha256(string value, string parameterName)
    {
        if (value.Length != 64 || value.Any(static character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("A 64-character hexadecimal SHA-256 is required.", parameterName);
    }

    public static void RequireDefined<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
            throw new ArgumentOutOfRangeException(parameterName, value, "Undefined enum value.");
    }

    public static void RequireTimestamp(DateTimeOffset value, string parameterName)
    {
        if (value == default)
            throw new ArgumentException("A non-default timestamp is required.", parameterName);
    }
}

internal static class KnowledgeStableKey
{
    /// <summary>Length-prefixes every UTF-8 component so embedded delimiters cannot collide.</summary>
    public static string Encode(params string[] components)
    {
        var builder = new StringBuilder();
        foreach (var component in components)
        {
            var byteCount = Encoding.UTF8.GetByteCount(component);
            builder.Append(byteCount).Append(':').Append(component);
        }

        return builder.ToString();
    }
}
