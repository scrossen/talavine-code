namespace SwarmCore.Agent.Runtime;

/// <summary>
/// Admission for verbatim statements extracted from an authentic user message.
/// Selection still requires a semantic fact/privacy policy; this prevents an
/// extractor from inventing text or dropping a sentence's negation/qualification.
/// </summary>
public static class UserStatementEvidence
{
    public static bool ContainsCompleteStatement(string userText, string statement)
    {
        if (string.IsNullOrWhiteSpace(statement))
            return false;

        for (var searchAt = 0; searchAt <= userText.Length - statement.Length;)
        {
            var start = userText.IndexOf(statement, searchAt, StringComparison.Ordinal);
            if (start < 0)
                return false;
            var before = start - 1;
            while (before >= 0 && char.IsWhiteSpace(userText[before]) && userText[before] != '\n')
                before--;
            var end = start + statement.Length;
            var after = end;
            while (after < userText.Length && char.IsWhiteSpace(userText[after]) && userText[after] != '\n')
                after++;
            var startsStatement = before < 0 || userText[before] is '.' or '!' or '?' or '\n';
            var endsStatement = after == userText.Length || userText[after] == '\n'
                || (statement[^1] is '.' or '!' or '?' && after > end);
            if (startsStatement && endsStatement)
                return true;
            searchAt = start + 1;
        }
        return false;
    }
}
