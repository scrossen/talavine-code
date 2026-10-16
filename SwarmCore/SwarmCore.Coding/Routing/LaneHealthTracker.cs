using System.Collections.Concurrent;

namespace SwarmCore.Coding.Routing;

/// <summary>
/// Remembers lanes whose route failed (CLI missing or signed out, quota, auth, network) so
/// the orchestrator skips them for a cooldown instead of paying a failed attempt on every
/// turn. A successful turn clears the mark. Process-local by design: lane health is a
/// property of this machine and session, not something to persist.
/// <para>
/// Marks are keyed by lane name within a health scope (<see cref="CodingLaneSet.HealthScope"/>).
/// A host serving several profiles from one process gives each its own scope, so one profile's
/// failing route (its key, its quota) does not cool the same-named lane for another profile. The
/// null scope is the single shared scope a one-profile host (SwarmCode) uses.
/// </para>
/// </summary>
public sealed class LaneHealthTracker(TimeProvider? time = null)
{
    private readonly ConcurrentDictionary<string, (DateTimeOffset Until, string Reason)> _unavailable = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public void MarkUnavailable(string laneName, string reason, TimeSpan cooldown, string? scope = null)
    {
        if (cooldown <= TimeSpan.Zero) return;
        _unavailable[Key(scope, laneName)] = (_time.GetUtcNow() + cooldown, reason);
    }

    public void Clear(string laneName, string? scope = null) => _unavailable.TryRemove(Key(scope, laneName), out _);

    /// <summary>True when the lane is cooling down in <paramref name="scope"/>; <paramref name="reason"/> explains why.</summary>
    public bool IsUnavailable(string laneName, out string reason, string? scope = null)
    {
        reason = "";
        var key = Key(scope, laneName);
        if (!_unavailable.TryGetValue(key, out var entry)) return false;
        if (entry.Until <= _time.GetUtcNow())
        {
            _unavailable.TryRemove(key, out _);
            return false;
        }
        var remaining = entry.Until - _time.GetUtcNow();
        reason = $"{entry.Reason} (retrying in {Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))} min)";
        return true;
    }

    /// <summary>Separates scope and lane key; a unit separator cannot appear in a lane name typed in configuration.</summary>
    private const char ScopeSeparator = (char)0x1F;

    private static string Key(string? scope, string laneName)
        => string.IsNullOrWhiteSpace(scope) ? laneName : scope.Trim() + ScopeSeparator + laneName;
}
