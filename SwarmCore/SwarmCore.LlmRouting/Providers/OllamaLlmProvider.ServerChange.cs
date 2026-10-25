using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting.Providers;

/// <summary>
/// A different daemon can start answering at this provider's endpoint while
/// the process runs: Talavine retires the daemon it started when the user's
/// Ollama app takes over the port, or starts one where none was running. The
/// windows this adapter grew and the windows recorded for this provider were
/// learned from the old daemon. Kept, they would pin the new daemon's runners
/// to the old grants (a grown 8K window reloading a 32K-default runner at 8K)
/// and cap routing at the old default for hours.
/// </summary>
public partial class OllamaLlmProvider : ILocalRuntimeServerChangeObserver
{
    public void OnLocalRuntimeServerChanged(string providerName)
    {
        // A LAN node shares this implementation under its own provider name;
        // only the provider whose endpoint changed server forgets anything.
        if (!string.Equals(providerName?.Trim(), ProviderName, StringComparison.OrdinalIgnoreCase)) return;

        _contextWindowGrants.Clear();
        _contextWindowGrowthBlockedUntil.Clear();
        // Whether the old server served over-window requests whole says
        // nothing about the new one.
        _nodeServesWhole.Clear();
        _contextWindows?.ForgetProvider(ProviderName);
        // The new server can serve a different model directory, artifacts and
        // capabilities, and a CPU-grammar workaround learned on the old one
        // does not apply to it.
        ClearModelCache();
        _visionCapabilityCache.Clear();
        _cpuGrammarRecoveryUntil.Clear();
    }
}
