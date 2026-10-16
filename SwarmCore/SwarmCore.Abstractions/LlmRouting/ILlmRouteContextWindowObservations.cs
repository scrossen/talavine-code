namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// The context window each serving runtime most recently reported for a model,
/// such as the num_ctx a local Ollama runner was loaded with (its /api/ps
/// context_length). That runtime window, not the model's trained maximum, is
/// what a request must fit. Readers never probe: routing consumes the latest
/// observation, and a stale one is treated as unknown.
/// </summary>
public interface ILlmRouteContextWindowObservations
{
    void Record(string provider, string model, int windowTokens);

    /// <summary>
    /// The window a request that sets no window of its own gets for
    /// <paramref name="model"/>, never more than its measured default
    /// (<see cref="RecordDefaultWindow"/>).
    /// </summary>
    bool TryGet(string provider, string model, out int windowTokens);

    /// <summary>
    /// An estimate of the window a runtime gives a text model it has not
    /// loaded yet: its default context length, inferred as the largest window
    /// among the text runners it currently reports. A model's own observation
    /// always wins over this estimate.
    /// </summary>
    bool TryGetRuntimeDefault(string provider, out int windowTokens)
    {
        windowTokens = 0;
        return false;
    }

    /// <summary>
    /// Records the window the runtime gives <paramref name="model"/> when a
    /// request sets none, measured when the runtime rejected such a request
    /// with its window. A runner loaded larger for an oversized request
    /// reports that larger window, which a request that cannot set a window
    /// (an OpenAI-compatible call) never gets, so the default caps what is
    /// reported for the model.
    /// </summary>
    void RecordDefaultWindow(string provider, string model, int windowTokens)
    {
    }

    /// <summary>
    /// Records the largest window the runtime grows to for a request to
    /// <paramref name="model"/> that its current window cannot hold (a native
    /// Ollama runtime that rejects an over-window request instead of truncating
    /// it). A capability, not a runner report: it never feeds
    /// <see cref="TryGet"/> or <see cref="TryGetRuntimeDefault"/>.
    /// </summary>
    void RecordMaximumWindow(string provider, string model, int windowTokens)
    {
    }

    /// <summary>
    /// The window recorded by <see cref="RecordMaximumWindow"/>, while it is
    /// current and not suspended.
    /// </summary>
    bool TryGetMaximumWindow(string provider, string model, out int windowTokens)
    {
        windowTokens = 0;
        return false;
    }

    /// <summary>
    /// Withdraws <paramref name="model"/>'s largest window for
    /// <paramref name="duration"/>: a grown window failed to load on the
    /// device, so the runtime serves the model at its current window
    /// meanwhile. Recording the window again does not end the suspension.
    /// </summary>
    void SuspendMaximumWindow(string provider, string model, TimeSpan duration)
    {
    }

    /// <summary>
    /// Drops every window recorded for <paramref name="provider"/>. Called when
    /// a different server answers at that provider's endpoint, so the old
    /// server's runner windows, measured defaults, largest windows, and
    /// suspensions no longer apply.
    /// </summary>
    void ForgetProvider(string provider)
    {
    }
}
