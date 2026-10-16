namespace SwarmCore.Abstractions.Agents;

/// <summary>
/// Plain-language descriptions of recorded actions, not claims that an objective
/// is complete. Hosts show the objective beside these labels and retain the raw
/// receipt in their technical activity view. Unknown tools never leak identifiers.
/// </summary>
public static class AgentActivityText
{
    public static string Describe(string? toolName, bool inProgress, string? safeTarget = null)
    {
        var (present, past, includeTarget) = toolName switch
        {
            "image.inspect_file" => ("Checking image", "Checked image", true),
            "http.request" or "shared.web-fetch" or "web.fetch" => ("Checking a web page", "Checked a web page", true),
            "web.search" => ("Searching the web", "Searched the web", false),
            "services.search" => ("Finding a way to do this task", "Looked for a way to do this task", false),
            "services.test" => ("Checking that a task step works", "Checked a task step", false),
            "services.revise" => ("Improving a task step", "Updated a task step", false),
            "services.activate" => ("Preparing a reusable task step", "Prepared a reusable task step", false),
            "fs.read_file" => ("Reading file", "Read file", true),
            "fs.write_file" => ("Updating file", "Updated file", true),
            "memory.search" => ("Looking up what is already known", "Looked up what is already known", false),
            "instructions.list" => ("Reviewing how to do this job", "Reviewed how to do this job", false),
            "messages.search" => ("Searching the mailbox", "Searched the mailbox", false),
            "subagents.list" => ("Checking on other agents", "Checked on other agents", false),
            "worker.due_tasks" => ("Checking scheduled work", "Checked scheduled work", false),
            _ => ("Carrying out a task step", "Carried out a task step", false),
        };
        var label = inProgress ? present : past;
        return includeTarget && !string.IsNullOrWhiteSpace(safeTarget) ? $"{label}: {safeTarget}" : label;
    }
}
