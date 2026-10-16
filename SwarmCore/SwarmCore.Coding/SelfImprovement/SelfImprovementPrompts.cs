using System.Text;
using SwarmCore.Coding.Routing;

namespace SwarmCore.Coding.SelfImprovement;

/// <summary>The message a cycle sends to its coding session. The host commits; the agent never does.</summary>
public static class SelfImprovementPrompts
{
    public static string Cycle(SelfImprovementItem item, IReadOnlyList<CodingAcceptanceCommand> checks)
    {
        var text = new StringBuilder();
        text.AppendLine(item.Kind == SelfImprovementKind.Defect
            ? "[Talavine Code self-improvement] Fix a defect observed in Talavine Code while people used it."
            : "[Talavine Code self-improvement] Implement a capability a person asked Talavine Code for and it did not have.");
        text.AppendLine();
        text.Append("Title: ").AppendLine(item.Title);
        text.Append("Kind: ").AppendLine(item.Kind == SelfImprovementKind.Defect ? "defect" : "feature request");
        text.Append("Seen: ").Append(item.Occurrences).Append(item.Occurrences == 1 ? " time" : " times").Append(", first ").Append(item.FirstSeenUtc.ToString("yyyy-MM-dd")).AppendLine(" (UTC).");
        if (!string.IsNullOrWhiteSpace(item.Source)) text.Append("Observed through: ").AppendLine(item.Source);
        text.AppendLine();
        text.AppendLine(item.Kind == SelfImprovementKind.Defect ? "Observation:" : "Request:");
        text.AppendLine(item.Details.Trim());
        text.AppendLine();
        text.AppendLine("How to work:");
        text.AppendLine("- You are in an isolated git worktree of the Talavine Code source repository. Read AGENTS.md and memory/swarmcode.md first and follow them.");
        if (item.Kind == SelfImprovementKind.Defect)
        {
            text.AppendLine("- Reproduce the defect with a failing automated test first (in the owning project's test suite), then fix the root cause, not the symptom.");
            text.AppendLine("- Keep the change minimal and local to the code that is wrong. Do not weaken, skip or delete tests to make them pass.");
        }
        else
        {
            text.AppendLine("- Put reusable logic in the owning SwarmCore project and host policy, UI and persistence in the Talavine Code host, like neighbouring features.");
            text.AppendLine("- Implement the smallest complete slice that serves the request end to end, with tests. If the request is too large for one cycle, implement that slice and say what remains.");
            text.AppendLine("- Register new tools and commands the way existing ones are registered; nothing is live until it is wired in.");
        }
        text.AppendLine("- Do not commit, stash, rebase, push or change branches: the host commits a verified result on this branch itself.");
        text.AppendLine("- Do not touch files outside this worktree, and never edit secrets, settings files or data folders.");
        if (checks.Count > 0)
        {
            text.AppendLine("- Verification runs after you finish and must pass before anything is kept:");
            foreach (var check in checks)
                text.Append("    ").Append(check.Name).Append(": ").Append(check.FileName).Append(' ').AppendLine(string.Join(' ', check.Arguments));
            text.AppendLine("  Run the relevant ones yourself before you finish.");
        }
        text.AppendLine("- End with a short summary: what was wrong (or missing), what you changed, which tests cover it, and anything you could not do.");
        if (item.Attempts > 0 && !string.IsNullOrWhiteSpace(item.Note))
        {
            text.AppendLine();
            text.Append("A previous cycle did not produce a verified fix: ").AppendLine(item.Note.Trim());
            if (!string.IsNullOrWhiteSpace(item.Evidence))
            {
                text.AppendLine("Its verification evidence (bounded):");
                text.AppendLine(item.Evidence.Length <= 3000 ? item.Evidence : item.Evidence[..3000] + "...");
            }
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>The commit message for a verified cycle.</summary>
    public static string CommitMessage(SelfImprovementItem item)
    {
        var title = item.Title.Length <= 72 ? item.Title : item.Title[..69] + "...";
        var body = new StringBuilder();
        body.Append("Talavine Code self-improvement: ").AppendLine(title);
        body.AppendLine();
        body.AppendLine(item.Kind == SelfImprovementKind.Defect ? "Automated repair of a defect observed while Talavine Code was in use." : "Automated implementation of a capability a person asked Talavine Code for.");
        body.AppendLine();
        body.AppendLine(item.Details.Trim().Length <= 1500 ? item.Details.Trim() : item.Details.Trim()[..1500] + "...");
        body.AppendLine();
        body.Append("Self-improvement-Item: ").AppendLine(item.Id);
        body.Append("Self-improvement-Fingerprint: ").AppendLine(item.Fingerprint);
        return body.ToString().TrimEnd();
    }
}
