using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SwarmCore.Agent.Continual;

/// <summary>
/// Host-configured limits for compacting retained receipt texts. Compaction keeps every fact of a receipt (tool, state,
/// error flag, timing, side-effect class, host proof) and replaces only a long result or input text with a digest and
/// an excerpt, so the receipt stays honest evidence without holding every byte forever. Hosts opt in; nothing is
/// compacted by default, and the policy below never releases a text an open attempt, an inspection or a host proof
/// still depends on.
/// </summary>
public sealed record ContinualReceiptRetentionOptions
{
    // Settable, not init-only: hosts bind these from configuration, and the configuration binder skips init-only members.

    /// <summary>An earlier attempt of a goal that may still continue must have been closed (its last receipt done) for at least this long.</summary>
    public TimeSpan ClosedAttemptAge { get; set; } = TimeSpan.FromDays(30);

    /// <summary>A finished goal (completed, cancelled, failed) must have finished at least this long ago, so a person can still read the full ledger of recent work.</summary>
    public TimeSpan TerminalGoalAge { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Only texts longer than this (in characters) are compacted; shorter texts stay whole.</summary>
    public int MinimumTextLength { get; set; } = 1024;

    /// <summary>How many leading UTF-8 bytes of the original text the compacted form keeps.</summary>
    public int ExcerptBytes { get; set; } = 1024;

    public void Validate()
    {
        if (ClosedAttemptAge < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ClosedAttemptAge));
        if (TerminalGoalAge < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(TerminalGoalAge));
        if (MinimumTextLength < 0) throw new ArgumentOutOfRangeException(nameof(MinimumTextLength));
        if (ExcerptBytes < 0) throw new ArgumentOutOfRangeException(nameof(ExcerptBytes));
    }
}

/// <summary>
/// What remains of a compacted receipt text: its hash and size, so what was there can be proven, and when it was compacted.
/// The first <see cref="ExcerptBytes"/> bytes of the original stay in the receipt text itself (see
/// <see cref="ContinualReceiptRetentionPolicy.RenderResult"/> and <see cref="ContinualReceiptRetentionPolicy.TryReadExcerpt"/>),
/// so every reader of the text sees how the output began without the inventory holding the excerpt twice.
/// </summary>
public sealed record ContinualReceiptTextDigest(string Sha256, int ByteLength, int ExcerptBytes, DateTimeOffset CompactedAtUtc);

/// <summary>Why a receipt text is kept whole. <see cref="None"/> means the policy releases it.</summary>
public enum ContinualReceiptRetentionHold
{
    None = 0,
    /// <summary>Missions keep their own evidence and verification contract; their receipts are never compacted here.</summary>
    MissionGoal,
    /// <summary>The goal is working, verifying, or waiting for an inspection of prior actions.</summary>
    GoalActive,
    /// <summary>A worker holds the goal's execution lease.</summary>
    GoalLeased,
    /// <summary>The receipt belongs to the current attempt of a goal that may continue from it.</summary>
    AttemptOpen,
    /// <summary>The receipt completed more recently than the configured age.</summary>
    TooRecent,
    /// <summary>The receipt is still Started: running, or an outcome nobody has settled.</summary>
    ReceiptOpen,
    /// <summary>A dispatched effect whose physical outcome an inspector may still need to read.</summary>
    NeedsInspection,
    /// <summary>The receipt carries a host proof bound to its exact fingerprint.</summary>
    HostProof,
    /// <summary>A host proof on another receipt binds this receipt as its read-only observation.</summary>
    ProofObservation,
    /// <summary>No text is above the limit, or every long text is already compacted.</summary>
    NothingToCompact,
}

/// <summary>
/// The shared rules for when a receipt text may be compacted and what the compacted form is. Hosts own the storage and
/// the schedule; this policy owns eligibility and the marker so every host writes the same honest form.
/// </summary>
public static class ContinualReceiptRetentionPolicy
{
    /// <summary>A compacted result text begins with this line; the first bytes of the original follow it.</summary>
    public const string ResultMarkerPrefix = "[Compacted receipt text: ";

    /// <summary>A compacted input stays valid JSON: an object with this first property, so structural readers still parse it.</summary>
    public const string InputMarkerProperty = "$compacted";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The hold that applies to every receipt of <paramref name="goal"/>, or <see cref="ContinualReceiptRetentionHold.None"/>.</summary>
    public static ContinualReceiptRetentionHold GoalHold(ContinualObjectiveSnapshot goal, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(goal);
        if (goal.Mission is not null) return ContinualReceiptRetentionHold.MissionGoal;
        if (ContinualObjectiveStateMachine.IsTerminal(goal.Status)) return ContinualReceiptRetentionHold.None;
        if (goal.Status is ContinualObjectiveStatus.InProgress or ContinualObjectiveStatus.Uncertain or ContinualObjectiveStatus.VerificationPending)
            return ContinualReceiptRetentionHold.GoalActive;
        if (goal.LeaseToken is not null || goal.LeaseExpiresAtUtc > now) return ContinualReceiptRetentionHold.GoalLeased;
        return ContinualReceiptRetentionHold.None;
    }

    /// <summary>
    /// The hold that keeps <paramref name="receipt"/> whole, or <see cref="ContinualReceiptRetentionHold.None"/> when it may be
    /// compacted now. Age is measured from when the receipt's attempt closed (its last receipt), and for a finished goal from
    /// when the goal finished, never from the receipt alone: a long goal that finishes today keeps its whole ledger for the
    /// grace period. Pass <paramref name="attemptEnds"/> from <see cref="AttemptEnds"/> when checking many receipts.
    /// </summary>
    public static ContinualReceiptRetentionHold ReceiptHold(ContinualObjectiveSnapshot goal, ContinualToolReceipt receipt,
        ContinualReceiptRetentionOptions options, DateTimeOffset now, IReadOnlyDictionary<int, DateTimeOffset>? attemptEnds = null)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(options);
        var goalHold = GoalHold(goal, now);
        if (goalHold != ContinualReceiptRetentionHold.None) return goalHold;
        if (receipt.State == ContinualToolReceiptState.Started) return ContinualReceiptRetentionHold.ReceiptOpen;
        // A proof is the more specific reason: a resolved effect still reads as needing inspection to the raw predicate.
        if (receipt.HostResolution is not null) return ContinualReceiptRetentionHold.HostProof;
        if (MissionTransitions.NeedsInspection(receipt)) return ContinualReceiptRetentionHold.NeedsInspection;
        if (IsProofObservation(goal, receipt)) return ContinualReceiptRetentionHold.ProofObservation;
        var terminal = ContinualObjectiveStateMachine.IsTerminal(goal.Status);
        if (!terminal && receipt.Attempt >= goal.AttemptCount) return ContinualReceiptRetentionHold.AttemptOpen;
        var closed = attemptEnds is not null && attemptEnds.TryGetValue(receipt.Attempt, out var end) ? end : AttemptEnds(goal)[receipt.Attempt];
        if (terminal && goal.UpdatedAtUtc > closed) closed = goal.UpdatedAtUtc;
        if (closed > now - (terminal ? options.TerminalGoalAge : options.ClosedAttemptAge)) return ContinualReceiptRetentionHold.TooRecent;
        if (!IsCompactable(receipt.Result, receipt.CompactedResult, options) && !IsCompactable(receipt.InputJson, receipt.CompactedInput, options))
            return ContinualReceiptRetentionHold.NothingToCompact;
        return ContinualReceiptRetentionHold.None;
    }

    /// <summary>When each attempt's last receipt completed (or started): the moment the attempt's work stopped.</summary>
    public static IReadOnlyDictionary<int, DateTimeOffset> AttemptEnds(ContinualObjectiveSnapshot goal)
    {
        ArgumentNullException.ThrowIfNull(goal);
        var ends = new Dictionary<int, DateTimeOffset>();
        foreach (var receipt in goal.Receipts)
        {
            var at = receipt.CompletedAtUtc ?? receipt.StartedAtUtc;
            if (!ends.TryGetValue(receipt.Attempt, out var current) || at > current) ends[receipt.Attempt] = at;
        }
        return ends;
    }

    /// <summary>True when any host proof in the goal binds this receipt as one of its observations.</summary>
    public static bool IsProofObservation(ContinualObjectiveSnapshot goal, ContinualToolReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(receipt);
        foreach (var other in goal.Receipts)
        {
            if (other.HostResolution is not { } proof) continue;
            foreach (var binding in proof.Observations)
                if (binding is not null && binding.Attempt == receipt.Attempt && string.Equals(binding.ToolCallId, receipt.ToolCallId, StringComparison.Ordinal))
                    return true;
        }
        return false;
    }

    /// <summary>A text is compactable when it is longer than the limit and not already compacted (by its digest member, or by its marker form if an older build dropped the member).</summary>
    public static bool IsCompactable(string? text, ContinualReceiptTextDigest? existing, ContinualReceiptRetentionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return existing is null && text is not null && text.Length > options.MinimumTextLength && !LooksCompacted(text);
    }

    /// <summary>True for either rendered form (a reader's cue; the digest members are the authority).</summary>
    public static bool LooksCompacted(string? text)
        => IsCompactedResult(text) || (text is not null && text.StartsWith("{\"" + InputMarkerProperty + "\"", StringComparison.Ordinal));

    /// <summary>The digest of <paramref name="text"/> (UTF-8 SHA-256 and byte length) and the excerpt its compacted form keeps.</summary>
    public static (ContinualReceiptTextDigest Digest, string Excerpt) Digest(string text, int excerptBytes, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = Encoding.UTF8.GetBytes(text);
        var excerpt = Excerpt(text, excerptBytes);
        return (new(Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length, Encoding.UTF8.GetByteCount(excerpt), now), excerpt);
    }

    /// <summary>The longest prefix of <paramref name="text"/> whose UTF-8 form fits in <paramref name="maxBytes"/>, never splitting a code point.</summary>
    public static string Excerpt(string text, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(text);
        var used = 0;
        var length = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (used + size > maxBytes) break;
            used += size;
            length += rune.Utf16SequenceLength;
        }
        return length >= text.Length ? text : text[..length];
    }

    /// <summary>The text a compacted result carries: one line saying what was there, then the excerpt.</summary>
    public static string RenderResult(ContinualReceiptTextDigest digest, string excerpt)
    {
        ArgumentNullException.ThrowIfNull(digest);
        ArgumentNullException.ThrowIfNull(excerpt);
        return $"{ResultMarkerPrefix}{digest.ByteLength} bytes, sha256 {digest.Sha256}, compacted {digest.CompactedAtUtc:O}. The first {digest.ExcerptBytes} bytes follow.]\n{excerpt}";
    }

    /// <summary>The JSON a compacted input carries, so readers that parse inputs still see an object.</summary>
    public static string RenderInput(ContinualReceiptTextDigest digest, string excerpt)
    {
        ArgumentNullException.ThrowIfNull(digest);
        ArgumentNullException.ThrowIfNull(excerpt);
        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [InputMarkerProperty] = "receipt input",
            ["byteLength"] = digest.ByteLength,
            ["sha256"] = digest.Sha256,
            ["compactedAtUtc"] = digest.CompactedAtUtc,
            ["excerpt"] = excerpt,
        }, Json);
    }

    /// <summary>True for the result form written by <see cref="RenderResult"/> (a reader's cue, not the authority: the receipt's digest fields are).</summary>
    public static bool IsCompactedResult(string? text) => text is not null && text.StartsWith(ResultMarkerPrefix, StringComparison.Ordinal);

    /// <summary>The excerpt kept in a compacted result (the text after the marker line) or input (its <c>excerpt</c> property).</summary>
    public static bool TryReadExcerpt(string? text, out string excerpt)
    {
        excerpt = "";
        if (string.IsNullOrEmpty(text)) return false;
        if (IsCompactedResult(text))
        {
            var newline = text.IndexOf('\n');
            excerpt = newline < 0 ? "" : text[(newline + 1)..];
            return true;
        }
        if (text[0] != '{') return false;
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty(InputMarkerProperty, out _)
                || !document.RootElement.TryGetProperty("excerpt", out var property) || property.ValueKind != JsonValueKind.String) return false;
            excerpt = property.GetString() ?? "";
            return true;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>
    /// The receipt with each releasable long text replaced by its rendered digest. Every other member keeps the same value,
    /// so the receipt's identity, state, error flag, timing and proof are unchanged; a receipt with nothing to compact is
    /// returned as the same instance. Callers check <see cref="ReceiptHold"/> first; this method only transforms.
    /// </summary>
    public static ContinualToolReceipt Compact(ContinualToolReceipt receipt, ContinualReceiptRetentionOptions options, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(options);
        var result = IsCompactable(receipt.Result, receipt.CompactedResult, options) ? Digest(receipt.Result!, options.ExcerptBytes, now) : default;
        var input = IsCompactable(receipt.InputJson, receipt.CompactedInput, options) ? Digest(receipt.InputJson!, options.ExcerptBytes, now) : default;
        if (result.Digest is null && input.Digest is null) return receipt;
        return receipt with
        {
            Result = result.Digest is null ? receipt.Result : RenderResult(result.Digest, result.Excerpt),
            CompactedResult = result.Digest ?? receipt.CompactedResult,
            InputJson = input.Digest is null ? receipt.InputJson : RenderInput(input.Digest, input.Excerpt),
            CompactedInput = input.Digest ?? receipt.CompactedInput,
        };
    }
}
