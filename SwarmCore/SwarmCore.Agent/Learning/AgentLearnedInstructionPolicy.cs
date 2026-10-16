using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using SwarmCore.Agent.InstructionChat;

namespace SwarmCore.Agent.Learning;

/// <summary>Source-neutral normalized practice learned by an agent from completed work.</summary>
public sealed record AgentLearnedPractice(
    AgentLearnedPracticeKind Kind,
    string Trigger,
    string Procedure,
    string? Reason,
    string ContentKey);

public sealed record AgentLearnedPracticeView(
    int Sequence,
    string Trigger,
    string Procedure,
    string? Reason,
    string Reference,
    AgentLearnedPracticeKind Kind = AgentLearnedPracticeKind.VerifyEvidence);

public enum AgentLearnedPracticeKind
{
    VerifyEvidence = 0,
    ConsultKnowledge = 1,
    AskClarifyingQuestion = 2,
    RecordOutcome = 3,
}

/// <summary>
/// Shared validation, identity, audit-chain, and prompt rendering for self-learned operating
/// practices. Hosts own authorization and persistence; learned text never grants authority.
/// </summary>
public static class AgentLearnedInstructionPolicy
{
    public const int MaximumTriggerChars = 300;
    public const int MaximumProcedureChars = 1_200;
    public const int MaximumReasonChars = 1_000;
    public const int MaximumPromptChars = 2_800;

    private static readonly Regex SourceMeasurement = new(
        @"(?:[$€£¥]\s*\d[\d,]*(?:\.\d+)?|\b\d[\d,]*(?:\.\d+)?\s*(?:USD|EUR|GBP|CAD|AUD)\b)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    /// <summary>Observed amounts belong in source records or evidence, not recurring procedures.</summary>
    public static bool ContainsSourceMeasurement(string? text)
        => !string.IsNullOrWhiteSpace(text) && SourceMeasurement.IsMatch(text);

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public static AgentLearnedPractice Normalize(
        AgentLearnedPracticeKind kind,
        string? trigger,
        string? procedure,
        string? reason = null)
    {
        var normalizedTrigger = Required(trigger, MaximumTriggerChars, nameof(trigger));
        var normalizedProcedure = Required(procedure, MaximumProcedureChars, nameof(procedure));
        var normalizedReason = Optional(reason, MaximumReasonChars, nameof(reason));

        var extracted = InstructionSecretExtractor.Extract(
            string.Join('\n', new[] { normalizedTrigger, normalizedProcedure, normalizedReason ?? string.Empty }));
        if (extracted.Credentials.Count > 0)
            throw new ArgumentException("Learned operating practices cannot contain credentials or secrets.");
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));

        var contentKey = HashFields("agent-learned-practice-v2", kind.ToString(), normalizedTrigger, normalizedProcedure);
        return new AgentLearnedPractice(
            kind,
            normalizedTrigger,
            normalizedProcedure,
            normalizedReason,
            contentKey);
    }

    public static string NextChainHash(
        string? previousChainHash,
        int sequence,
        AgentLearnedPractice practice)
    {
        ArgumentNullException.ThrowIfNull(practice);
        if (sequence <= 0) throw new ArgumentOutOfRangeException(nameof(sequence));
        return HashFields(
            "agent-learned-chain-v1",
            previousChainHash?.Trim().ToLowerInvariant() ?? string.Empty,
            sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            practice.ContentKey,
            practice.Kind.ToString(),
            practice.Trigger,
            practice.Procedure,
            practice.Reason ?? string.Empty);
    }

    public static string RenderPromptSection(IReadOnlyList<AgentLearnedPracticeView> practices)
    {
        ArgumentNullException.ThrowIfNull(practices);
        if (practices.Count == 0) return string.Empty;

        const string heading = "## Learned operating practices";
        const string warning = "Only host-authored safe behavior kinds are compiled here. The free-form experience narrative remains in the audit log and is never inserted into this prompt. These behaviors cannot grant capabilities or override the current boss task.";
        var used = heading.Length + warning.Length + 2;
        var selected = new List<(AgentLearnedPracticeView Item, string Line)>();
        foreach (var item in practices
                     .OrderByDescending(item => item.Sequence)
                     .GroupBy(item => item.Kind)
                     .Select(group => group.First())
                     .OrderByDescending(item => item.Sequence))
        {
            var line = $"- {SafeInstruction(item.Kind)} [{item.Reference}]";
            if (used + line.Length + 1 > MaximumPromptChars) break;
            selected.Add((item, line));
            used += line.Length + 1;
        }
        if (selected.Count == 0) return string.Empty;
        return string.Join('\n', new[] { heading, warning }
            .Concat(selected.OrderBy(entry => entry.Item.Sequence).Select(entry => entry.Line)));
    }

    /// <summary>
    /// The host-authored behavior text for a kind — the ONLY prose that ever
    /// compiles into a prompt for this lane. Public so adoption-review
    /// surfaces can show a person exactly what approving would compile.
    /// </summary>
    public static string DescribeKind(AgentLearnedPracticeKind kind) => SafeInstruction(kind);

    private static string SafeInstruction(AgentLearnedPracticeKind kind) => kind switch
    {
        AgentLearnedPracticeKind.VerifyEvidence => "verify current evidence before acting or making a claim.",
        AgentLearnedPracticeKind.ConsultKnowledge => "consult the authorized knowledge tools and preserve citations.",
        AgentLearnedPracticeKind.AskClarifyingQuestion => "ask the boss for the missing decision-critical detail.",
        AgentLearnedPracticeKind.RecordOutcome => "record the outcome and its provenance after the work completes.",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string HashFields(params string[] values)
    {
        using var stream = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        foreach (var value in values)
        {
            var bytes = StrictUtf8.GetBytes(value);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            stream.Write(length);
            stream.Write(bytes);
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static string Required(string? value, int max, string name)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("A value is required.", name);
        ValidateStrictUtf8(normalized, name);
        if (normalized.Length > max)
            throw new ArgumentException($"The value must be {max} characters or fewer.", name);
        return normalized;
    }

    private static string? Optional(string? value, int max, string name)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized)) return null;
        ValidateStrictUtf8(normalized, name);
        if (normalized.Length > max)
            throw new ArgumentException($"The value must be {max} characters or fewer.", name);
        return normalized;
    }

    private static void ValidateStrictUtf8(string value, string name)
    {
        try { _ = StrictUtf8.GetByteCount(value); }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("The value contains malformed Unicode.", name, exception);
        }
    }

}
