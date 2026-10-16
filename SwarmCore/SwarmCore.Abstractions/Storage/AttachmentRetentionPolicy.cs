using System.Security.Cryptography;
using System.Text;

namespace SwarmCore.Abstractions.Storage;

/// <summary>Content-bound proof, independent of a host's queue-completion flags.</summary>
public static class AttachmentRetentionPolicy
{
    public static string? ContentKey(string? hash, string? text, string? description, string? tags)
    {
        if (hash is not { Length: 64 } || !hash.All(Uri.IsHexDigit)
            || (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(description))) return null;
        // Length-prefix fields so embedded separators cannot create the same receipt.
        var fields = new[] { "attachment-knowledge-v2-full-index", hash.ToLowerInvariant(), text ?? "", description ?? "", tags ?? "" };
        var value = string.Concat(fields.Select(f => $"{f.Length}:{f}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    public static string? RetainReason(bool keepOffline, bool sourceRetrievable, bool pending,
        bool extractionComplete, bool stagesComplete, string? contentKey, string? semanticReceipt, string? knowledgeReceipt)
    {
        if (keepOffline) return "Kept offline";
        if (!sourceRetrievable) return "No retrievable server original";
        if (pending || !extractionComplete) return "Waiting for attachment reading";
        if (!stagesComplete || contentKey is null || semanticReceipt != contentKey || knowledgeReceipt != contentKey)
            return "Waiting for complete search and knowledge processing";
        return null;
    }
}
