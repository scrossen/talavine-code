using System.Text.Json;

namespace SwarmCode.Hosting.Improvement;

/// <summary>Host-ready receipt for an automatic installed repair. This is written only after the
/// actual current executable, loaded execution modules and complete artifact match the helper's manifest.</summary>
public static class GoalRepairStartupAcknowledgment
{
    public static Task<bool> TryAcknowledgeAsync(SwarmCodeDataPaths paths, CancellationToken ct = default)
        => Task.Run(async () =>
        {
            try
            {
                var path = Path.Combine(paths.SelfImprovementDirectory, SwarmCodeRebuild.StatusName);
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > 16 * 1024 * 1024) return false;
                var status = JsonSerializer.Deserialize<RelaunchStatus>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (status is not { AutomaticGoalRepair: true, BuildExitCode: null, FinishedUtc: null,
                        ArtifactRoot: not null, ArtifactHashes: { Count: > 0 }, ProfileRoot: not null,
                        ExitedProcessId: > 0 } || status.ExitedProcessId == Environment.ProcessId
                    || !Guid.TryParseExact(status.AttemptId, "N", out _)
                    || !Path.GetFullPath(status.ProfileRoot).Equals(Path.GetFullPath(paths.RootDirectory),
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                    || !await RelaunchInstalledFixVerifier.MatchesCurrentProcessAsync(status.ArtifactRoot,
                        status.ArtifactHashes, status.StartedUtc, ct).ConfigureAwait(false)) return false;
                var acknowledgment = new RepairStartupAcknowledgment(status.AttemptId!, Environment.ProcessId,
                    DateTime.UtcNow, status.ArtifactHashes);
                var target = Path.Combine(paths.SelfImprovementDirectory, "startup-" + status.AttemptId + ".json");
                var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(acknowledgment,
                        new JsonSerializerOptions(JsonSerializerDefaults.Web)), ct).ConfigureAwait(false);
                    File.Move(temporary, target, overwrite: true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or BadImageFormatException)
            { return false; }
        }, ct);
}

internal sealed record RepairStartupAcknowledgment(string AttemptId, int ProcessId, DateTime AcknowledgedAtUtc,
    IReadOnlyDictionary<string, string> ArtifactHashes);
