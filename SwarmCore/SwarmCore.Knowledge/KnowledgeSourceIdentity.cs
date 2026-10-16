using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SwarmCore.Knowledge;

/// <summary>
/// The communication fields covered by the current contribution input
/// fingerprint. Raw message bodies are deliberately excluded.
/// </summary>
public sealed record KnowledgeCommunicationFingerprintInput(
    string? From,
    string? To,
    string? Cc,
    string? Bcc,
    string? Subject,
    DateTimeOffset ObservedAt,
    string? ExtractedEntities,
    string? ExtractedRelations);

/// <summary>
/// Version-2 fingerprint input: the version-1 fields plus the extracted
/// commitment and observation payloads and the user-authorship flag, all of
/// which now affect graph projection. Raw bodies remain excluded.
/// </summary>
public sealed record KnowledgeCommunicationFingerprintInputV2(
    KnowledgeCommunicationFingerprintInput Communication,
    string? ExtractedCommitments,
    string? ExtractedObservations,
    bool IsUserAuthored);

/// <summary>
/// Creates portable communication source identities and the version-1 input
/// fingerprint used by the contribution ledger. Hosts must use this policy so
/// the same communication has the same identity and change detector.
/// </summary>
public static class KnowledgeSourceIdentity
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static bool TryCreateRfc822(string? messageId, out string stableId)
    {
        stableId = string.Empty;
        if (string.IsNullOrWhiteSpace(messageId))
            return false;

        var normalized = messageId.Trim().Trim('<', '>').Trim().ToLowerInvariant();
        if (normalized.Length == 0)
            return false;

        stableId = Bound($"rfc822:{normalized}");
        return true;
    }

    public static string CreateNative(string sourceKind, string nativeId)
    {
        var normalizedKind = KnowledgeContractValidation.RequiredTrimmed(
            sourceKind,
            nameof(sourceKind)).ToLowerInvariant();
        KnowledgeContractValidation.RequireBounded(
            normalizedKind,
            KnowledgeContractLimits.SourceKindLength,
            nameof(sourceKind));
        if (normalizedKind.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Native source kinds cannot contain the identity delimiter ':'.",
                nameof(sourceKind));
        }
        var normalizedNativeId = KnowledgeContractValidation.RequiredTrimmed(
            nativeId,
            nameof(nativeId));
        return Bound($"source:{normalizedKind}:native:{normalizedNativeId}");
    }

    public static string CreateContent(KnowledgeCommunicationFingerprintInput input)
        => "content:" + ComputeCommunicationInputFingerprint(input);

    /// <summary>
    /// Preserves the current delimiter-based fingerprint contract exactly.
    /// Any future collision-resistant length-prefix format requires a projection
    /// version bump and explicit rematerialization rather than an in-place change.
    /// </summary>
    public static string ComputeCommunicationInputFingerprint(
        KnowledgeCommunicationFingerprintInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var canonical = string.Join('\u001f',
            NormalizeField(input.From),
            NormalizeField(input.To),
            NormalizeField(input.Cc),
            NormalizeField(input.Bcc),
            NormalizeField(input.Subject),
            input.ObservedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            NormalizeField(input.ExtractedEntities),
            NormalizeField(input.ExtractedRelations));
        return Sha256(canonical, nameof(input));
    }

    /// <summary>
    /// Version-2 fingerprint: appends the projection-version-4 inputs to the
    /// frozen version-1 delimiter layout. Version 1 stays byte-identical for
    /// callers that still mint content identities from it.
    /// </summary>
    public static string ComputeCommunicationInputFingerprintV2(
        KnowledgeCommunicationFingerprintInputV2 input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Communication);
        var communication = input.Communication;
        var canonical = string.Join('\u001f',
            "v2",
            NormalizeField(communication.From),
            NormalizeField(communication.To),
            NormalizeField(communication.Cc),
            NormalizeField(communication.Bcc),
            NormalizeField(communication.Subject),
            communication.ObservedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            NormalizeField(communication.ExtractedEntities),
            NormalizeField(communication.ExtractedRelations),
            NormalizeField(input.ExtractedCommitments),
            NormalizeField(input.ExtractedObservations),
            input.IsUserAuthored ? "1" : "0");
        return Sha256(canonical, nameof(input));
    }

    public static string Bound(string value)
    {
        var normalized = KnowledgeContractValidation.RequiredTrimmed(value, nameof(value));
        byte[] utf8;
        try
        {
            utf8 = StrictUtf8.GetBytes(normalized);
        }
        catch (EncoderFallbackException ex)
        {
            throw new ArgumentException("Knowledge source identities must be valid UTF-16.", nameof(value), ex);
        }

        return normalized.Length <= KnowledgeContractLimits.SourceStableIdLength
            ? normalized
            : "sha256:" + Convert.ToHexString(SHA256.HashData(utf8)).ToLowerInvariant();
    }

    private static string NormalizeField(string? value)
        => (value ?? string.Empty).Trim().Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string Sha256(string value, string parameterName)
    {
        try
        {
            return Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(value))).ToLowerInvariant();
        }
        catch (EncoderFallbackException ex)
        {
            throw new ArgumentException("Knowledge fingerprint fields must be valid UTF-16.", parameterName, ex);
        }
    }
}
