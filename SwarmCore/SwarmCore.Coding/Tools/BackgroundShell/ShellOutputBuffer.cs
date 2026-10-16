using System.Text;

namespace SwarmCore.Coding.Tools.BackgroundShell;

/// <summary>
/// Bounded output of one background shell, addressed by absolute character position: the first
/// <c>headCapacity</c> characters are kept, then a ring of the last <c>tailCapacity</c>. Middle output that falls out of
/// the ring is dropped and reported as omitted. Not thread-safe; the owning shell serializes access.
/// </summary>
internal sealed class ShellOutputBuffer(int headCapacity, int tailCapacity)
{
    private readonly StringBuilder _head = new();
    private readonly char[] _ring = new char[tailCapacity];

    /// <summary>Total characters ever appended.</summary>
    public long Written { get; private set; }

    /// <summary>First absolute position still held after the head.</summary>
    private long TailStart => Math.Max(_head.Length, Written - _ring.Length);

    public void Append(string text)
    {
        var i = 0;
        if (Written == _head.Length && _head.Length < headCapacity)
        {
            var take = Math.Min(headCapacity - _head.Length, text.Length);
            _head.Append(text, 0, take);
            Written += take;
            i = take;
        }
        for (; i < text.Length; i++)
        {
            _ring[(int)((Written - _head.Length) % _ring.Length)] = text[i];
            Written++;
        }
    }

    /// <summary>
    /// Text from <paramref name="from"/> to the end. When more than <paramref name="max"/> characters are available the
    /// start and end are kept around a marker; <paramref name="omitted"/> counts characters not returned (dropped from the
    /// buffer or elided here).
    /// </summary>
    public string Read(long from, int max, out long omitted)
    {
        omitted = 0;
        from = Math.Clamp(from, 0, Written);
        var sb = new StringBuilder();
        var position = from;
        if (position < _head.Length)
        {
            sb.Append(_head, (int)position, _head.Length - (int)position);
            position = _head.Length;
        }
        var tailStart = TailStart;
        if (position < tailStart)
        {
            omitted += tailStart - position;
            if (sb.Length > 0) sb.Append(Marker(tailStart - position));
            position = tailStart;
        }
        for (; position < Written; position++)
            sb.Append(_ring[(int)((position - _head.Length) % _ring.Length)]);

        if (sb.Length <= max) return sb.ToString();
        var keepHead = max / 4;
        var keepTail = max - keepHead;
        var elided = sb.Length - max;
        omitted += elided;
        return sb.ToString(0, keepHead) + Marker(elided) + sb.ToString(sb.Length - keepTail, keepTail);
    }

    private static string Marker(long characters) => "\n…[" + characters.ToString("N0") + " characters omitted]…\n";
}
