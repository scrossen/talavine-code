namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// Told when a different server now answers at a local runtime's fixed
/// endpoint, for example after Talavine retired the Ollama daemon it had
/// started because the user's Ollama app took over the same port, or after it
/// started its own daemon where none was running. Whatever a provider learned
/// from the previous server (the windows it grew, the default window it
/// measured, the runner windows it observed) describes a process that no
/// longer answers there.
/// </summary>
public interface ILocalRuntimeServerChangeObserver
{
    /// <param name="providerName">The provider whose endpoint changed server, such as <c>Ollama</c>.</param>
    void OnLocalRuntimeServerChanged(string providerName);
}
