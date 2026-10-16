using System.Text.RegularExpressions;

namespace SwarmCore.Agent.Continual;

/// <summary>
/// Detects requests to renew ordinary implementation authority. This is a defect detector,
/// not an authority grant: it must never approve an action or resolve a target ambiguity.
/// </summary>
public static partial class MissionInterruptionPolicy
{
    public const string DefectToolName = "host.mission.unnecessary-confirmation";

    public static bool IsRoutineConfirmation(string? output)
        => !string.IsNullOrWhiteSpace(output) && RoutineConfirmation().IsMatch(output);

    public static int UnnecessaryConfirmationCount(IEnumerable<ContinualToolReceipt> receipts)
        => receipts.Count(r => r.ToolName == DefectToolName);

    [GeneratedRegex(@"(?:^|[\r\n.!]\s*)\s*(?:may I|can I|shall I|should I|would you like me to|do you want me to|please (?:confirm|approve|allow)(?: that I (?:can|may))?)\s+(?:(?:go ahead and|proceed (?:to|with)|continue (?:to|with))\s+)?(?:edit|modify|write|repair|fix|build|compile|test|run (?:the )?(?:tests|build|compiler)|install (?:the |this |a )?(?:package|dependency|tool)|restore|retry|continue|proceed)\b[^?\r\n]*\?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RoutineConfirmation();
}
