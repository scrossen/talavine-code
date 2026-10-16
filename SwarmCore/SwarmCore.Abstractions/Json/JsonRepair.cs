using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SwarmCore.Abstractions.Json;

/// <summary>
/// Deterministic, non-LLM structural repair for JSON that language models emit
/// with the usual imperfections: wrapped in markdown code fences, buried in a
/// prose preamble/epilogue, unquoted object keys, trailing commas, single quotes
/// instead of double, JS-style comments, or truncated so the closing braces are
/// missing.
///
/// This is the ONE place structural JSON salvage lives. It does NOT reprompt the
/// model — that is a separate concern owned by the router and bulk categorizer.
/// Callers get a sequence of progressively-repaired candidate strings to try
/// deserializing, or a convenience <see cref="TryDeserialize{T}"/> that walks
/// them for you.
/// </summary>
public static class JsonRepair
{
    public enum ParseDisposition
    {
        Exact = 0,
        Extracted = 1,
        Repaired = 2,
        Failed = 3,
    }
    // Bare object/array keys: `{ key:` or `, key:` -> quote the key. Anchored to
    // a `{` `[` or `,` so values that merely contain `word:` are untouched.
    private static readonly Regex UnquotedKeyRegex = new(
        @"([\{\[,]\s*)([A-Za-z_$][A-Za-z0-9_$]*)\s*:",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // A comma immediately before a closing brace/bracket.
    private static readonly Regex TrailingCommaRegex = new(
        @",(\s*[}\]])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // JS-style comments: `// line` and `/* block */`. Applied only OUTSIDE string
    // literals (see StripComments), so a URL inside a value is safe.
    private static readonly JsonDocumentOptions LenientDocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Strips markdown code fences and any prose surrounding the JSON, then
    /// returns the outermost balanced JSON object or array as a substring.
    /// Falls back to the fence-stripped, trimmed text when no balanced structure
    /// is found (so a caller can still surface a useful error). Returns an empty
    /// string only for null/whitespace input.
    /// </summary>
    public static string ExtractJsonObject(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var cleaned = StripCodeFences(raw.Trim());

        // Pick whichever structure opens first — object or array.
        var firstBrace = cleaned.IndexOf('{');
        var firstBracket = cleaned.IndexOf('[');

        int start;
        char open, close;
        if (firstBrace >= 0 && (firstBracket < 0 || firstBrace <= firstBracket))
        {
            start = firstBrace;
            open = '{';
            close = '}';
        }
        else if (firstBracket >= 0)
        {
            start = firstBracket;
            open = '[';
            close = ']';
        }
        else
        {
            return cleaned;
        }

        var end = FindMatchingClose(cleaned, start, open, close);
        if (end > start)
        {
            return cleaned[start..(end + 1)];
        }

        // Unbalanced (truncated): take from the opener to the end so the
        // brace-balancing candidate can try to recover it.
        return cleaned[start..];
    }

    /// <summary>
    /// Produces a sequence of progressively-repaired candidate strings to try
    /// deserializing, most-faithful first. Every candidate is distinct; the raw
    /// extracted text is always first so already-valid JSON parses unchanged.
    /// </summary>
    public static IEnumerable<string> RepairCandidates(string? raw)
        => RepairCandidates(raw, includeTruncationCompletion: true);

    /// <summary>
    /// Produces conservative syntax-repair candidates without appending any
    /// missing closing strings, braces, or array elements.
    /// </summary>
    public static IEnumerable<string> CompleteRepairCandidates(string? raw)
        => RepairCandidates(raw, includeTruncationCompletion: false);

    /// <summary>
    /// Produces progressively-repaired candidates, optionally omitting the
    /// final EOF-completion step. Callers whose payload contains executable
    /// instructions can reject truncated arguments while still sharing every
    /// deterministic cosmetic repair in this class.
    /// </summary>
    public static IEnumerable<string> RepairCandidates(
        string? raw,
        bool includeTruncationCompletion)
    {
        var extracted = ExtractJsonObject(raw);
        if (string.IsNullOrWhiteSpace(extracted))
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);

        // The repair steps are cumulative: each stage layers one more
        // transformation on top of the previous stage's output, and we yield the
        // result after every stage so the earliest one that parses wins.
        var current = extracted;
        foreach (var stage in RepairStages(includeTruncationCompletion))
        {
            if (seen.Add(current))
            {
                yield return current;
            }

            current = stage(current);
        }

        if (seen.Add(current))
        {
            yield return current;
        }
    }

    private static IEnumerable<Func<string, string>> RepairStages(bool includeTruncationCompletion)
    {
        yield return QuoteUnquotedKeys;
        yield return RemoveTrailingCommas;
        yield return StripComments;
        yield return ConvertSingleQuotes;
        // Normalize only after an unambiguously single-quoted document has
        // been converted. NormalizeForeignLiterals deliberately tracks JSON
        // double-quoted strings; running it first would rewrite words such as
        // `None` inside a Python-style single-quoted string value.
        yield return NormalizeForeignLiterals;
        yield return EscapeControlCharactersInStrings;
        yield return CloseArraysBeforeObjectProperty;
        yield return RemoveUnexpectedClosingDelimiters;
        if (includeTruncationCompletion)
        {
            yield return BalanceBraces;
        }
    }

    /// <summary>
    /// Walks the repair candidates, deserializing each with <paramref name="options"/>,
    /// and returns the first that succeeds. On failure <paramref name="value"/> is
    /// default and <paramref name="issue"/> explains why.
    /// </summary>
    public static bool TryDeserialize<T>(
        string? raw,
        JsonSerializerOptions options,
        out T? value,
        out string issue)
        => TryDeserialize(raw, options, out value, out issue, out _);

    public static bool TryDeserialize<T>(
        string? raw,
        JsonSerializerOptions options,
        out T? value,
        out string issue,
        out ParseDisposition disposition)
    {
        value = default;
        issue = string.Empty;
        disposition = ParseDisposition.Failed;
        ArgumentNullException.ThrowIfNull(options);

        var any = false;
        var candidateIndex = 0;
        var trimmedRaw = raw?.Trim() ?? string.Empty;
        foreach (var candidate in RepairCandidates(raw))
        {
            any = true;
            try
            {
                var parsed = JsonSerializer.Deserialize<T>(candidate, options);
                if (parsed is null)
                {
                    issue = "The JSON object deserialized to null.";
                    candidateIndex++;
                    continue;
                }

                value = parsed;
                issue = string.Empty;
                disposition = candidateIndex == 0
                    ? string.Equals(candidate, trimmedRaw, StringComparison.Ordinal)
                        ? ParseDisposition.Exact
                        : ParseDisposition.Extracted
                    : ParseDisposition.Repaired;
                return true;
            }
            catch (JsonException ex)
            {
                issue = $"The model returned invalid JSON: {ex.Message}";
            }

            candidateIndex++;
        }

        if (!any)
        {
            issue = "The model returned no JSON object.";
        }

        return false;
    }

    /// <summary>
    /// Deserializes using conservative syntax repairs but never invents missing
    /// closing strings, braces, or array elements. Use this at typed provider
    /// boundaries where a truncated prefix must trigger the normal failure or
    /// fallback path instead of being accepted as a complete response.
    /// </summary>
    public static bool TryDeserializeComplete<T>(
        string? raw,
        JsonSerializerOptions options,
        out T? value,
        out string issue)
    {
        value = default;
        issue = string.Empty;
        ArgumentNullException.ThrowIfNull(options);

        var any = false;
        foreach (var candidate in CompleteRepairCandidates(raw))
        {
            any = true;
            try
            {
                var parsed = JsonSerializer.Deserialize<T>(candidate, options);
                if (parsed is null)
                {
                    issue = "The JSON object deserialized to null.";
                    continue;
                }

                value = parsed;
                issue = string.Empty;
                return true;
            }
            catch (JsonException ex)
            {
                issue = $"The model returned invalid JSON: {ex.Message}";
            }
        }

        if (!any)
        {
            issue = "The model returned no JSON object.";
        }

        return false;
    }

    /// <summary>
    /// Convenience over <see cref="TryDeserialize{T}"/> that also validates the
    /// parsed structure with lenient options first, returning null on failure.
    /// </summary>
    public static T? DeserializeOrDefault<T>(string? raw, JsonSerializerOptions options)
        => TryDeserialize<T>(raw, options, out var value, out _) ? value : default;

    /// <summary>
    /// True when some repair candidate parses as valid JSON of the given root
    /// kind. When <paramref name="expectArray"/> is null either root is accepted.
    /// </summary>
    public static bool TryNormalize(string? raw, bool? expectArray, out string normalized)
        => TryNormalizeCore(raw, expectArray, allowTruncatedRepair: true, out normalized);

    /// <summary>
    /// Normalizes only complete JSON structures; truncated prefixes are never
    /// made parseable by appending missing delimiters.
    /// </summary>
    public static bool TryNormalizeComplete(string? raw, bool? expectArray, out string normalized)
        => TryNormalizeCore(raw, expectArray, allowTruncatedRepair: false, out normalized);

    private static bool TryNormalizeCore(
        string? raw,
        bool? expectArray,
        bool allowTruncatedRepair,
        out string normalized)
    {
        normalized = string.Empty;
        var candidates = allowTruncatedRepair
            ? RepairCandidates(raw)
            : CompleteRepairCandidates(raw);
        foreach (var candidate in candidates)
        {
            try
            {
                using var doc = JsonDocument.Parse(candidate, LenientDocumentOptions);
                var kind = doc.RootElement.ValueKind;
                if (expectArray is true && kind != JsonValueKind.Array)
                {
                    continue;
                }

                if (expectArray is false && kind != JsonValueKind.Object)
                {
                    continue;
                }

                normalized = candidate;
                return true;
            }
            catch (JsonException)
            {
                // Try the next candidate.
            }
        }

        return false;
    }

    private static string StripCodeFences(string text)
    {
        var cleaned = text.Trim();
        if (!cleaned.StartsWith("```", StringComparison.Ordinal))
        {
            return cleaned;
        }

        // Drop the opening fence line (```json, ```JSON, or bare ```).
        var firstNewline = cleaned.IndexOf('\n');
        cleaned = firstNewline >= 0 ? cleaned[(firstNewline + 1)..] : cleaned[3..];

        var closingFence = cleaned.LastIndexOf("```", StringComparison.Ordinal);
        if (closingFence >= 0)
        {
            cleaned = cleaned[..closingFence];
        }

        return cleaned.Trim();
    }

    private static int FindMatchingClose(string text, int start, char open, char close)
    {
        var depth = 0;
        var quote = '\0';
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == quote) quote = '\0';
                continue;
            }

            if (c is '"' or '\'') quote = c;
            else if (c == open) depth++;
            else if (c == close)
            {
                depth--;
                if (depth == 0) return i;
            }
        }

        return -1;
    }

    private static string QuoteUnquotedKeys(string json)
        => UnquotedKeyRegex.Replace(json, "$1\"$2\":");

    private static string RemoveTrailingCommas(string json)
        => TrailingCommaRegex.Replace(json, "$1");

    /// <summary>
    /// Removes <c>// line</c> and <c>/* block */</c> comments that sit OUTSIDE
    /// string literals. A URL like <c>"http://x"</c> in a value is preserved.
    /// </summary>
    private static string StripComments(string json)
    {
        if (!json.Contains("//", StringComparison.Ordinal)
            && !json.Contains("/*", StringComparison.Ordinal))
        {
            return json;
        }

        var builder = new StringBuilder(json.Length);
        var quote = '\0';
        var escaped = false;
        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];
            if (quote != '\0')
            {
                builder.Append(c);
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == quote) quote = '\0';
                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                builder.Append(c);
                continue;
            }

            if (c == '/' && i + 1 < json.Length && json[i + 1] == '/')
            {
                // Skip to end of line.
                i += 2;
                while (i < json.Length && json[i] != '\n') i++;
                if (i < json.Length) builder.Append('\n');
                continue;
            }

            if (c == '/' && i + 1 < json.Length && json[i + 1] == '*')
            {
                // Skip to closing */.
                i += 2;
                while (i + 1 < json.Length && !(json[i] == '*' && json[i + 1] == '/')) i++;
                i++; // land on the '/'; loop's i++ moves past it
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    // Python literals used as bare JSON values. Only `True`, `False`, and
    // `None` have exact JSON equivalents; NaN/Infinity/undefined stay invalid
    // on purpose — silently converting them to null would erase meaning for
    // every JsonRepair consumer.
    private static readonly Regex ForeignLiteralRegex = new(
        @"\b(True|False|None)\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Rewrites Python value literals that models leak into JSON — `True`,
    /// `False`, `None` — to their exact JSON equivalents. Only text OUTSIDE
    /// string literals is touched, so a value like "None of the above" is
    /// preserved. Meaning-erasing rewrites (NaN/Infinity/undefined → null) are
    /// deliberately not performed.
    /// </summary>
    private static string NormalizeForeignLiterals(string json)
    {
        if (!ForeignLiteralRegex.IsMatch(json))
        {
            return json;
        }

        var builder = new StringBuilder(json.Length);
        var inString = false;
        var escaped = false;
        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];
            if (inString)
            {
                builder.Append(c);
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"')
            {
                inString = true;
                builder.Append(c);
                continue;
            }

            if (char.IsLetter(c))
            {
                // Collect the bare word and map Python literals to JSON ones.
                var start = i;
                var j = i;
                while (j < json.Length && char.IsLetter(json[j])) j++;
                var word = json[start..j];
                builder.Append(word switch
                {
                    "True" => "true",
                    "False" => "false",
                    "None" => "null",
                    _ => word
                });
                i = j - 1;
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Escapes raw control characters (literal newlines, tabs, carriage returns,
    /// and other chars below U+0020) that appear INSIDE string literals — a
    /// frequent local-model failure on multi-line values like reply drafts.
    /// Text outside strings is untouched, so formatting whitespace is preserved.
    /// </summary>
    private static string EscapeControlCharactersInStrings(string json)
    {
        var builder = new StringBuilder(json.Length);
        var inString = false;
        var escaped = false;
        foreach (var c in json)
        {
            if (inString)
            {
                if (escaped)
                {
                    builder.Append(c);
                    escaped = false;
                    continue;
                }

                if (c == '\\')
                {
                    builder.Append(c);
                    escaped = true;
                    continue;
                }

                if (c == '"')
                {
                    builder.Append(c);
                    inString = false;
                    continue;
                }

                if (c < ' ')
                {
                    builder.Append(c switch
                    {
                        '\n' => "\\n",
                        '\r' => "\\r",
                        '\t' => "\\t",
                        _ => $"\\u{(int)c:x4}"
                    });
                    continue;
                }

                builder.Append(c);
                continue;
            }

            if (c == '"') inString = true;
            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Converts single-quote delimiters to double quotes, but only when the text
    /// contains no double quotes at all — i.e. the model quoted the WHOLE document
    /// with single quotes. Mixing the two is ambiguous (an apostrophe inside a
    /// double-quoted value must not be touched), so we leave those alone.
    /// </summary>
    private static string ConvertSingleQuotes(string json)
    {
        if (json.Contains('"') || !json.Contains('\''))
        {
            return json;
        }

        return json.Replace('\'', '"');
    }

    /// <summary>
    /// Restores an omitted array closer when the next token can only be a
    /// property of an enclosing object. For example,
    /// <c>{"items":[{"id":1},"entities":{}}</c> becomes
    /// <c>{"items":[{"id":1}],"entities":{}}</c>. The repair is deliberately
    /// narrow: the property-like string must follow a comma, the current and any
    /// intervening containers must be arrays, and an enclosing object must exist.
    /// </summary>
    private static string CloseArraysBeforeObjectProperty(string json)
    {
        var stack = new Stack<char>();
        var builder = new StringBuilder(json.Length + 4);
        var inString = false;
        var escaped = false;
        var propertyCandidate = false;
        var precedingCommaIndex = -1;
        var lastSignificant = '\0';
        var lastSignificantIndex = -1;
        var changed = false;

        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];
            builder.Append(c);

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                    if (propertyCandidate
                        && NextSignificantCharacter(json, i + 1) == ':'
                        && TryCountArraysBeforeObject(stack, out var arraysToClose))
                    {
                        builder.Insert(precedingCommaIndex, new string(']', arraysToClose));
                        for (var closed = 0; closed < arraysToClose; closed++)
                        {
                            stack.Pop();
                        }

                        changed = true;
                    }

                    lastSignificant = '"';
                    lastSignificantIndex = builder.Length - 1;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    propertyCandidate = lastSignificant == ','
                        && stack.TryPeek(out var expectedClose)
                        && expectedClose == ']';
                    precedingCommaIndex = propertyCandidate ? lastSignificantIndex : -1;
                    break;
                case '{':
                    stack.Push('}');
                    lastSignificant = c;
                    lastSignificantIndex = builder.Length - 1;
                    break;
                case '[':
                    stack.Push(']');
                    lastSignificant = c;
                    lastSignificantIndex = builder.Length - 1;
                    break;
                case '}' or ']':
                    if (stack.TryPeek(out var expected) && expected == c)
                    {
                        stack.Pop();
                    }

                    lastSignificant = c;
                    lastSignificantIndex = builder.Length - 1;
                    break;
                default:
                    if (!char.IsWhiteSpace(c))
                    {
                        lastSignificant = c;
                        lastSignificantIndex = builder.Length - 1;
                    }

                    break;
            }
        }

        return changed ? builder.ToString() : json;
    }

    private static char NextSignificantCharacter(string text, int start)
    {
        for (var i = start; i < text.Length; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                return text[i];
            }
        }

        return '\0';
    }

    private static bool TryCountArraysBeforeObject(Stack<char> stack, out int arraysToClose)
    {
        arraysToClose = 0;
        foreach (var expectedClose in stack)
        {
            if (expectedClose == ']')
            {
                arraysToClose++;
                continue;
            }

            return expectedClose == '}' && arraysToClose > 0;
        }

        return false;
    }

    /// <summary>
    /// Removes closing braces/brackets for which there is no matching currently
    /// open structure. Small structured-output models occasionally duplicate one
    /// trailing delimiter while leaving the rest of the object intact. A closer
    /// that matches a lower stack frame is preserved as invalid rather than
    /// guessing that an inner delimiter was omitted. String contents are untouched.
    /// </summary>
    private static string RemoveUnexpectedClosingDelimiters(string json)
    {
        var stack = new Stack<char>();
        var builder = new StringBuilder(json.Length);
        var inString = false;
        var escaped = false;
        var changed = false;

        foreach (var c in json)
        {
            if (inString)
            {
                builder.Append(c);
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    builder.Append(c);
                    break;
                case '{':
                    stack.Push('}');
                    builder.Append(c);
                    break;
                case '[':
                    stack.Push(']');
                    builder.Append(c);
                    break;
                case '}' or ']':
                    if (stack.Count > 0 && stack.Peek() == c)
                    {
                        stack.Pop();
                        builder.Append(c);
                    }
                    else if (!stack.Contains(c))
                    {
                        changed = true;
                    }
                    else
                    {
                        builder.Append(c);
                    }
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        return changed ? builder.ToString() : json;
    }

    /// <summary>
    /// Recovers a truncated object/array by appending the missing closing
    /// brackets (and closing an unterminated trailing string). Only acts when the
    /// text opens with <c>{</c> or <c>[</c> and is actually unbalanced.
    /// </summary>
    private static string BalanceBraces(string json)
    {
        var trimmed = json.TrimEnd();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            return json;
        }

        var stack = new Stack<char>();
        var inString = false;
        var escaped = false;
        var danglingStringIsPropertyName = false;
        var danglingStringStart = -1;
        var lastSignificant = '\0';
        for (var i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"')
                {
                    inString = false;
                    lastSignificant = '"';
                }
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    danglingStringStart = i;
                    danglingStringIsPropertyName = stack.TryPeek(out var expectedClose)
                        && expectedClose == '}'
                        && lastSignificant is '{' or ',';
                    break;
                case '{':
                    stack.Push('}');
                    lastSignificant = c;
                    break;
                case '[':
                    stack.Push(']');
                    lastSignificant = c;
                    break;
                case '}' or ']':
                    if (stack.Count > 0) stack.Pop();
                    lastSignificant = c;
                    break;
                default:
                    if (!char.IsWhiteSpace(c)) lastSignificant = c;
                    break;
            }
        }

        if (!inString && stack.Count == 0)
        {
            return json;
        }

        var builder = new StringBuilder(trimmed);
        // Close a dangling string literal first.
        if (inString)
        {
            // If generation stopped immediately after an escape prefix, a quote
            // would itself be escaped and the string would remain unterminated.
            // Complete the pair as a literal backslash before closing the string.
            if (escaped)
            {
                builder.Append('\\');
            }

            builder.Append('"');
            if (danglingStringIsPropertyName
                && danglingStringStart >= 0
                && trimmed.Length > danglingStringStart + 1)
            {
                builder.Append(":null");
            }
        }

        // Drop a dangling trailing comma before closing (`{"a":1,` -> `{"a":1}`).
        var tail = builder.ToString().TrimEnd();
        if (tail.EndsWith(','))
        {
            builder = new StringBuilder(tail[..^1]);
        }

        while (stack.Count > 0)
        {
            builder.Append(stack.Pop());
        }

        return builder.ToString();
    }
}
