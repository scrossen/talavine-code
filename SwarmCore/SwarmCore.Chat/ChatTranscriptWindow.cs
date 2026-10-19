namespace SwarmCore.Chat;

/// <summary>Bounds rendered history while retaining access to every saved turn.</summary>
public sealed class ChatTranscriptWindow<T>(Func<T, Guid> identity, int pageSize = 40, Func<T, bool>? include = null)
{
    private readonly int _pageSize = pageSize > 0 ? pageSize : throw new ArgumentOutOfRangeException(nameof(pageSize));
    private string? _conversation;
    private Guid? _endId;

    public ChatTranscriptPage<T> Select(string conversation, IReadOnlyList<T> turns)
    {
        if (!string.Equals(_conversation, conversation, StringComparison.Ordinal))
        {
            _conversation = conversation;
            _endId = null;
        }

        var end = turns.Count;
        if (_endId is { } anchor)
        {
            var found = false;
            for (var i = turns.Count - 1; i >= 0; i--)
            {
                if (identity(turns[i]) != anchor) continue;
                end = i + 1;
                found = true;
                break;
            }
            if (!found) _endId = null;
        }

        var items = new List<T>(_pageSize);
        var start = end;
        for (var i = end - 1; i >= 0 && items.Count < _pageSize; i--)
        {
            start = i;
            if (include?.Invoke(turns[i]) == false) continue;
            items.Add(turns[i]);
        }
        items.Reverse();
        return new(items, start, end, turns.Count);
    }

    public void Earlier(string conversation, IReadOnlyList<T> turns)
    {
        var page = Select(conversation, turns);
        if (page.HasEarlier) _endId = identity(turns[page.StartIndex - 1]);
    }

    public void Newer(string conversation, IReadOnlyList<T> turns)
    {
        var page = Select(conversation, turns);
        var end = page.EndExclusive;
        var count = 0;
        while (end < turns.Count && count < _pageSize)
        {
            if (include?.Invoke(turns[end]) != false) count++;
            end++;
        }
        while (end < turns.Count && include?.Invoke(turns[end]) == false) end++;
        _endId = end < turns.Count ? identity(turns[end - 1]) : null;
    }

    public void Latest() => _endId = null;

    public void Reveal(string conversation, IReadOnlyList<T> turns, Guid turnId)
    {
        Select(conversation, turns);
        foreach (var turn in turns)
        {
            if (identity(turn) != turnId) continue;
            _endId = turnId;
            return;
        }
    }
}

public sealed record ChatTranscriptPage<T>(IReadOnlyList<T> Items, int StartIndex, int EndExclusive, int TotalCount)
{
    public bool HasEarlier => StartIndex > 0;
    public bool HasNewer => EndExclusive < TotalCount;
}
