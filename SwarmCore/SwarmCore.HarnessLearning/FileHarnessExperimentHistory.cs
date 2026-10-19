using System.Text.Json;

namespace SwarmCore.HarnessLearning;

/// <summary>
/// Optional local host adapter. Hosts supply a private, non-synced root and redact inputs before use.
/// Each report is immutable; atomic create refuses a conflicting writer. No delete or active-head writer exists.
/// </summary>
public sealed class FileHarnessExperimentHistory(string directory, Action? validateScope = null) : IHarnessExperimentHistory
{
    private readonly string _root = Path.GetFullPath(directory);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public async Task<IReadOnlyList<HarnessExperimentReport>> ReadAsync(HarnessExperimentScope scope, CancellationToken ct)
    {
        validateScope?.Invoke();
        var partition = PartitionDirectory(scope);
        if (!Directory.Exists(partition)) return [];
        EnsurePhysicalDirectory(partition);
        var reports = new List<HarnessExperimentReport>();
        foreach (var path in Directory.EnumerateFiles(partition, "*.json").Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Experiment history cannot follow file links.");
            var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            var report = JsonSerializer.Deserialize<HarnessExperimentReport>(bytes, Json)
                ?? throw new InvalidDataException("Experiment history report is empty.");
            if (report.Scope != scope || Path.GetFileName(path) != report.Id.ToString("N") + ".json")
                throw new InvalidDataException("Experiment history identity does not match its partition.");
            reports.Add(report);
        }
        return reports.OrderBy(r => r.StartedAt).ToArray();
    }

    public async Task AppendAsync(HarnessExperimentReport report, CancellationToken ct)
    {
        validateScope?.Invoke();
        if (report.Id == Guid.Empty) throw new ArgumentException("A report identity is required.");
        var partition = PartitionDirectory(report.Scope);
        EnsurePhysicalDirectory(_root);
        Directory.CreateDirectory(partition);
        EnsurePhysicalDirectory(partition);
        var path = Path.Combine(partition, report.Id.ToString("N") + ".json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, Json);
        var pending = Path.Combine(partition, report.Id.ToString("N") + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await file.WriteAsync(bytes, ct).ConfigureAwait(false);
                await file.FlushAsync(ct).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            validateScope?.Invoke();
            try { File.Move(pending, path, overwrite: false); }
            catch (IOException) when (File.Exists(path))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
                    || !(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false)).AsSpan().SequenceEqual(bytes))
                    throw new InvalidOperationException("A conflicting experiment report already owns this identity.");
                // Exact retry after an unknown save outcome is idempotent.
            }
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }

    private string PartitionDirectory(HarnessExperimentScope scope)
    {
        if (string.IsNullOrWhiteSpace(scope.Partition) || string.IsNullOrWhiteSpace(scope.Workload)
            || !HarnessEvidence.IsHash(scope.EnvironmentHash) || !HarnessEvidence.IsHash(scope.SuiteHash))
            throw new ArgumentException("A complete experiment scope is required.");
        return Path.Combine(_root, HarnessEvidence.Hash(JsonSerializer.Serialize(scope)));
    }

    internal static void EnsurePhysicalDirectory(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Experiment history requires physical directories.");
    }
}
