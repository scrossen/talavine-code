using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SwarmCore.Mcp.ExternalClients;

internal enum TomlStatementKind
{
    Blank,
    Comment,
    Table,
    ArrayTable,
    KeyValue,
}

internal abstract record TomlValue;

internal sealed record TomlString(string Value) : TomlValue;

internal sealed record TomlArray(IReadOnlyList<TomlValue> Items) : TomlValue;

internal sealed record TomlInlineTable(IReadOnlyList<TomlKeyValue> Entries) : TomlValue;

/// <summary>A boolean, number, or date-time kept as its validated source text.</summary>
internal sealed record TomlScalar(string Raw) : TomlValue;

internal sealed record TomlKeyValue(IReadOnlyList<string> Key, TomlValue Value);

/// <summary>
/// One logical line of a TOML document. <see cref="Start"/>/<see cref="End"/> are character
/// offsets into the original text; <see cref="End"/> includes the terminating newline, and a
/// multi-line value makes one statement span several physical lines.
/// </summary>
internal sealed record TomlStatement(
    TomlStatementKind Kind,
    int Start,
    int End,
    int Line,
    IReadOnlyList<string> Path,
    IReadOnlyList<string> Key,
    TomlValue? Value)
{
    /// <summary>For key/value statements: the table path followed by the (dotted) key.</summary>
    public IReadOnlyList<string> FullPath => Kind == TomlStatementKind.KeyValue ? [.. Path, .. Key] : Path;
}

/// <summary>
/// A strict structural reader for TOML documents. It decodes strings, arrays, and inline
/// tables, validates scalar syntax, and rejects redefined keys and tables, while recording
/// exact offsets so an editor can replace or remove individual tables and leave every
/// other byte of the document untouched. Anything it cannot delimit with certainty is
/// reported as an error, so callers refuse to edit rather than risk corrupting the file.
/// </summary>
internal sealed class TomlDocument
{
    private TomlDocument(string text, IReadOnlyList<TomlStatement> statements)
    {
        Text = text;
        Statements = statements;
    }

    public string Text { get; }

    public IReadOnlyList<TomlStatement> Statements { get; }

    public static bool TryParse(string text, out TomlDocument? document, out string? error)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            var parser = new Parser(text);
            document = new TomlDocument(text, parser.ParseDocument());
            error = null;
            return true;
        }
        catch (TomlParseException ex)
        {
            document = null;
            error = ex.Message;
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidOperationException or FormatException)
        {
            // Fail closed: a document this reader cannot fully account for is never edited.
            document = null;
            error = "the file uses syntax that could not be read safely";
            return false;
        }
    }

    private sealed class TomlParseException(string message) : Exception(message);

    private sealed class Parser(string text)
    {
        private static readonly Regex DecimalInteger = new(@"^[+-]?(0|[1-9](_?[0-9])*)$", RegexOptions.CultureInvariant);
        private static readonly Regex PrefixedInteger = new(@"^(0x[0-9A-Fa-f](_?[0-9A-Fa-f])*|0o[0-7](_?[0-7])*|0b[01](_?[01])*)$", RegexOptions.CultureInvariant);
        private static readonly Regex Float = new(@"^[+-]?(0|[1-9](_?[0-9])*)((\.[0-9](_?[0-9])*)([eE][+-]?[0-9](_?[0-9])*)?|[eE][+-]?[0-9](_?[0-9])*)$", RegexOptions.CultureInvariant);
        private static readonly Regex SpecialFloat = new(@"^[+-]?(inf|nan)$", RegexOptions.CultureInvariant);
        private static readonly Regex LocalDate = new(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}$", RegexOptions.CultureInvariant);
        private static readonly Regex DateTime = new(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}[Tt ][0-9]{2}:[0-9]{2}(:[0-9]{2}(\.[0-9]+)?)?([Zz]|[+-][0-9]{2}:[0-9]{2})?$", RegexOptions.CultureInvariant);
        private static readonly Regex LocalTime = new(@"^[0-9]{2}:[0-9]{2}(:[0-9]{2}(\.[0-9]+)?)?$", RegexOptions.CultureInvariant);

        private readonly Semantics _semantics = new();
        private int _pos;
        private int _line = 1;

        public List<TomlStatement> ParseDocument()
        {
            var statements = new List<TomlStatement>();
            IReadOnlyList<string> currentTable = [];

            while (_pos < text.Length)
            {
                var start = _pos;
                var line = _line;
                SkipInlineWhitespace();

                if (AtEnd || AtNewline)
                {
                    ConsumeNewlineOrEnd();
                    statements.Add(new TomlStatement(TomlStatementKind.Blank, start, _pos, line, [], [], null));
                    continue;
                }

                if (Current == '#')
                {
                    SkipComment();
                    ConsumeNewlineOrEnd();
                    statements.Add(new TomlStatement(TomlStatementKind.Comment, start, _pos, line, [], [], null));
                    continue;
                }

                if (Current == '[')
                {
                    var isArrayTable = Peek(1) == '[';
                    _pos += isArrayTable ? 2 : 1;
                    SkipInlineWhitespace();
                    var path = ParseKey();
                    SkipInlineWhitespace();
                    Expect(']');
                    if (isArrayTable)
                        Expect(']');
                    FinishLine();

                    if (isArrayTable)
                        _semantics.DefineArrayTable(path, line);
                    else
                        _semantics.DefineTable(path, line);

                    currentTable = path;
                    statements.Add(new TomlStatement(
                        isArrayTable ? TomlStatementKind.ArrayTable : TomlStatementKind.Table,
                        start, _pos, line, path, [], null));
                    continue;
                }

                var key = ParseKey();
                SkipInlineWhitespace();
                Expect('=');
                SkipInlineWhitespace();
                var value = ParseValue();
                FinishLine();
                _semantics.DefineKey(key, value, line);
                statements.Add(new TomlStatement(TomlStatementKind.KeyValue, start, _pos, line, currentTable, key, value));
            }

            return statements;
        }

        private bool AtEnd => _pos >= text.Length;

        private char Current => text[_pos];

        private bool AtNewline => !AtEnd && (Current == '\n' || (Current == '\r' && Peek(1) == '\n'));

        private char Peek(int offset) => _pos + offset < text.Length ? text[_pos + offset] : '\0';

        private TomlParseException Error(string message) => new($"line {_line}: {message}");

        private void SkipInlineWhitespace()
        {
            while (!AtEnd && (Current == ' ' || Current == '\t'))
                _pos++;
        }

        private void SkipComment()
        {
            while (!AtEnd && !AtNewline)
            {
                if (Current == '\r')
                    throw Error("a carriage return must be followed by a line feed");
                _pos++;
            }
        }

        private void ConsumeNewlineOrEnd()
        {
            if (AtEnd)
                return;
            if (Current == '\r' && Peek(1) == '\n')
                _pos += 2;
            else if (Current == '\n')
                _pos++;
            else
                throw Error("expected the end of the line");
            _line++;
        }

        private void FinishLine()
        {
            SkipInlineWhitespace();
            if (!AtEnd && Current == '#')
                SkipComment();
            ConsumeNewlineOrEnd();
        }

        private void Expect(char expected)
        {
            if (AtEnd || Current != expected)
                throw Error($"expected '{expected}'");
            _pos++;
        }

        private void SkipWhitespaceCommentsAndNewlines()
        {
            while (!AtEnd)
            {
                SkipInlineWhitespace();
                if (AtEnd)
                    return;
                if (Current == '#')
                {
                    SkipComment();
                    continue;
                }

                if (AtNewline)
                {
                    ConsumeNewlineOrEnd();
                    continue;
                }

                return;
            }
        }

        private List<string> ParseKey()
        {
            var parts = new List<string> { ParseSimpleKey() };
            while (true)
            {
                var save = _pos;
                SkipInlineWhitespace();
                if (!AtEnd && Current == '.')
                {
                    _pos++;
                    SkipInlineWhitespace();
                    parts.Add(ParseSimpleKey());
                    continue;
                }

                _pos = save;
                return parts;
            }
        }

        private string ParseSimpleKey()
        {
            if (AtEnd)
                throw Error("expected a key");
            if (Current == '"')
            {
                if (Peek(1) == '"' && Peek(2) == '"')
                    throw Error("multi-line strings cannot be keys");
                return ParseBasicString();
            }

            if (Current == '\'')
            {
                if (Peek(1) == '\'' && Peek(2) == '\'')
                    throw Error("multi-line strings cannot be keys");
                return ParseLiteralString();
            }

            var start = _pos;
            while (!AtEnd && IsBareKeyCharacter(Current))
                _pos++;
            if (_pos == start)
                throw Error("expected a key");
            return text[start.._pos];
        }

        private static bool IsBareKeyCharacter(char c) =>
            c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-';

        private TomlValue ParseValue()
        {
            if (AtEnd)
                throw Error("expected a value");

            switch (Current)
            {
                case '"':
                    return new TomlString(Peek(1) == '"' && Peek(2) == '"' ? ParseMultilineBasicString() : ParseBasicString());
                case '\'':
                    return new TomlString(Peek(1) == '\'' && Peek(2) == '\'' ? ParseMultilineLiteralString() : ParseLiteralString());
                case '[':
                    return ParseArray();
                case '{':
                    return ParseInlineTable();
                default:
                    return ParseScalar();
            }
        }

        private TomlArray ParseArray()
        {
            Expect('[');
            var items = new List<TomlValue>();
            while (true)
            {
                SkipWhitespaceCommentsAndNewlines();
                if (AtEnd)
                    throw Error("unterminated array");
                if (Current == ']')
                {
                    _pos++;
                    return new TomlArray(items);
                }

                items.Add(ParseValue());
                SkipWhitespaceCommentsAndNewlines();
                if (AtEnd)
                    throw Error("unterminated array");
                if (Current == ',')
                {
                    _pos++;
                    continue;
                }

                if (Current == ']')
                {
                    _pos++;
                    return new TomlArray(items);
                }

                throw Error("expected ',' or ']' in an array");
            }
        }

        private TomlInlineTable ParseInlineTable()
        {
            Expect('{');
            var entries = new List<TomlKeyValue>();
            var local = new Semantics();
            while (true)
            {
                // TOML 1.1 permits newlines, comments, and a trailing comma here; accepting
                // them is safe because the editor never rewrites an inline table.
                SkipWhitespaceCommentsAndNewlines();
                if (AtEnd)
                    throw Error("unterminated inline table");
                if (Current == '}')
                {
                    _pos++;
                    return new TomlInlineTable(entries);
                }

                var key = ParseKey();
                SkipInlineWhitespace();
                Expect('=');
                SkipInlineWhitespace();
                var value = ParseValue();
                local.DefineKey(key, value, _line);
                entries.Add(new TomlKeyValue(key, value));
                SkipWhitespaceCommentsAndNewlines();
                if (AtEnd)
                    throw Error("unterminated inline table");
                if (Current == ',')
                {
                    _pos++;
                    continue;
                }

                if (Current == '}')
                {
                    _pos++;
                    return new TomlInlineTable(entries);
                }

                throw Error("expected ',' or '}' in an inline table");
            }
        }

        private TomlScalar ParseScalar()
        {
            var start = _pos;
            while (!AtEnd && IsScalarCharacter(Current))
                _pos++;

            // A date and a time may be separated by a single space.
            var raw = text[start.._pos];
            if (LocalDate.IsMatch(raw) && !AtEnd && Current == ' ' && char.IsAsciiDigit(Peek(1)) && char.IsAsciiDigit(Peek(2)) && Peek(3) == ':')
            {
                _pos++;
                while (!AtEnd && IsScalarCharacter(Current))
                    _pos++;
                raw = text[start.._pos];
            }

            if (raw.Length == 0)
                throw Error("expected a value");

            if (raw is "true" or "false"
                || DecimalInteger.IsMatch(raw)
                || PrefixedInteger.IsMatch(raw)
                || Float.IsMatch(raw)
                || SpecialFloat.IsMatch(raw)
                || LocalDate.IsMatch(raw)
                || DateTime.IsMatch(raw)
                || LocalTime.IsMatch(raw))
            {
                return new TomlScalar(raw);
            }

            throw Error($"'{Truncate(raw)}' is not a valid value");
        }

        private static bool IsScalarCharacter(char c) =>
            c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' or '+' or '.' or ':';

        private static string Truncate(string value) => value.Length <= 24 ? value : value[..24] + "…";

        private string ParseBasicString()
        {
            Expect('"');
            var builder = new StringBuilder();
            while (true)
            {
                if (AtEnd || AtNewline || Current == '\r')
                    throw Error("unterminated string");
                var c = Current;
                if (c == '"')
                {
                    _pos++;
                    return builder.ToString();
                }

                if (c == '\\')
                {
                    _pos++;
                    AppendEscape(builder);
                    continue;
                }

                RejectControlCharacter(c, allowNewlines: false);
                builder.Append(c);
                _pos++;
            }
        }

        private string ParseMultilineBasicString()
        {
            _pos += 3;
            TrimLeadingNewline();
            var builder = new StringBuilder();
            while (true)
            {
                if (AtEnd)
                    throw Error("unterminated multi-line string");
                var c = Current;
                if (c == '"' && Peek(1) == '"' && Peek(2) == '"')
                {
                    var quotes = CountRun('"');
                    if (quotes > 5)
                        throw Error("too many quotes at the end of a multi-line string");
                    builder.Append('"', quotes - 3);
                    _pos += quotes;
                    return builder.ToString();
                }

                if (c == '\\')
                {
                    // A backslash at the end of a line trims the newline and following whitespace.
                    var probe = _pos + 1;
                    while (probe < text.Length && (text[probe] == ' ' || text[probe] == '\t'))
                        probe++;
                    if (probe < text.Length && (text[probe] == '\n' || (text[probe] == '\r' && probe + 1 < text.Length && text[probe + 1] == '\n')))
                    {
                        _pos = probe;
                        while (!AtEnd && (Current == ' ' || Current == '\t' || AtNewline))
                        {
                            if (AtNewline)
                                ConsumeNewlineOrEnd();
                            else
                                _pos++;
                        }

                        continue;
                    }

                    _pos++;
                    AppendEscape(builder);
                    continue;
                }

                if (AtNewline)
                {
                    builder.Append('\n');
                    ConsumeNewlineOrEnd();
                    continue;
                }

                RejectControlCharacter(c, allowNewlines: false);
                builder.Append(c);
                _pos++;
            }
        }

        private string ParseLiteralString()
        {
            Expect('\'');
            var start = _pos;
            while (true)
            {
                if (AtEnd || AtNewline || Current == '\r')
                    throw Error("unterminated string");
                if (Current == '\'')
                {
                    var value = text[start.._pos];
                    _pos++;
                    return value;
                }

                RejectControlCharacter(Current, allowNewlines: false);
                _pos++;
            }
        }

        private string ParseMultilineLiteralString()
        {
            _pos += 3;
            TrimLeadingNewline();
            var builder = new StringBuilder();
            while (true)
            {
                if (AtEnd)
                    throw Error("unterminated multi-line string");
                if (Current == '\'' && Peek(1) == '\'' && Peek(2) == '\'')
                {
                    var quotes = CountRun('\'');
                    if (quotes > 5)
                        throw Error("too many quotes at the end of a multi-line string");
                    builder.Append('\'', quotes - 3);
                    _pos += quotes;
                    return builder.ToString();
                }

                if (AtNewline)
                {
                    builder.Append('\n');
                    ConsumeNewlineOrEnd();
                    continue;
                }

                RejectControlCharacter(Current, allowNewlines: false);
                builder.Append(Current);
                _pos++;
            }
        }

        private void TrimLeadingNewline()
        {
            if (AtNewline)
                ConsumeNewlineOrEnd();
        }

        private int CountRun(char c)
        {
            var count = 0;
            while (_pos + count < text.Length && text[_pos + count] == c)
                count++;
            return count;
        }

        private void RejectControlCharacter(char c, bool allowNewlines)
        {
            if (c == '\t' || (allowNewlines && c == '\n'))
                return;
            if (c < 0x20 || c == 0x7F)
                throw Error("control characters are not allowed in strings");
        }

        private void AppendEscape(StringBuilder builder)
        {
            if (AtEnd)
                throw Error("unterminated escape sequence");
            var c = Current;
            _pos++;
            switch (c)
            {
                case 'b': builder.Append('\b'); return;
                case 't': builder.Append('\t'); return;
                case 'n': builder.Append('\n'); return;
                case 'f': builder.Append('\f'); return;
                case 'r': builder.Append('\r'); return;
                case 'e': builder.Append('\u001B'); return;
                case '"': builder.Append('"'); return;
                case '\\': builder.Append('\\'); return;
                case 'x': AppendCodePoint(builder, 2); return;
                case 'u': AppendCodePoint(builder, 4); return;
                case 'U': AppendCodePoint(builder, 8); return;
                default:
                    throw Error($"'\\{c}' is not a valid escape sequence");
            }
        }

        private void AppendCodePoint(StringBuilder builder, int digits)
        {
            if (_pos + digits > text.Length)
                throw Error("incomplete unicode escape");
            var hex = text.Substring(_pos, digits);
            if (hex.Any(ch => !Uri.IsHexDigit(ch))
                || !uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var codePoint)
                || codePoint > 0x10FFFF
                || codePoint is >= 0xD800 and <= 0xDFFF)
            {
                throw Error($"'{hex}' is not a valid unicode escape");
            }

            _pos += digits;
            builder.Append(char.ConvertFromUtf32((int)codePoint));
        }
    }

    /// <summary>
    /// Tracks which paths are tables, dotted-key tables, array tables, and values so that
    /// redefinitions are rejected the way a conforming TOML parser rejects them.
    /// </summary>
    private sealed class Semantics
    {
        private readonly HashSet<string> _explicitTables = new(StringComparer.Ordinal);
        private readonly HashSet<string> _dottedTables = new(StringComparer.Ordinal);
        private readonly HashSet<string> _values = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _arrayTables = new(StringComparer.Ordinal);
        private string _current = string.Empty;

        public void DefineTable(IReadOnlyList<string> path, int line)
        {
            var key = ResolveHeader(path, line);
            if (_arrayTables.ContainsKey(key))
                throw new TomlParseException($"line {line}: '{Describe(path)}' is already an array of tables");
            if (_explicitTables.Contains(key) || _dottedTables.Contains(key))
                throw new TomlParseException($"line {line}: table '{Describe(path)}' is defined more than once");
            if (_values.Contains(key))
                throw new TomlParseException($"line {line}: '{Describe(path)}' is already a value");

            _explicitTables.Add(key);
            _current = key;
        }

        public void DefineArrayTable(IReadOnlyList<string> path, int line)
        {
            var key = ResolveHeader(path, line);
            if (_explicitTables.Contains(key) || _dottedTables.Contains(key) || _values.Contains(key))
                throw new TomlParseException($"line {line}: '{Describe(path)}' is already defined and cannot become an array of tables");

            var index = _arrayTables.TryGetValue(key, out var count) ? count : 0;
            _arrayTables[key] = index + 1;
            _current = key + "#" + index.ToString(CultureInfo.InvariantCulture);
        }

        public void DefineKey(IReadOnlyList<string> key, TomlValue value, int line)
        {
            var path = _current;
            for (var i = 0; i < key.Count - 1; i++)
            {
                path = Append(path, key[i]);
                if (_values.Contains(path) || _arrayTables.ContainsKey(path) || _explicitTables.Contains(path))
                    throw new TomlParseException($"line {line}: key '{Describe(key)}' extends a value or table that is already defined");
                _dottedTables.Add(path);
            }

            path = Append(path, key[^1]);
            if (_values.Contains(path) || _explicitTables.Contains(path) || _dottedTables.Contains(path) || _arrayTables.ContainsKey(path))
                throw new TomlParseException($"line {line}: key '{Describe(key)}' is defined more than once");
            _values.Add(path);
        }

        private string ResolveHeader(IReadOnlyList<string> path, int line)
        {
            var resolved = string.Empty;
            for (var i = 0; i < path.Count; i++)
            {
                resolved = Append(resolved, path[i]);
                if (i == path.Count - 1)
                    break;
                if (_values.Contains(resolved))
                    throw new TomlParseException($"line {line}: '{Describe(path)}' extends a value that is already defined");
                if (_arrayTables.TryGetValue(resolved, out var count))
                    resolved += "#" + (count - 1).ToString(CultureInfo.InvariantCulture);
            }

            return resolved;
        }

        private static string Append(string path, string segment) =>
            path + "/" + segment.Length.ToString(CultureInfo.InvariantCulture) + ":" + segment;

        private static string Describe(IReadOnlyList<string> path) => string.Join('.', path);
    }
}
