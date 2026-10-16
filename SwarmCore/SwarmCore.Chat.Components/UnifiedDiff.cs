namespace SwarmCore.Chat.Components;

public enum DiffLineKind { Context, Add, Delete, Hunk, Meta }

/// <summary>One classified diff line. For Add/Delete/Context, <see cref="Text"/> excludes the leading sign.</summary>
public sealed record DiffLine(DiffLineKind Kind, string Text);

/// <summary>Line classifier for unified diffs and the sign-prefixed preview format of the edit tools.</summary>
public static class UnifiedDiff
{
    public static IReadOnlyList<DiffLine> Parse(string? diff)
    {
        var result = new List<DiffLine>();
        if (string.IsNullOrEmpty(diff))
        {
            return result;
        }

        var lines = diff.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var count = lines.Length;
        if (count > 0 && lines[count - 1].Length == 0)
        {
            count--;
        }

        var inHunk = false;
        for (var i = 0; i < count; i++)
        {
            var line = lines[i];

            if (line.StartsWith("diff --git", StringComparison.Ordinal))
            {
                inHunk = false;
                result.Add(new DiffLine(DiffLineKind.Meta, line));
            }
            else if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                inHunk = true;
                result.Add(new DiffLine(DiffLineKind.Hunk, line));
            }
            else if (!inHunk && IsHeaderLine(line, i + 1 < count ? lines[i + 1] : null, i > 0 ? lines[i - 1] : null))
            {
                result.Add(new DiffLine(DiffLineKind.Meta, line));
            }
            else if (line.StartsWith("\\ ", StringComparison.Ordinal) || IsEllipsisFooter(line))
            {
                result.Add(new DiffLine(DiffLineKind.Meta, line));
            }
            else if (line.Length > 0 && line[0] == '+')
            {
                result.Add(new DiffLine(DiffLineKind.Add, line[1..]));
            }
            else if (line.Length > 0 && line[0] == '-')
            {
                result.Add(new DiffLine(DiffLineKind.Delete, line[1..]));
            }
            else if (line.Length > 0 && line[0] == ' ')
            {
                result.Add(new DiffLine(DiffLineKind.Context, line[1..]));
            }
            else
            {
                result.Add(new DiffLine(DiffLineKind.Context, line));
            }
        }

        return result;
    }

    /// <summary>True when the text has at least one change/hunk line and every non-empty line looks like diff output.</summary>
    public static bool LooksLikeDiff(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var changes = 0;
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (raw.Length == 0)
            {
                continue;
            }

            switch (raw[0])
            {
                case '+':
                case '-':
                    changes++;
                    break;
                case '@' when raw.StartsWith("@@", StringComparison.Ordinal):
                    changes++;
                    break;
                case ' ':
                case '\\':
                    break;
                default:
                    if (!(raw.StartsWith("diff ", StringComparison.Ordinal)
                          || raw.StartsWith("index ", StringComparison.Ordinal)
                          || IsEllipsisFooter(raw)))
                    {
                        return false;
                    }

                    break;
            }
        }

        return changes > 0;
    }

    private static bool IsEllipsisFooter(string line) =>
        line.StartsWith("... (", StringComparison.Ordinal) || line.StartsWith("… (", StringComparison.Ordinal);

    private static bool IsHeaderLine(string line, string? next, string? previous)
    {
        if (line.StartsWith("index ", StringComparison.Ordinal)
            || line.StartsWith("new file mode", StringComparison.Ordinal)
            || line.StartsWith("deleted file mode", StringComparison.Ordinal)
            || line.StartsWith("old mode", StringComparison.Ordinal)
            || line.StartsWith("new mode", StringComparison.Ordinal)
            || line.StartsWith("similarity index", StringComparison.Ordinal)
            || line.StartsWith("rename from", StringComparison.Ordinal)
            || line.StartsWith("rename to", StringComparison.Ordinal)
            || line.StartsWith("Binary files", StringComparison.Ordinal))
        {
            return true;
        }

        if (line.StartsWith("--- ", StringComparison.Ordinal))
        {
            return next is not null && next.StartsWith("+++ ", StringComparison.Ordinal);
        }

        return line.StartsWith("+++ ", StringComparison.Ordinal)
               && previous is not null && previous.StartsWith("--- ", StringComparison.Ordinal);
    }
}
