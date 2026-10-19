using System.Text.Json;

namespace SwarmCore.HarnessLearning;

/// <summary>Reads only explicit host-owned report locations. Never searches customer profiles or artifact trees.</summary>
public static class HarnessReportReader
{
    public static async Task<HarnessExperimentReport> ReadFileAsync(string path, CancellationToken ct = default)
    {
        var file = new FileInfo(Path.GetFullPath(path));
        EnsurePhysical(file.FullName);
        if (!file.Exists || file.Length is <= 0 or > 4_000_000) throw new InvalidDataException("Select a harness report JSON file smaller than 4 MB.");
        var bytes = await File.ReadAllBytesAsync(file.FullName, ct).ConfigureAwait(false);
        if (bytes.Length > 4_000_000) throw new InvalidDataException("The report is too large.");
        var report = JsonSerializer.Deserialize<HarnessExperimentReport>(bytes)
            ?? throw new InvalidDataException("The report is empty.");
        Validate(report);
        return report;
    }

    public static async Task<IReadOnlyList<HarnessExperimentReport>> ReadHistoryAsync(string root, CancellationToken ct = default)
    {
        EnsurePhysical(Path.GetFullPath(root));
        if (!Directory.Exists(root)) return [];
        var reports = new List<HarnessExperimentReport>();
        // The host gives this reader a private per-profile root. Only immutable
        // history partitions are admitted, never a recursive filesystem search.
        var directories = Directory.EnumerateDirectories(root).Where(d => HarnessEvidence.IsHash(Path.GetFileName(d))).Take(501).ToArray();
        if (directories.Length > 500) throw new InvalidDataException("History has more than 500 partitions.");
        foreach (var directory in directories)
        {
            ct.ThrowIfCancellationRequested();
            EnsurePhysical(directory);
            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            {
                if (reports.Count >= 500) throw new InvalidDataException("History has more than 500 reports. Use a smaller host-owned history partition.");
                var report = await ReadFileAsync(path, ct).ConfigureAwait(false);
                var partition = HarnessEvidence.Hash(JsonSerializer.Serialize(report.Scope));
                if (Path.GetFileName(directory) != partition || Path.GetFileName(path) != report.Id.ToString("N") + ".json")
                    throw new InvalidDataException("Report identity does not match its history partition.");
                reports.Add(report);
            }
        }
        return reports;
    }

    private static void EnsurePhysical(string path)
    {
        for (FileSystemInfo? current = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path); current is not null;
             current = current is DirectoryInfo directory ? directory.Parent : ((FileInfo)current).Directory)
            if (current.LinkTarget is not null || current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Report locations cannot follow links.");
    }

    private static void Validate(HarnessExperimentReport r)
    {
        if (r.Id == Guid.Empty || r.Scope is null || string.IsNullOrWhiteSpace(r.Scope.Partition)
            || string.IsNullOrWhiteSpace(r.Scope.Workload) || r.Scope.Workload.Length > 200 || !HarnessEvidence.IsHash(r.Scope.EnvironmentHash)
            || !HarnessEvidence.IsHash(r.Scope.SuiteHash) || r.Baseline?.Components is null
            || r.Candidates is null || r.Candidates.Count > 10 || r.Trials is null || r.Trials.Count > 10_000
            || r.Comparisons is null || r.Comparisons.Count > 10 || r.Policy is null
            || new[] { r.Baseline }.Concat(r.Candidates).Any(c => c?.Components is null || string.IsNullOrWhiteSpace(c.Id)
                || c.Id.Length > 200 || c.Components.Count > 32 || c.Components.Any(p => p.Value is null || p.Value.Length > 16000))
            || r.Trials.Any(t => t is null || !double.IsFinite(t.Score) || t.Score is < 0 or > 1
                || t.Tokens is < 0 || t.DurationMilliseconds < 0 || !double.IsFinite(t.DurationMilliseconds))
            || r.Comparisons.Any(c => c is null || !double.IsFinite(c.DevelopmentGain) || !double.IsFinite(c.HeldOutGain)
                || !double.IsFinite(c.DevelopmentNoiseFloor) || !double.IsFinite(c.HeldOutNoiseFloor)))
            throw new InvalidDataException("This file is not a valid harness experiment report.");
        var revisions = new[] { r.Baseline }.Concat(r.Candidates).ToArray();
        if (revisions.Select(v => v.Id).Distinct(StringComparer.Ordinal).Count() != revisions.Length
            || r.Trials.Any(t => !revisions.Any(v => v.Fingerprint == t.RevisionHash))
            || r.Comparisons.Any(c => !r.Candidates.Any(v => v.Id == c.RevisionId && v.Fingerprint == c.RevisionHash))
            || r.RecommendedRevisionId is { } recommended && (r.StopReason is not null || !r.Comparisons.Any(c => c.Accepted && c.RevisionId == recommended)))
            throw new InvalidDataException("Experiment references are inconsistent.");
    }
}
