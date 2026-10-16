using System.Text.RegularExpressions;

namespace SwarmCore.Agent.Continual;

/// <summary>Host-registered labels describe a legacy report's explicit reference grammar; no semantic substring search.</summary>
public sealed record ContinualReportReferenceLabel(string Kind, string Label);
public static partial class ContinualReportReferenceCoverage
{
    // Exact bounded lexical reference forms: label [ID] [:|#] canonical-ID.
    // Unlabelled numbers are never accepted as record references.
    public static IReadOnlyList<string>? MissingKinds(string report, IReadOnlyList<ContinualReportReference> required,
        IReadOnlyList<ContinualReportReferenceLabel> labels)
    {
        if (report.Length > ContinualSavedReportCorrectionPolicy.MaximumReportCharacters || required.Count is < 1 or > 32
            || labels.Count is < 1 or > 128 || required.Any(r => !labels.Any(l => l.Kind == r.Kind))) return null;
        var presented = Bold().Replace(report, "$1");
        presented = InlineCode().Replace(presented, "$1");
        var tokens = Tokens().Matches(presented).Select(m => m.Value.EndsWith('.') && m.Value.Count(c => c == '.') == 1
            ? m.Value[..^1] : m.Value).ToArray();
        if (tokens.Length > 24_000) return null;
        var found = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < tokens.Length - 1; index++)
        {
            var registered = labels.Where(l => string.Equals(l.Label, tokens[index], StringComparison.OrdinalIgnoreCase)).ToArray();
            if (registered.Length == 0) continue;
            var valueIndex = index + 1;
            if (tokens[valueIndex].Equals("id", StringComparison.OrdinalIgnoreCase) && ++valueIndex >= tokens.Length) continue;
            foreach (var label in registered)
            {
                var expected = required.SingleOrDefault(r => r.Kind == label.Kind);
                if (expected is null) return null;
                if (string.Equals(tokens[valueIndex], expected.RecordId, StringComparison.OrdinalIgnoreCase)) found.Add(label.Kind);
                else if (Guid.TryParse(tokens[valueIndex], out _) || long.TryParse(tokens[valueIndex], out _)
                    || tokens[valueIndex].Length > 0 && (char.IsDigit(tokens[valueIndex][0])
                        || tokens[valueIndex][0] == '-' && tokens[valueIndex].Length > 1 && char.IsDigit(tokens[valueIndex][1])
                        || Guid.TryParse(expected.RecordId, out _) && tokens[valueIndex].Length >= 32))
                    return null; // A contradictory explicit reference needs independent reconciliation, not correction.
            }
        }
        return required.Select(r => r.Kind).Where(kind => !found.Contains(kind)).ToArray();
    }
    [GeneratedRegex("[^\\s,:;()\\[\\]{}\\\"#]+", RegexOptions.CultureInvariant)]
    private static partial Regex Tokens();
    [GeneratedRegex("\\*\\*([^*\\r\\n]{1,256})\\*\\*", RegexOptions.CultureInvariant)]
    private static partial Regex Bold();
    [GeneratedRegex("(?<!`)`([^`\\r\\n]{1,256})`(?!`)", RegexOptions.CultureInvariant)]
    private static partial Regex InlineCode();
}
