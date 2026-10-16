using SwarmCore.Secrets;

namespace SwarmCode.Hosting.Secrets;

/// <summary>Counts from one CSV import. Names are listed; values never are.</summary>
public sealed record PasswordImportSummary(
    int Added,
    int Updated,
    int Unchanged,
    int SkippedRows,
    string DetectedFormat,
    IReadOnlyList<string> Names,
    string? Error)
{
    public bool Success => Error is null;
    public int Imported => Added + Updated;

    public string Describe() => Error is not null
        ? Error
        : $"Imported {Imported} login{(Imported == 1 ? "" : "s")} from a {DetectedFormat} export ({Added} new, {Updated} updated, {Unchanged} unchanged, {SkippedRows} row{(SkippedRows == 1 ? "" : "s")} skipped).";
}

/// <summary>
/// Imports a browser or password-manager CSV export into the vault as site logins named
/// <c>host/username</c>. Each password is protected before it is written; a repeat import
/// of the same export leaves unchanged rows alone. The CSV text itself is read once and
/// never logged, persisted, or shown to a model.
/// </summary>
public sealed class PasswordCsvImporter(ISecretVault vault)
{
    public async Task<PasswordImportSummary> ImportFileAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return Failed($"File not found: {path}");
        string csv;
        try { csv = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Failed($"Could not read the CSV: {ex.Message}"); }
        return await ImportTextAsync(csv, ct).ConfigureAwait(false);
    }

    public async Task<PasswordImportSummary> ImportTextAsync(string csv, CancellationToken ct = default)
    {
        var parsed = PasswordCsvParser.Parse(csv);
        if (!parsed.Success) return Failed(parsed.Error!);

        int added = 0, updated = 0, unchanged = 0;
        var names = new List<string>();
        foreach (var entry in parsed.Entries)
        {
            ct.ThrowIfCancellationRequested();
            string name;
            try { name = entry.SuggestedName; }
            catch (ArgumentException) { continue; }

            var existing = await vault.GetLoginAsync(name, ct).ConfigureAwait(false);
            if (existing is not null
                && string.Equals(existing.Password, entry.Password, StringComparison.Ordinal)
                && string.Equals(existing.Username ?? "", entry.Username ?? "", StringComparison.Ordinal))
            {
                unchanged++;
                continue;
            }

            await vault.SetAsync(new SecretWriteRequest
            {
                Name = name,
                Value = entry.Password,
                Kind = SecretKinds.Password,
                Site = entry.Url,
                Username = entry.Username,
                Notes = BuildNotes(entry),
            }, ct).ConfigureAwait(false);
            names.Add(name);
            if (existing is null) added++; else updated++;
        }

        return new PasswordImportSummary(added, updated, unchanged, parsed.SkippedRows, parsed.DetectedFormat, names, null);
    }

    private static string? BuildNotes(ImportedCredential entry)
    {
        var label = string.IsNullOrWhiteSpace(entry.Label) ? null : "Imported login: " + entry.Label.Trim();
        var notes = string.IsNullOrWhiteSpace(entry.Notes) ? null : entry.Notes.Trim();
        return label is null ? notes : notes is null ? label : label + "\n" + notes;
    }

    private static PasswordImportSummary Failed(string error) => new(0, 0, 0, 0, "unknown", [], error);
}
