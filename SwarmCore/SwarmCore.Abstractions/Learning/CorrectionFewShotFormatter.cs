using System.Text;
using System.Text.Json;

namespace SwarmCore.Abstractions.Learning;

/// <summary>
/// Renders recent corrections as a ground-truth few-shot section for a
/// classifier prompt. Pure formatting — no I/O — so consumers (message
/// pipeline, CRM agents) share one rendering and one precedence contract:
/// the section is appended AFTER the generic rubric so learned preferences
/// override it, and any explicit standing user instructions must still be
/// appended after this section (the final word).
/// </summary>
public static class CorrectionFewShotFormatter
{
    /// <summary>Default number of examples injected per domain.</summary>
    public const int DefaultFewShotCount = 20;

    /// <summary>
    /// Render one domain's corrections, newest first. Returns null when there
    /// is nothing to render so callers can skip the section cleanly.
    /// <paramref name="axisLabel"/> names the axis for the model
    /// (e.g. "attention", "kind"); <paramref name="corrections"/> should
    /// already be capped to the desired few-shot count.
    /// </summary>
    public static string? Render(string axisLabel, IReadOnlyList<CorrectionRecord> corrections)
    {
        if (corrections is not { Count: > 0 }) return null;

        var sb = new StringBuilder();
        sb.AppendLine($"### Corrected {axisLabel} examples (ground truth)");
        sb.AppendLine();
        if (axisLabel.Equals("utility", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine("These examples calibrate useful_no_action versus low_value_no_action only. They never create or erase action_required and imply nothing about any other axis.");
        }
        else
        {
            sb.AppendLine($"The user explicitly corrected past verdicts; match their standard for {axisLabel}. Corrections apply to the {axisLabel} axis ONLY and imply nothing about any other axis.");
        }
        foreach (var c in corrections)
        {
            var evidence = DescribeEvidence(c);
            sb.AppendLine($"- {evidence} → user says {c.UserVerdict.ToUpperInvariant()} (classifier had said {c.MachineVerdict})");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Render the user-approved semantic rules for a domain — the distilled
    /// tier above the raw examples. Callers compose this ABOVE
    /// <see cref="Render"/>'s example block: rules generalize, examples
    /// calibrate. Returns null when there are no rules.
    /// </summary>
    public static string? RenderRules(string axisLabel, IReadOnlyList<LearnedRule> rules)
    {
        // Compiled content rules (SubjectPattern set) have LEFT the prompt —
        // the deterministic routing hint applies them; rendering them here
        // would double-count and waste prompt budget on settled ground.
        rules = rules.Where(r => string.IsNullOrWhiteSpace(r.SubjectPattern)).ToList();
        if (rules is not { Count: > 0 }) return null;

        var sb = new StringBuilder();
        sb.AppendLine($"### Learned {axisLabel} rules (user-approved)");
        sb.AppendLine();
        if (axisLabel.Equals("utility", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine("Distilled from the user's explicit corrections and approved by them. They calibrate useful_no_action versus low_value_no_action only — they never create or erase action_required and imply nothing about any other axis.");
        }
        else
        {
            sb.AppendLine($"Distilled from the user's explicit corrections and approved by them. They apply to the {axisLabel} axis ONLY and imply nothing about any other axis.");
        }
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Text)) continue;
            sb.AppendLine($"- {rule.Text.Trim()}");
        }
        return sb.ToString().TrimEnd();
    }

    private static string DescribeEvidence(CorrectionRecord c)
    {
        string? from = null, subject = null;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(c.EvidenceJson) ? "{}" : c.EvidenceJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("from", out var f)) from = f.GetString();
                if (doc.RootElement.TryGetProperty("subject", out var s)) subject = s.GetString();
            }
        }
        catch (JsonException)
        {
            // Malformed evidence degrades to the subject key — never break the prompt.
        }

        from ??= c.SubjectKey;
        return string.IsNullOrWhiteSpace(subject)
            ? $"From: {Flatten(from, 100)}"
            : $"From: {Flatten(from, 100)} | Subject: \"{Flatten(subject, 120)}\"";
    }

    /// <summary>
    /// Sender/subject text is attacker-controlled and rendered into a
    /// single-line bulleted list — flatten control characters and cap length
    /// so it cannot fake extra examples or break the section format.
    /// </summary>
    private static string Flatten(string value, int maxLength)
    {
        var chars = value.Select(ch => char.IsControl(ch) ? ' ' : ch).ToArray();
        var flattened = new string(chars).Trim();
        return flattened.Length <= maxLength ? flattened : flattened[..maxLength] + "…";
    }
}
