using System.Text;
using System.Text.RegularExpressions;

namespace SwarmCore.Knowledge;

/// <summary>
/// Normalizes an extracted entity surface without damaging meaningful balanced
/// brackets. This is shared ingestion policy: every host must mint the same
/// entity text from the same extraction.
/// </summary>
public static class KnowledgeEntitySurfaceNormalizer
{
    private static readonly char[] NoiseCharacters =
    {
        ' ', '\t', '\r', '\n',
        '"', '\'', '`',
        '“', '”', '‘', '’',
        ',', ';', ':', '.', '!', '?'
    };

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = Regex.Replace(value.Trim(), @"\s+", " ").AsSpan();
        while (true)
        {
            var trimmed = normalized.Trim(NoiseCharacters);
            if (trimmed.Length == 0)
                return string.Empty;

            var debracketed = TrimStrayBrackets(trimmed);
            if (debracketed.Length == trimmed.Length)
                return debracketed.ToString();
            normalized = debracketed;
        }
    }

    private static ReadOnlySpan<char> TrimStrayBrackets(ReadOnlySpan<char> value)
    {
        if (value.Length == 0)
            return value;

        if (TryGetBracketClose(value[0], out var close))
        {
            var matchIndex = IndexOfMatchingClose(value, value[0], close);
            if (matchIndex == value.Length - 1)
                return value[1..^1];
            if (matchIndex < 0)
                return value[1..];
        }

        if (TryGetBracketOpen(value[^1], out var open)
            && !HasUnmatchedOpen(value[..^1], open, value[^1]))
        {
            return value[..^1];
        }

        return value;
    }

    private static bool TryGetBracketClose(char candidate, out char close)
    {
        close = candidate switch
        {
            '(' => ')',
            '[' => ']',
            '<' => '>',
            _ => '\0'
        };
        return close != '\0';
    }

    private static bool TryGetBracketOpen(char candidate, out char open)
    {
        open = candidate switch
        {
            ')' => '(',
            ']' => '[',
            '>' => '<',
            _ => '\0'
        };
        return open != '\0';
    }

    private static int IndexOfMatchingClose(ReadOnlySpan<char> value, char open, char close)
    {
        var depth = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == open)
                depth++;
            else if (value[i] == close && --depth == 0)
                return i;
        }
        return -1;
    }

    private static bool HasUnmatchedOpen(ReadOnlySpan<char> value, char open, char close)
    {
        var depth = 0;
        foreach (var candidate in value)
        {
            if (candidate == open)
                depth++;
            else if (candidate == close && depth > 0)
                depth--;
        }
        return depth > 0;
    }
}

/// <summary>
/// Progressive mention-count buckets used to decide when derived summaries
/// need refreshing while preserving exact counts for rank and display.
/// </summary>
public static class KnowledgeMentionMilestones
{
    public static int GetBucket(int count)
    {
        if (count <= 1) return 1;
        if (count < 5) return 2;
        if (count < 10) return 5;
        if (count < 25) return 10;
        if (count < 50) return 25;
        if (count < 100) return 50;
        if (count < 250) return 100;
        if (count < 500) return 250;
        if (count < 1000) return 500;
        return 1000 * Math.Max(1, count / 1000);
    }
}

/// <summary>
/// Delimiter-safe cursor for a portable source key, optional source instance,
/// and host-local stable paging tie-breaker.
/// </summary>
public static class KnowledgeSourceCursor
{
    private const int MaxIdentityLength = KnowledgeContractLimits.SourceStableIdLength;
    private const int MaxInstanceLength = KnowledgeContractLimits.SourceInstanceIdLength;
    private const int MaxEncodedComponentLength = 4 * MaxIdentityLength;
    private const int MaxCursorLength = (2 * MaxEncodedComponentLength) + 34;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static string Encode(
        string portableSourceIdentity,
        string? portableSourceInstanceIdentity,
        Guid sourceRecordId)
    {
        ValidateDecodedCoordinate(
            portableSourceIdentity,
            portableSourceInstanceIdentity ?? string.Empty,
            sourceRecordId);
        return EncodeComponent(portableSourceIdentity, nameof(portableSourceIdentity)) + "."
            + EncodeComponent(
                portableSourceInstanceIdentity ?? string.Empty,
                nameof(portableSourceInstanceIdentity)) + "."
            + sourceRecordId.ToString("N");
    }

    public static (string PortableSourceIdentity, string PortableSourceInstanceIdentity, Guid SourceRecordId)
        Decode(string cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
            throw new ArgumentException("A knowledge source cursor is required.", nameof(cursor));
        if (cursor.Length > MaxCursorLength)
            throw new ArgumentException("The knowledge source cursor is too large.", nameof(cursor));
        var firstSeparator = cursor.IndexOf('.');
        var secondSeparator = firstSeparator < 0 ? -1 : cursor.IndexOf('.', firstSeparator + 1);
        if (firstSeparator <= 0
            || secondSeparator < firstSeparator + 1
            || secondSeparator == cursor.Length - 1
            || cursor.IndexOf('.', secondSeparator + 1) >= 0)
        {
            throw new ArgumentException("The knowledge source cursor is invalid.", nameof(cursor));
        }

        try
        {
            var identityComponent = cursor[..firstSeparator];
            var instanceComponent = cursor[(firstSeparator + 1)..secondSeparator];
            if (identityComponent.Length > MaxEncodedComponentLength
                || instanceComponent.Length > MaxEncodedComponentLength)
            {
                throw new FormatException("A cursor component is too large.");
            }
            if (!Guid.TryParseExact(cursor[(secondSeparator + 1)..], "N", out var sourceRecordId))
                throw new FormatException("Invalid source id component.");
            var identity = DecodeComponent(identityComponent);
            var instance = DecodeComponent(instanceComponent);
            ValidateDecodedCoordinate(identity, instance, sourceRecordId);
            var decoded = (identity, instance, sourceRecordId);
            if (!string.Equals(
                    Encode(identity, instance, sourceRecordId),
                    cursor,
                    StringComparison.Ordinal))
            {
                throw new FormatException("Cursor encoding is not canonical Base64Url.");
            }
            return decoded;
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
        {
            throw new ArgumentException("The knowledge source cursor is invalid.", nameof(cursor), ex);
        }
    }

    private static void ValidateDecodedCoordinate(
        string identity,
        string instance,
        Guid sourceRecordId)
    {
        if (string.IsNullOrEmpty(identity))
            throw new ArgumentException("A non-empty source identity is required.", nameof(identity));
        if (identity.Length > MaxIdentityLength)
            throw new ArgumentException($"Source identity cannot exceed {MaxIdentityLength} characters.", nameof(identity));
        if (instance.Length > MaxInstanceLength)
            throw new ArgumentException($"Source instance cannot exceed {MaxInstanceLength} characters.", nameof(instance));
        if (sourceRecordId == Guid.Empty)
            throw new ArgumentException("Source record id cannot be empty.", nameof(sourceRecordId));
    }

    private static string EncodeComponent(string value, string parameterName)
    {
        try
        {
            return Convert.ToBase64String(StrictUtf8.GetBytes(value))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }
        catch (EncoderFallbackException ex)
        {
            throw new ArgumentException("Source cursor identities must be valid UTF-16.", parameterName, ex);
        }
    }

    private static string DecodeComponent(string value)
    {
        if (value.Any(static character => !IsBase64UrlCharacter(character)))
            throw new FormatException("Cursor components must use unpadded Base64Url.");
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = (base64.Length % 4) switch
        {
            0 => base64,
            2 => base64 + "==",
            3 => base64 + "=",
            _ => throw new FormatException("Invalid Base64Url component.")
        };
        try
        {
            return StrictUtf8.GetString(Convert.FromBase64String(base64));
        }
        catch (DecoderFallbackException ex)
        {
            throw new FormatException("Cursor component is not valid UTF-8.", ex);
        }
    }

    private static bool IsBase64UrlCharacter(char character)
        => character is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '-' or '_';
}
