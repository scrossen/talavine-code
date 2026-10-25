using System.Text.Json;
using SwarmCode.Hosting.Goals;
using SwarmCore.Coding.Goals;
using SwarmCore.Coding.SelfImprovement;

namespace SwarmCode.Hosting.Improvement;

/// <summary>Failed automatic installation feeds the existing repair worker; it never resumes campaign work or rewrites receipts.</summary>
public sealed class GoalRepairActivationFeedback(SwarmCodeDataPaths paths, ISelfImprovementBacklog backlog,
    ICodingGoalStore goals, Func<SelfImprovementOptions> options, Func<bool>? paused = null,
    IFailedRepairActivationVerifier? verifier = null)
{
    private readonly IFailedRepairActivationVerifier _verifier = verifier ?? new PreviousAppActivationVerifier();
    private bool Allowed => options().Enabled && options().AutoApplyGoalRepairs && paused?.Invoke() != true;

    public Task<int> RequeueAsync(Guid? goalId = null, CancellationToken ct = default)
        => Task.Run(() => RequeueCoreAsync(goalId, ct), ct);

    private async Task<int> RequeueCoreAsync(Guid? goalId, CancellationToken ct)
    {
        if (!Allowed || goals is not FileCodingGoalStore owningGoals) return 0;
        try
        {
            var path = Path.Combine(paths.SelfImprovementDirectory, SwarmCodeRebuild.StatusName);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 16 * 1024 * 1024) return 0;
            var status = JsonSerializer.Deserialize<RelaunchStatus>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            // System.Text.Json parses an explicit offset into local DateTime. Compare instants,
            // not local clock fields, against the host's UTC baselines.
            if (status is not null) status = status with { StartedUtc = status.StartedUtc.ToUniversalTime(),
                FinishedUtc = status.FinishedUtc?.ToUniversalTime(), FallbackVerifiedAtUtc = status.FallbackVerifiedAtUtc?.ToUniversalTime() };
            var now = DateTime.UtcNow;
            if (status is not { AutomaticGoalRepair: true, BuildExitCode: not null, FinishedUtc: not null,
                    FallbackVerifiedAtUtc: not null, PreviousArtifactHashes: { Count: > 0 }, ArtifactRoot: not null,
                    SourceRoot: not null, ProfileRoot: not null, ExitedProcessId: > 0 }
                || status.BuildExitCode == 0 || status.ExitedProcessId == Environment.ProcessId
                || !Guid.TryParseExact(status.AttemptId, "N", out _) || status.FinishedUtc < status.StartedUtc
                || status.FallbackVerifiedAtUtc < status.FinishedUtc || status.FallbackVerifiedAtUtc > now
                || !SamePath(status.ProfileRoot, paths.RootDirectory)
                || SwarmCodeSourceRepository.Resolve(options().RepositoryRoot) is not { } source || !SamePath(status.SourceRoot, source)
                || status.FailurePhase is not ("source_preflight" or "build" or "candidate_verification" or "installation" or "artifact_receipt" or "launch")
                || status.RepairedItems is null || status.RepairedItems.Count == 0 || status.RepairedItems.Any(b => b is null) || status.RepairedItems.GroupBy(b => b.Id, StringComparer.Ordinal).Any(g => g.Count() != 1)) return 0;
            if (!await _verifier.VerifyAsync(status, ct).ConfigureAwait(false) || !Allowed) return 0;
            var count = 0;
            foreach (var binding in status.RepairedItems)
            {
                if (binding.GoalRepair is not { } baseline || goalId is { } selected && selected != baseline.GoalId
                    || binding.LastAttemptUtc is not { } attempted || attempted > status.StartedUtc) continue;
                var item = await backlog.GetAsync(binding.Id, ct).ConfigureAwait(false);
                if (item is not { Stage: SelfImprovementStage.Merged } || item.ActivationFailure?.AttemptId == status.AttemptId
                    || RelaunchInstalledFixVerifier.Binding(item) != binding) continue;
                try
                {
                    // Goal controls and deletion share this lock. Re-check them in the same critical section
                    // as the field-owned backlog CAS, so a newer Stop/Resume/edit cannot be overwritten.
                    var changed = await owningGoals.WithGoalsAsync(async rows =>
                    {
                        var current = rows.SingleOrDefault(g => g.Id == baseline.GoalId);
                        if (!Allowed || current is null || !baseline.CanResume(current, DateTimeOffset.UtcNow)) return false;
                        var failure = new SelfImprovementActivationFailure(status.AttemptId!, binding.Commit, status.BuildExitCode.Value,
                            status.FailurePhase, status.FinishedUtc.Value,
                            $"Automatic installation of verified commit {binding.Commit} failed during {status.FailurePhase} (exit {status.BuildExitCode}). " +
                            "The host verified the original app was restored/running. Reproduce this build/installation failure and verify a new fix; the original goal and uncertain tool receipts remain unchanged.");
                        var updated = await backlog.UpdateAsync(item.Id, fresh => fresh.Stage == SelfImprovementStage.Merged
                            && RelaunchInstalledFixVerifier.Binding(fresh) == binding && fresh.ActivationFailure?.AttemptId != status.AttemptId
                            ? fresh with { Stage = SelfImprovementStage.Queued, Owner = null, ActivationFailure = failure,
                                Note = "Automatic installation failed; the previous verified app is running and another repair is queued." }
                            : null, item.Revision, ct).ConfigureAwait(false);
                        return updated?.Stage == SelfImprovementStage.Queued && updated.ActivationFailure?.AttemptId == status.AttemptId;
                    }, ct).ConfigureAwait(false);
                    if (changed) ++count;
                }
                catch (SelfImprovementConflictException) { } // A newer human/worker mutation wins; do not consume its failure token.
            }
            return count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or BadImageFormatException)
        { return 0; }
    }

    private static bool SamePath(string first, string second) => Path.GetFullPath(first).Equals(Path.GetFullPath(second),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

/// <summary>Host trust boundary: model output and an app launch command cannot establish that the previous artifact is running.</summary>
public interface IFailedRepairActivationVerifier
{
    Task<bool> VerifyAsync(RelaunchStatus failedStatus, CancellationToken ct = default);
}

internal sealed class PreviousAppActivationVerifier : IFailedRepairActivationVerifier
{
    public Task<bool> VerifyAsync(RelaunchStatus failedStatus, CancellationToken ct = default)
        => RelaunchInstalledFixVerifier.MatchesCurrentProcessAsync(failedStatus.ArtifactRoot!, failedStatus.PreviousArtifactHashes!,
            failedStatus.StartedUtc, ct);
}
