using System.Text;

namespace SwarmCore.Secrets;

/// <summary>One plaintext row held only long enough for the caller to protect it.</summary>
public sealed record ImportedCredential(
    string Label,
    string? Username,
    string? Url,
    string Password,
    string? Notes)
{
    /// <summary>Vault name proposed for this row: the site host plus username, or the label.</summary>
    public string SuggestedName => SecretNames.ForSite(Url, Username, Label);

    public override string ToString() => $"ImportedCredential({Label}, redacted)";
}

public sealed record PasswordCsvParseResult(
    IReadOnlyList<ImportedCredential> Entries,
    int SkippedRows,
    string DetectedFormat,
    string? Error)
{
    public bool Success => Error is null;

    internal static PasswordCsvParseResult Failed(string error) => new([], 0, "unknown", error);
}

/// <summary>
/// Parses a CSV exported by a browser or password manager: Chrome / Edge / Google
/// Password Manager (<c>name,url,username,password,note</c>), Firefox
/// (<c>url,username,password,...</c>), Bitwarden (<c>name,login_uri,login_username,login_password,notes</c>),
/// 1Password (<c>Title,Url,Username,Password,Notes</c>), LastPass (<c>url,username,password,extra,name</c>),
/// KeePass and Dashlane exports with the equivalent columns. Header order and casing are
/// ignored. Deliberately a pure parser: callers must protect every
/// <see cref="ImportedCredential.Password"/> before persistence and must never send the raw
/// CSV or parsed passwords through an LLM or a log.
/// </summary>
public static class PasswordCsvParser
{
    private static readonly string[] LabelHeaders = ["name", "title", "account", "item", "label"];
    private static readonly string[] UrlHeaders = ["url", "login_uri", "website", "web site", "site", "uri", "hostname", "domain", "origin_url", "login url"];
    private static readonly string[] UsernameHeaders = ["username", "login_username", "user name", "login name", "login", "user", "email", "user_name"];
    private static readonly string[] PasswordHeaders = ["password", "login_password", "pass", "passwd", "pwd"];
    private static readonly string[] NotesHeaders = ["note", "notes", "extra", "comments", "comment", "login_notes"];

    public static PasswordCsvParseResult Parse(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
            return PasswordCsvParseResult.Failed("The selected CSV is empty.");

        var parsed = ParseRows(csv);
        if (parsed.Error is not null)
            return PasswordCsvParseResult.Failed(parsed.Error);
        if (parsed.Rows.Count == 0)
            return PasswordCsvParseResult.Failed("The selected CSV has no header row.");

        var headers = parsed.Rows[0]
            .Select((value, index) => (Name: NormalizeHeader(value), Index: index))
            .Where(h => h.Name.Length > 0)
            .GroupBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Index, StringComparer.OrdinalIgnoreCase);

        var password = FirstIndex(headers, PasswordHeaders);
        var label = FirstIndex(headers, LabelHeaders);
        var url = FirstIndex(headers, UrlHeaders);
        var username = FirstIndex(headers, UsernameHeaders);
        var notes = FirstIndex(headers, NotesHeaders);
        var type = headers.TryGetValue("type", out var t) ? t : -1;

        if (password < 0 || (label < 0 && url < 0))
        {
            return PasswordCsvParseResult.Failed(
                "This does not look like a password export. Expected columns such as name or url, username, and password.");
        }

        var entries = new List<ImportedCredential>();
        var skipped = 0;
        foreach (var row in parsed.Rows.Skip(1))
        {
            // Bitwarden mixes secure notes and cards into the same file; only logins carry a usable password.
            if (type >= 0)
            {
                var kind = ValueAt(row, type).Trim();
                if (kind.Length > 0 && !kind.Equals("login", StringComparison.OrdinalIgnoreCase)) { skipped++; continue; }
            }
            var pw = ValueAt(row, password);
            if (pw.Length == 0) { skipped++; continue; }

            var name = ValueAt(row, label).Trim();
            var site = NullIfWhiteSpace(ValueAt(row, url));
            var user = NullIfWhiteSpace(ValueAt(row, username));
            var note = NullIfWhiteSpace(ValueAt(row, notes));
            var display = name.Length > 0 ? name : SecretNames.Host(site) ?? site ?? user ?? "Imported password";
            entries.Add(new ImportedCredential(display, user, site, pw, note));
        }

        return new PasswordCsvParseResult(entries, skipped, DetectFormat(headers), Error: null);
    }

    /// <summary>
    /// Stable metadata-only key used to avoid duplicating entries on repeat imports.
    /// Secret values are intentionally absent from the key.
    /// </summary>
    public static string MetadataKey(string? label, string? username, string? url)
        => string.Join('\u001f', NormalizeKeyPart(label), NormalizeKeyPart(username), NormalizeUrl(url));

    private static string DetectFormat(IReadOnlyDictionary<string, int> headers)
    {
        if (headers.ContainsKey("login_uri") || headers.ContainsKey("login_password")) return "Bitwarden";
        if (headers.ContainsKey("extra") && headers.ContainsKey("grouping")) return "LastPass";
        if (headers.ContainsKey("httprealm") || headers.ContainsKey("formactionorigin") || headers.ContainsKey("timecreated")) return "Firefox";
        if (headers.ContainsKey("title") && headers.ContainsKey("url")) return "1Password";
        if (headers.ContainsKey("name") && headers.ContainsKey("url") && headers.ContainsKey("username")) return "Chrome/Edge";
        return "generic";
    }

    private static int FirstIndex(IReadOnlyDictionary<string, int> headers, string[] candidates)
    {
        foreach (var c in candidates)
            if (headers.TryGetValue(c, out var i)) return i;
        return -1;
    }

    private static string NormalizeHeader(string value)
        => value.Trim().TrimStart('﻿').ToLowerInvariant();

    private static string NormalizeKeyPart(string? value)
        => string.IsNullOrWhiteSpace(value) ? "" : value.Trim().ToUpperInvariant();

    private static string NormalizeUrl(string? value)
        => string.IsNullOrWhiteSpace(value) ? "" : value.Trim().TrimEnd('/').ToUpperInvariant();

    private static string ValueAt(IReadOnlyList<string> row, int index)
        => index >= 0 && index < row.Count ? row[index] : "";

    private static string? NullIfWhiteSpace(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ParsedRows ParseRows(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
                continue;
            }

            if (c == '"')
            {
                // A quote starts a quoted field only before any unquoted text; inside text it is literal.
                if (field.Length == 0) inQuotes = true;
                else field.Append(c);
            }
            else if (c == ',')
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString());
                field.Clear();
                if (row.Any(value => value.Length > 0)) rows.Add(row);
                row = [];
            }
            else field.Append(c);
        }

        if (inQuotes)
            return new ParsedRows([], "The CSV contains an unfinished quoted value.");

        row.Add(field.ToString());
        if (row.Any(value => value.Length > 0)) rows.Add(row);
        return new ParsedRows(rows, Error: null);
    }

    private sealed record ParsedRows(IReadOnlyList<List<string>> Rows, string? Error);
}
