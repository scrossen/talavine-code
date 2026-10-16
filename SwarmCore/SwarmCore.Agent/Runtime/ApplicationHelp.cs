using System.Text.RegularExpressions;

namespace SwarmCore.Agent.Runtime;

/// <summary>Help published by the running host, rather than retrieved user records.</summary>
public sealed record ApplicationHelpTopic(string Title, string HelpPath, string Summary,
    IReadOnlyList<string> Keywords, string? Instructions = null);

public interface IApplicationHelpCatalog
{
    IReadOnlyList<ApplicationHelpTopic> Topics { get; }
}

public static class ApplicationHelpPolicy
{
    private static readonly Regex UsageQuestion = new(
        @"\bhow\s+(?:(?:do|can|could|should|would)\s+(?:i|we)|to)\s+(?:\w+\s+){0,3}(?:give|grant|add|attach|upload|connect|disconnect|import|export|configure|enable|disable|revoke|remove|set\s+up)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex AdditionalRecordRequest = new(
        @"\b(?:and|then|also)\s+(?:please\s+)?(?:find|search|summarize|review|read|list|compare|show|check)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static bool IsUsageQuestion(string message) => UsageQuestion.IsMatch(message);

    // A setup question can also contain a request for records. The guide may
    // help with setup, but must not veto the explicitly requested lookup.
    public static bool RequestsRecordsToo(string message) => AdditionalRecordRequest.IsMatch(message);

    public static string? BuildGuidance(string message, IApplicationHelpCatalog? catalog)
    {
        if (catalog is null || !IsUsageQuestion(message)) return null;
        var terms = Regex.Matches(message.ToLowerInvariant(), @"[\p{L}\p{N}]+")
            .Select(match => match.Value).ToHashSet(StringComparer.Ordinal);
        var topics = catalog.Topics.Select(topic => (Topic: topic,
                Score: topic.Keywords.Count(keyword => Regex.Matches(keyword.ToLowerInvariant(), @"[\p{L}\p{N}]+")
                    .All(term => terms.Contains(term.Value)))))
            .Where(entry => entry.Score > 0).OrderByDescending(entry => entry.Score).Take(3).ToArray();
        if (topics.Length == 0) return null;
        return "<application-help>\n" +
            "These are help topics shipped with the running application. Use them for app instructions; " +
            "user emails, memories, and old conversations do not establish current app capabilities. " +
            "Link to the relevant help or feature page. Do not invent controls, claim access has been granted, " +
            "or use communication source aliases as citations for these instructions.\n" +
            string.Join("\n\n", topics.Select(entry =>
                $"{entry.Topic.Title} — {entry.Topic.HelpPath}\n{entry.Topic.Summary}\n{entry.Topic.Instructions}")) +
            "\n</application-help>";
    }
}
