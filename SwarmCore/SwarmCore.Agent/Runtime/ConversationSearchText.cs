using System.Text;

namespace SwarmCore.Agent.Runtime;

/// <summary>One literal, Unicode-aware search contract for database matches and visible previews.</summary>
public static class ConversationSearchText
{
    public static string Normalize(string text) => text.Normalize(NormalizationForm.FormC);
    public static bool Contains(string? text, string query)
        => text is not null && Normalize(text).Contains(Normalize(query), StringComparison.OrdinalIgnoreCase);
}
