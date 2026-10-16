using System.Text.RegularExpressions;

namespace SwarmCore.Chat.Components;

/// <summary>A readable notice with the original diagnostic text available on demand.</summary>
public sealed partial record ChatNoticePresentation(string Summary, string? Details)
{
    private const int SummaryLimit = 240;

    public static ChatNoticePresentation Create(string text, string? details)
    {
        var summary = text.Trim();
        var lineEnd = summary.IndexOfAny(['\r', '\n']);
        if (lineEnd >= 0) summary = summary[..lineEnd].TrimEnd();

        // Some process failures append timestamped stderr to the exception on the same line.
        var logStart = LogTimestamp().Match(summary);
        if (logStart.Success && logStart.Index > 0) summary = summary[..logStart.Index].TrimEnd();
        if (summary.Length > SummaryLimit) summary = summary[..SummaryLimit].TrimEnd() + "…";

        if (summary == text.Trim()) return new(summary, details);
        if (string.IsNullOrWhiteSpace(details) || text.Contains(details.Trim(), StringComparison.Ordinal))
            return new(summary, text);
        if (details.Contains(text.Trim(), StringComparison.Ordinal)) return new(summary, details);
        return new(summary, text + "\n\n" + details);
    }

    [GeneratedRegex(@"\b\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z\s+(?:WARN|ERROR|INFO|DEBUG|TRACE)\b")]
    private static partial Regex LogTimestamp();
}
