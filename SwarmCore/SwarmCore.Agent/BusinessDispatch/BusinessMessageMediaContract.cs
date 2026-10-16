using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SwarmCore.Agent.BusinessDispatch;

public static class BusinessMessageMediaContract
{
    public const int MaximumCount = 10;
    public const int MaximumTotalBytes = 5 * 1024 * 1024;
    private static readonly JsonSerializerOptions StrictJson = new()
    { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    public static string InlineId(BusinessMessageMedia media) => "media-" + BusinessDispatchContract.Hash(new { media.ResourceId, media.Sha256 });
    public static IReadOnlyList<BusinessMessageMedia>? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        if (json.Length > 16000) throw new ArgumentException("The approved media manifest is too large.");
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.EnumerateArray().Any(item =>
            item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != item.EnumerateObject().Count()))
            throw new ArgumentException("Media entries must be objects with distinct exact property names.");
        var media = JsonSerializer.Deserialize<BusinessMessageMedia[]>(json, StrictJson);
        Validate(media);
        return media is { Length: > 0 } ? media : null;
    }
    public static int Count(string? json) => Parse(json)?.Count ?? 0;
    public static string? Serialize(IReadOnlyList<BusinessMessageMedia>? media)
    { Validate(media); return media is { Count: > 0 } ? JsonSerializer.Serialize(media) : null; }
    public static void Validate(IReadOnlyList<BusinessMessageMedia>? media)
    {
        if (media is null || media.Count == 0) return;
        if (media.Any(x => x is null)) throw new ArgumentException("Every media entry must describe an exact resource.");
        if (media.Count > MaximumCount || media.Sum(x => (decimal)x.ByteCount) > MaximumTotalBytes
            || media.Select(x => x.ResourceId).Distinct(StringComparer.Ordinal).Count() != media.Count)
            throw new ArgumentException("Choose at most ten distinct attachments totalling at most 5 MiB.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in media)
        {
            if (!Regex.IsMatch(item.ResourceId ?? "", "^[a-z][a-z0-9-]{0,31}:[A-Za-z0-9][A-Za-z0-9_-]{0,79}$")
                || string.IsNullOrWhiteSpace(item.FileName) || item.FileName.Length > 255
                || item.FileName.IndexOfAny(['/', '\\', ':']) >= 0 || item.FileName.Any(char.IsControl)
                || item.FileName is "." or ".." || item.ByteCount <= 0 || item.ByteCount > MaximumTotalBytes
                || !Regex.IsMatch(item.Sha256 ?? "", "^[0-9A-F]{64}$")
                || item.ContentType is not ("application/pdf" or "image/png" or "image/jpeg" or "image/gif" or "text/plain" or "text/csv")
                || item.Disposition is not ("attachment" or "inline") || !names.Add(item.FileName))
                throw new ArgumentException("An exact resource identity, safe filename, supported MIME type, bounded bytes and SHA-256 are required.");
            if (item.Disposition == "inline")
            {
                if (!item.ContentType.StartsWith("image/", StringComparison.Ordinal)
                    || !Regex.IsMatch(item.ContentId ?? "", "^[A-Za-z0-9][A-Za-z0-9._@-]{0,127}$") || !ids.Add(item.ContentId!))
                    throw new ArgumentException("Inline images need distinct explicit bounded Content-IDs.");
            }
            else if (item.ContentId is not null) throw new ArgumentException("Ordinary attachments cannot carry inline Content-IDs.");
        }
    }
    public static void ValidateFor(string channel, string body, IReadOnlyList<BusinessMessageMedia>? media)
    {
        Validate(media);
        // Tokenize the complete CID after HTML entity decoding. IDs are case-sensitive;
        // the URI scheme is not. Prefix matches or unbound references are never accepted.
        if (channel is "email" or "shared-email")
        {
            var references = Regex.Matches(System.Net.WebUtility.HtmlDecode(body), "(?i)(?<![a-z0-9])cid:([^\\s\"'<>\\)]+)")
                .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            var approved = (media ?? []).Where(x => x.Disposition == "inline").Select(x => x.ContentId!).ToHashSet(StringComparer.Ordinal);
            if (!references.SetEquals(approved))
                throw new ArgumentException("Every exact case-sensitive CID reference must bind an approved inline image, and every inline image must be referenced.");
        }
        if (media is null || media.Count == 0) return;
        if (channel is not ("email" or "shared-email" or "sms"))
            throw new ArgumentException("This configured adapter does not support attachments. Select a supported email or MMS route.");
        foreach (var item in media)
        {
            if (channel == "sms" && (item.Disposition != "attachment"
                || item.ContentType is not ("image/png" or "image/jpeg" or "image/gif" or "application/pdf")
                || item.ByteCount > 500 * 1024 || item.FileName.Length > 20
                || !Regex.IsMatch(item.FileName, "^[A-Za-z0-9._-]+$")))
                throw new ArgumentException("This MMS route supports only bounded images/PDF attachments up to 500 KiB each; inline and unsupported media are blocked.");
        }
    }
    public static bool MatchesBytes(BusinessMessageMedia media, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != media.ByteCount || Convert.ToHexString(SHA256.HashData(bytes)) != media.Sha256) return false;
        return media.ContentType switch
        {
            "application/pdf" => bytes.StartsWith("%PDF-"u8),
            "image/png" => bytes.StartsWith(new byte[] {137,80,78,71,13,10,26,10}),
            "image/jpeg" => bytes.StartsWith(new byte[] {255,216,255}),
            "image/gif" => bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8),
            "text/plain" or "text/csv" => IsUtf8Text(bytes),
            _ => false
        };
    }
    private static bool IsUtf8Text(ReadOnlySpan<byte> bytes)
    {
        try { return !new System.Text.UTF8Encoding(false, true).GetString(bytes).Any(x => char.IsControl(x) && x is not ('\r' or '\n' or '\t')); }
        catch (System.Text.DecoderFallbackException) { return false; }
    }
}
