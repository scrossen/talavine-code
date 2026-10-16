using System.Collections.Concurrent;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting.Services;

/// <summary>
/// Process-local <see cref="ILlmDegradedRouteNoticeSink"/>. Notices live only
/// between route selection and the end of the same turn, so an in-memory map
/// with a short expiry is the whole implementation. Bounded on both age and
/// count: a turn that errors out never takes its notice, and nothing else
/// would ever clean it up.
/// </summary>
public sealed class InMemoryLlmDegradedRouteNoticeSink : ILlmDegradedRouteNoticeSink
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);
    private const int MaxEntries = 256;

    private readonly ConcurrentDictionary<string, Entry> _notices = new(StringComparer.Ordinal);

    public void Record(string? turnId, LlmDegradedRouteNotice notice)
    {
        if (string.IsNullOrWhiteSpace(turnId) || notice is null)
        {
            return;
        }

        Prune();
        _notices[turnId] = new Entry(notice, DateTimeOffset.UtcNow);
    }

    public void Clear(string? turnId)
    {
        if (string.IsNullOrWhiteSpace(turnId))
        {
            return;
        }

        _notices.TryRemove(turnId, out _);
    }

    public LlmDegradedRouteNotice? Take(string? turnId)
    {
        if (string.IsNullOrWhiteSpace(turnId))
        {
            return null;
        }

        if (!_notices.TryRemove(turnId, out var entry))
        {
            return null;
        }

        return DateTimeOffset.UtcNow - entry.RecordedAt > MaxAge ? null : entry.Notice;
    }

    private void Prune()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _notices)
        {
            if (now - pair.Value.RecordedAt > MaxAge)
            {
                _notices.TryRemove(pair.Key, out _);
            }
        }

        if (_notices.Count <= MaxEntries)
        {
            return;
        }

        foreach (var pair in _notices.OrderBy(item => item.Value.RecordedAt)
                     .Take(_notices.Count - MaxEntries)
                     .ToList())
        {
            _notices.TryRemove(pair.Key, out _);
        }
    }

    private readonly record struct Entry(LlmDegradedRouteNotice Notice, DateTimeOffset RecordedAt);
}
