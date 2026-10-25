using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SwarmCore.Agent.Continual;
using SwarmCore.Coding.Goals;
using SwarmCore.Coding.SelfImprovement;

namespace SwarmCode.Hosting.Improvement;

/// <summary>Checks the helper's exact post-build artifact against the current process, including already-loaded module identities.</summary>
public sealed class RelaunchInstalledFixVerifier(SwarmCodeDataPaths paths) : ISelfImprovementInstalledFixVerifier
{
    public static InstalledRepairBinding Binding(SelfImprovementItem item)
        => new(item.Id, item.Commit ?? "", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item.Evidence ?? ""))).ToLowerInvariant())
        { ItemRevision = item.Revision, LastAttemptUtc = item.LastAttemptUtc, GoalRepair = item.GoalRepair };

    public async Task<string?> VerifyAsync(SelfImprovementItem item, CancellationToken ct = default)
    {
        if (item.Stage != SelfImprovementStage.Merged || string.IsNullOrWhiteSpace(item.Commit)
            || string.IsNullOrWhiteSpace(item.Evidence)) return null;
        try
        {
            var status = JsonSerializer.Deserialize<RelaunchStatus>(await File.ReadAllTextAsync(
                Path.Combine(paths.SelfImprovementDirectory, SwarmCodeRebuild.StatusName), ct).ConfigureAwait(false), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (status is { AutomaticGoalRepair: true, InProgress: true, ArtifactHashes: { Count: > 0 } }
                && status.RepairedItems?.Contains(Binding(item)) == true
                && Guid.TryParseExact(status.AttemptId, "N", out _))
                status = await WaitForStartupCompletionAsync(status, ct).ConfigureAwait(false);
            if (status is not null) status = status with { StartedUtc = status.StartedUtc.ToUniversalTime(),
                FinishedUtc = status.FinishedUtc?.ToUniversalTime(), StartupAcknowledgedAtUtc = status.StartupAcknowledgedAtUtc?.ToUniversalTime() };
            if (status is not { BuildExitCode: 0, FinishedUtc: not null, ExitedProcessId: not null,
                    ArtifactRoot: not null, ArtifactHashes: { Count: > 0 } }
                || status.ExitedProcessId == Environment.ProcessId || status.FinishedUtc < status.StartedUtc
                || status.RepairedItems is null || !status.RepairedItems.Contains(Binding(item))
                || item.LastAttemptUtc is not { } attempted || status.StartedUtc < attempted
                || StartupProofBaseline(status, Environment.ProcessId) is not { } earliest) return null;
            if (!await MatchesCurrentProcessAsync(status.ArtifactRoot, status.ArtifactHashes,
                earliest, ct).ConfigureAwait(false)) return null;
            return $"Installed repair {item.Id}, commit {item.Commit}; exact rebuilt artifact and loaded execution modules verified.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or BadImageFormatException or ArgumentException)
        { return null; }
    }

    internal static DateTime? StartupProofBaseline(RelaunchStatus status, int currentProcessId)
    {
        if (status.BuildExitCode != 0 || status.FinishedUtc is not { } finished) return null;
        finished = finished.ToUniversalTime();
        if (!status.AutomaticGoalRepair) return finished;
        var started = status.StartedUtc.ToUniversalTime();
        if (status.StartedProcessId is not > 0 || status.ExitedProcessId is not > 0
            || status.StartedProcessId == status.ExitedProcessId
            || status.StartupAcknowledgedAtUtc is not { } acknowledgment
            || acknowledgment.ToUniversalTime() < started || acknowledgment.ToUniversalTime() > finished) return null;
        // The helper proved its exact child. That receipt remains valid after a later restart,
        // but that later process must have started after installation completed and must still
        // pass the full current executable/artifact/loaded-module proof.
        return status.StartedProcessId == currentProcessId ? started : finished;
    }

    // Restored runners can reach their natural continuation checkpoint before the detached
    // helper accepts the host-ready receipt. Await only this exact activation, then let the
    // coordinator re-read the goal controls before its CAS; never create another runner/worker.
    internal async Task<RelaunchStatus?> WaitForStartupCompletionAsync(RelaunchStatus pending, CancellationToken ct,
        TimeSpan? timeout = null)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (deadline.Elapsed < (timeout ?? TimeSpan.FromSeconds(50)))
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(100, ct).ConfigureAwait(false);
            var current = JsonSerializer.Deserialize<RelaunchStatus>(await File.ReadAllTextAsync(
                Path.Combine(paths.SelfImprovementDirectory, SwarmCodeRebuild.StatusName), ct).ConfigureAwait(false),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (current is null || current.AttemptId != pending.AttemptId
                || current.StartedUtc.ToUniversalTime() != pending.StartedUtc.ToUniversalTime()
                || current.ProfileRoot != pending.ProfileRoot || current.SourceRoot != pending.SourceRoot
                || current.ArtifactRoot != pending.ArtifactRoot || current.ExitedProcessId != pending.ExitedProcessId
                || !current.AutomaticGoalRepair || current.RepairedItems is null || pending.RepairedItems is null
                || !current.RepairedItems.SequenceEqual(pending.RepairedItems)
                || current.ArtifactHashes is null || pending.ArtifactHashes is null
                || current.ArtifactHashes.Count != pending.ArtifactHashes.Count
                || current.ArtifactHashes.Any(pair => !pending.ArtifactHashes.TryGetValue(pair.Key, out var expected)
                    || !string.Equals(pair.Value, expected, StringComparison.OrdinalIgnoreCase))) return null;
            if (!current.InProgress) return current;
        }
        return null;
    }

    internal static async Task<bool> MatchesCurrentProcessAsync(string artifactRoot, IReadOnlyDictionary<string, string> hashes,
        DateTime earliestProcessStartUtc, CancellationToken ct)
    {
            var root = Path.GetFullPath(artifactRoot);
            var process = Environment.ProcessPath;
            if (process is null) return false;
            var processDirectory = Path.GetDirectoryName(process)!;
            var expectedRoot = Path.GetFileName(processDirectory) == "MacOS"
                ? Path.GetDirectoryName(processDirectory)! : processDirectory;
            if (!Path.GetFullPath(expectedRoot).Equals(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return false;
            using var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
            if (currentProcess.StartTime.ToUniversalTime() < earliestProcessStartUtc) return false;
            // The live host and shared execution code must be from that artifact. Matching newly replaced disk files
            // is insufficient when an old process still has the pre-fix assemblies loaded.
            var critical = new[] { typeof(GoalSelfImprovementCoordinator).Assembly, typeof(CodingGoalOrchestrator).Assembly,
                typeof(ContinualObjectiveProcessor).Assembly };
            foreach (var assembly in critical)
                if (!await MatchesLoadedAsync(assembly, root, hashes, ct).ConfigureAwait(false)) return false;
            if (!await MatchesFileAsync(process, root, hashes, ct).ConfigureAwait(false)) return false;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic
                && (a.GetName().Name?.StartsWith("SwarmCode", StringComparison.Ordinal) == true
                    || a.GetName().Name?.StartsWith("SwarmCore", StringComparison.Ordinal) == true)))
                if (!await MatchesLoadedAsync(assembly, root, hashes, ct).ConfigureAwait(false)) return false;
            var actual = InstalledArtifactSnapshot.Capture(root);
            return actual.Count == hashes.Count && actual.All(pair => hashes.TryGetValue(pair.Key, out var expected)
                && string.Equals(pair.Value, expected, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<bool> MatchesFileAsync(string path, string root, IReadOnlyDictionary<string, string> hashes, CancellationToken ct)
    {
        var relative = Path.GetRelativePath(root, Path.GetFullPath(path)).Replace('\\', '/');
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal)
            || !hashes.TryGetValue(relative, out var expected) || expected is not { Length: 64 }) return false;
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> MatchesLoadedAsync(Assembly assembly, string root, IReadOnlyDictionary<string, string> hashes, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(assembly.Location) || !await MatchesFileAsync(assembly.Location, root, hashes, ct).ConfigureAwait(false)) return false;
        using var pe = new PEReader(File.OpenRead(assembly.Location));
        var metadata = pe.GetMetadataReader();
        return metadata.GetGuid(metadata.GetModuleDefinition().Mvid) == assembly.ManifestModule.ModuleVersionId;
    }
}
