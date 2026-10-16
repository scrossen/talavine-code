using SwarmCore.LlmRouting.Services;

namespace SwarmCore.Agent.CodingTasks;

/// <summary>
/// Resolves the on-disk executable for a coding CLI command ("claude" /
/// "codex"). Injectable so tests never depend on what is installed.
/// </summary>
public interface ICodingTaskCliLocator
{
    /// <summary>First launchable candidate for the command, or null when the
    /// CLI is not installed.</summary>
    string? FindExecutable(string command);
}

/// <summary>
/// Production locator delegating to the shared
/// <see cref="SubscriptionCliDiscovery"/> sequence (known install paths,
/// running processes, PATH — the same discovery the subscription-CLI LLM
/// providers and the setup page use), so "installed for chat" and "installed
/// for coding tasks" can never disagree.
/// </summary>
public sealed class SubscriptionCliCodingTaskCliLocator : ICodingTaskCliLocator
{
    public string? FindExecutable(string command)
        => SubscriptionCliDiscovery.FindExecutableCandidates(command).FirstOrDefault();
}
