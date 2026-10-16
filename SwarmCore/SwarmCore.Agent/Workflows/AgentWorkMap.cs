using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SwarmCore.Agent.Workflows;

/// <summary>A projection of the authoritative instructions, never a second executable plan.</summary>
public sealed record AgentWorkMap(string Version, string Job, IReadOnlyList<AgentWorkStep> Steps)
{
    public static AgentWorkMap FromInstructions(string instructions)
    {
        var version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(instructions)));
        var job = new List<string>();
        var steps = new List<AgentWorkStep>();
        var section = "";
        var inCode = false;
        foreach (var raw in instructions.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal))
            { inCode = !inCode; continue; }
            if (inCode) continue;
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                section = line[3..].Trim();
                if (section.Equals("Steps each cycle", StringComparison.OrdinalIgnoreCase)) steps.Clear();
                continue;
            }
            if (section.Equals("Job", StringComparison.OrdinalIgnoreCase) && line.Length > 0)
                job.Add(line);
            if (!section.Equals("Steps each cycle", StringComparison.OrdinalIgnoreCase) || line.Length == 0)
                continue;
            var text = Regex.Replace(line, @"^(?:[-*]|\d+[.)])\s+", "");
            if (text.Length > 0)
                steps.Add(new AgentWorkStep($"{version[..12]}:{steps.Count + 1}", text));
        }
        return new(version, string.Join("\n", job), steps);
    }

    public string? ValidateProgress(string? version, string? stepId, string? state)
    {
        if (version != Version) return "The instructions changed. Read the map again before reporting progress.";
        if (!Steps.Any(step => step.Id == stepId)) return "Unknown workflow step.";
        return state is "working" or "done" or "blocked" or "skipped" ? null : "State must be working, done, blocked, or skipped.";
    }

    public static string? ValidateTransition(string? previousState, string state) =>
        state == "done" && previousState is not ("working" or "done")
            ? "Report working before doing this step, then report done after completing it."
            : null;

    public string ChangeRequest(string? stepId, string request)
    {
        if (string.IsNullOrWhiteSpace(request)) throw new ArgumentException("Describe the change.", nameof(request));
        var step = stepId is null ? null : Steps.SingleOrDefault(s => s.Id == stepId)
            ?? throw new InvalidOperationException("The selected step has changed. Reload the work map.");
        return (step is null ? "Update this agent's job and setup. " : $"Update the workflow step '{step.Text}'. ")
            + "Apply the following user's change to the operating instructions and keep all unrelated job details, limits, and rules intact. "
            + "Update the Steps each cycle section so the map matches the actual instructions. "
            + "Do not perform the job or claim a result.\nUser change:\n" + request.Trim();
    }
}

public sealed record AgentWorkStep(string Id, string Text);

public enum AgentWorkPhase { Ready, Working, Checking, Waiting, DeliveryBlocked, Completed, Failed, Stopped }

/// <summary>Recorded execution phases. Planned steps are deliberately not marked done from tool counts.</summary>
public static class AgentWorkProgress
{
    public static string? StepLabel(IEnumerable<SwarmCore.Abstractions.Agents.AgentWorkflowStepProgress> reports,
        string version, string stepId, bool running)
    {
        var current = reports.Where(p => p.Version == version).OrderByDescending(p => p.RecordedUtc).ToList();
        var step = current.FirstOrDefault(p => p.StepId == stepId);
        if (step is null) return null;
        return step.State switch
        {
            "working" when running && current[0] == step => "Working now",
            "working" => "Started",
            "done" => "Reported done",
            "skipped" => "Skipped",
            "blocked" => "Needs attention",
            _ => null
        };
    }

    public static int ActiveStage(AgentWorkPhase phase) => phase switch
    {
        AgentWorkPhase.Working or AgentWorkPhase.Waiting => 1,
        AgentWorkPhase.Checking => 2,
        AgentWorkPhase.DeliveryBlocked or AgentWorkPhase.Completed => 3,
        _ => -1
    };

    public static string StageState(int index, AgentWorkPhase phase) => phase switch
    {
        AgentWorkPhase.Completed when index == 3 => "done",
        AgentWorkPhase.Waiting when index == 1 => "waiting",
        AgentWorkPhase.DeliveryBlocked when index == 3 => "waiting",
        _ when index == ActiveStage(phase) => "current",
        _ => "planned"
    };
}
