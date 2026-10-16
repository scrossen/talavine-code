using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SwarmCore.Coding.SelfImprovement;

/// <summary>What the backlog holds: something that broke, or something the person asked for that does not exist yet.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SelfImprovementKind>))]
public enum SelfImprovementKind { Defect, FeatureRequest }

/// <summary>
/// Where an item is. <see cref="Queued"/> waits for an idle moment; <see cref="InProgress"/> has a cycle running (owned
/// by one worker process); <see cref="Verified"/> has a fix committed on its own branch with the build and test checks
/// passing; <see cref="Merged"/> was also fast-forwarded into the source checkout; <see cref="NeedsHuman"/> exhausted its
/// attempts or cannot be verified automatically; <see cref="Dismissed"/> was closed by the person and stays closed.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SelfImprovementStage>))]
public enum SelfImprovementStage { Queued, InProgress, Verified, Merged, NeedsHuman, Dismissed }

/// <summary>One backlog entry. Written by the reporter (sightings), the worker (cycles) and the person (dismiss/requeue).</summary>
public sealed record SelfImprovementItem
{
    public required string Id { get; init; }
    public required SelfImprovementKind Kind { get; init; }
    public required string Title { get; init; }

    /// <summary>Stable identity of the underlying problem or request, so repeated sightings fold into one item.</summary>
    public required string Fingerprint { get; init; }

    /// <summary>Plain-language detail: expected vs actual with the error, or the request and what was missing.</summary>
    public required string Details { get; init; }

    /// <summary>How it was observed (turn-error, tool-error, unhandled-exception, decline, request_feature, unknown-command).</summary>
    public string? Source { get; init; }

    /// <summary>The conversation in which it was last observed, when there was one.</summary>
    public string? ConversationId { get; init; }

    public int Occurrences { get; init; } = 1;
    public DateTime FirstSeenUtc { get; init; }
    public DateTime LastSeenUtc { get; init; }

    public SelfImprovementStage Stage { get; init; } = SelfImprovementStage.Queued;

    /// <summary>Cycles that ran to an outcome (a deferred cycle does not count).</summary>
    public int Attempts { get; init; }
    public DateTime? LastAttemptUtc { get; init; }

    /// <summary>The last outcome in plain words.</summary>
    public string? Note { get; init; }

    /// <summary>Bounded verification evidence from the last cycle.</summary>
    public string? Evidence { get; init; }

    public string? Branch { get; init; }
    public string? Commit { get; init; }
    public string? WorktreePath { get; init; }

    /// <summary>The session of the last cycle, so the person can read what the agent did.</summary>
    public string? SessionId { get; init; }

    /// <summary>The worker instance (pid:startTicks:nonce) running the item's cycle while <see cref="Stage"/> is InProgress.</summary>
    public string? Owner { get; init; }

    public long Revision { get; init; }

    [JsonIgnore]
    public bool IsOpen => Stage is SelfImprovementStage.Queued or SelfImprovementStage.InProgress;
}

/// <summary>A sighting to fold into the backlog. A null <see cref="Fingerprint"/> is computed from kind, title and details.</summary>
public sealed record SelfImprovementReport(
    SelfImprovementKind Kind,
    string Title,
    string Details,
    string? Fingerprint = null,
    string? Source = null,
    string? ConversationId = null);

/// <summary>
/// Fingerprints fold repeated sightings of one problem into one item. Numbers, hexadecimal ids, GUIDs, quoted strings
/// and path prefixes are normalized away first, so a stack line number or a temporary folder does not make a new item.
/// </summary>
public static class SelfImprovementFingerprint
{
    private static readonly Regex Guid = new("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);
    private static readonly Regex Hex = new("\\b[0-9a-fA-F]{12,}\\b", RegexOptions.Compiled);
    private static readonly Regex Quoted = new("(\"[^\"\\n]*\"|'[^'\\n]*')", RegexOptions.Compiled);
    private static readonly Regex PathPrefix = new("(?:[A-Za-z]:)?[\\\\/](?:[^\\\\/\\s:]+[\\\\/])+", RegexOptions.Compiled);
    private static readonly Regex LineNumber = new(":line \\d+|:\\d+(?::\\d+)?\\b", RegexOptions.Compiled);
    private static readonly Regex Number = new("\\d+", RegexOptions.Compiled);
    private static readonly Regex Space = new("\\s+", RegexOptions.Compiled);

    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = Guid.Replace(text, "<guid>");
        s = Hex.Replace(s, "<hex>");
        s = Quoted.Replace(s, "<str>");
        s = PathPrefix.Replace(s, "<path>/");
        s = LineNumber.Replace(s, "");
        s = Number.Replace(s, "#");
        return Space.Replace(s, " ").Trim().ToLowerInvariant();
    }

    /// <summary>A 32-character hex digest over the normalized parts.</summary>
    public static string Compute(SelfImprovementKind kind, params string?[] parts)
    {
        var builder = new StringBuilder(kind.ToString());
        foreach (var part in parts) builder.Append('\u001f').Append(Normalize(part));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))[..32].ToLowerInvariant();
    }

    public static bool IsValid(string? fingerprint)
        => !string.IsNullOrEmpty(fingerprint) && fingerprint.Length is >= 8 and <= 64 && fingerprint.All(char.IsAsciiLetterOrDigit);
}
