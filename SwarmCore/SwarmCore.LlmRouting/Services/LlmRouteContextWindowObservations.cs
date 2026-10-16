using System.Collections.Concurrent;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting.Services;

/// <summary>
/// Process-wide cache of runtime-reported context windows. A runner can be
/// reloaded with a different window, so an observation expires after a few
/// hours and routing falls back to "unknown" rather than a stale number.
/// </summary>
public sealed class LlmRouteContextWindowObservations(TimeProvider? timeProvider = null)
    : ILlmRouteContextWindowObservations
{
    internal static readonly TimeSpan ObservationLifetime = TimeSpan.FromHours(6);
    private const int MaximumEntries = 512;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, (int Tokens, DateTimeOffset ObservedAt)> _windows =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (int Tokens, DateTimeOffset ObservedAt)> _defaults =
        new(StringComparer.OrdinalIgnoreCase);
    // Kept apart from the runner windows: a runtime that can grow a model's
    // window says nothing about what an unloaded model is served at.
    private readonly ConcurrentDictionary<string, (int Tokens, DateTimeOffset ObservedAt)> _maximums =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _suspendedMaximums =
        new(StringComparer.OrdinalIgnoreCase);

    public void Record(string provider, string model, int windowTokens)
        => Store(_windows, provider, model, windowTokens);

    public void RecordDefaultWindow(string provider, string model, int windowTokens)
        => Store(_defaults, provider, model, windowTokens);

    public void RecordMaximumWindow(string provider, string model, int windowTokens)
        => Store(_maximums, provider, model, windowTokens);

    public bool TryGetMaximumWindow(string provider, string model, out int windowTokens)
    {
        windowTokens = 0;
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model)) return false;
        var key = Key(provider, model);
        if (_suspendedMaximums.TryGetValue(key, out var until))
        {
            if (_time.GetUtcNow() < until) return false;
            _suspendedMaximums.TryRemove(new KeyValuePair<string, DateTimeOffset>(key, until));
        }

        return TryRead(_maximums, key, out windowTokens);
    }

    public void SuspendMaximumWindow(string provider, string model, TimeSpan duration)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model) || duration <= TimeSpan.Zero) return;
        var now = _time.GetUtcNow();
        if (_suspendedMaximums.Count >= MaximumEntries)
        {
            foreach (var (key, until) in _suspendedMaximums)
                if (until <= now) _suspendedMaximums.TryRemove(key, out _);
        }

        _suspendedMaximums[Key(provider, model)] = now + duration;
    }

    public void ForgetProvider(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider)) return;
        // "Ollama|" never matches a LAN node's "Ollama@node|" keys.
        var prefix = provider.Trim() + "|";
        foreach (var entries in new[] { _windows, _defaults, _maximums })
        {
            foreach (var key in entries.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) entries.TryRemove(key, out _);
            }
        }

        foreach (var key in _suspendedMaximums.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) _suspendedMaximums.TryRemove(key, out _);
        }
    }

    public bool TryGet(string provider, string model, out int windowTokens)
    {
        windowTokens = 0;
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model)) return false;
        var key = Key(provider, model);
        var observed = TryRead(_windows, key, out var loaded);
        var measured = TryRead(_defaults, key, out var defaultWindow);
        if (!observed && !measured) return false;
        windowTokens = observed && measured ? Math.Min(loaded, defaultWindow) : observed ? loaded : defaultWindow;
        return true;
    }

    /// <summary>
    /// An Ollama daemon loads a model without its own num_ctx at its default,
    /// which the API does not report. The text runners it already serves show
    /// that default; the largest is used so the estimate never shrinks a window
    /// below what the runtime demonstrably serves. A runner loaded larger than
    /// its model's measured default counts at that default. Embedding runners
    /// are loaded with their own small window and say nothing about text
    /// models. Other providers have no shared default, so they never get an
    /// estimate.
    /// </summary>
    public bool TryGetRuntimeDefault(string provider, out int windowTokens)
    {
        windowTokens = 0;
        if (!OllamaProviderFamily.IsOllamaFamily(provider)) return false;
        var prefix = provider.Trim() + "|";
        foreach (var key in _windows.Keys.Concat(_defaults.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || ModelWorkloadFamilies.IsEmbeddingOnly(key[prefix.Length..])
                || !TryGet(provider, key[prefix.Length..], out var served))
            {
                continue;
            }

            windowTokens = Math.Max(windowTokens, served);
        }

        return windowTokens > 0;
    }

    private void Store(
        ConcurrentDictionary<string, (int Tokens, DateTimeOffset ObservedAt)> entries,
        string provider,
        string model,
        int windowTokens)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model) || windowTokens <= 0) return;
        if (entries.Count >= MaximumEntries) Prune(entries);
        entries[Key(provider, model)] = (windowTokens, _time.GetUtcNow());
    }

    private bool TryRead(
        ConcurrentDictionary<string, (int Tokens, DateTimeOffset ObservedAt)> entries,
        string key,
        out int windowTokens)
    {
        windowTokens = 0;
        if (!entries.TryGetValue(key, out var observed)
            || _time.GetUtcNow() - observed.ObservedAt > ObservationLifetime)
        {
            return false;
        }

        windowTokens = observed.Tokens;
        return true;
    }

    private void Prune(ConcurrentDictionary<string, (int Tokens, DateTimeOffset ObservedAt)> entries)
    {
        var cutoff = _time.GetUtcNow() - ObservationLifetime;
        foreach (var (key, observed) in entries)
            if (observed.ObservedAt < cutoff) entries.TryRemove(key, out _);
    }

    private static string Key(string provider, string model)
        => provider.Trim() + "|" + LlmRouter.NormalizeOllamaModelTag(model.Trim());
}
