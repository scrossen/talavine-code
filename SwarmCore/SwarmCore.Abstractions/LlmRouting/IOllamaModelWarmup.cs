namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// Allows callers to preload a specific model into Ollama's VRAM
/// so that the next request using that model doesn't incur a cold-start.
/// </summary>
public interface IOllamaModelWarmup
{
    /// <summary>
    /// Sends a lightweight request to Ollama that loads <paramref name="model"/>
    /// into VRAM without generating any output.
    /// </summary>
    Task PreloadModelAsync(string model, CancellationToken ct = default);
}
