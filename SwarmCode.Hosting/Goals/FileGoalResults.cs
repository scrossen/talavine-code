using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SwarmCore.Agent.Communication;
using SwarmCore.Agent.Continual;
using SwarmCore.Agent.Runtime;

namespace SwarmCode.Hosting.Goals;

/// <summary>
/// Profile-owned results ledger (<c>goals/results.json</c>). Every mutation runs under the goal inventory's OS lock, so a
/// record is admitted only while the recording worker still holds the goal's lease. It owns results alone: it never
/// touches goal control, leases, receipts or the inbox.
/// </summary>
public sealed class FileGoalResults(SwarmCodeDataPaths paths, FileCodingGoalStore goals, TimeProvider? time = null) : IGoalResultLedger
{
    // Kinds and states are stored by name so the owner's file reads as written.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    private readonly string _path = Path.Combine(paths.RootDirectory, "goals", "results.json");
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public Task<GoalResult> RecordAsync(Guid goalId, string conversationId, string leaseToken, GoalResultDraft draft, CancellationToken ct = default)
    {
        var validated = GoalResults.Validate(draft);
        if (string.IsNullOrWhiteSpace(conversationId) || string.IsNullOrWhiteSpace(leaseToken))
            throw new UnauthorizedAccessException("Only the goal's bound worker can record a result.");
        // Defense in depth: inside a running turn, the explicit identity must be that turn's own goal and lease.
        if (AsyncLocalCurrentTurnContextAccessor.Ambient is { GoalMessageGoalId: { } ambientGoal } ambient
            && (ambientGoal != goalId || ambient.GoalMessageLeaseToken != leaseToken || ambient.ConversationId != conversationId))
            throw new UnauthorizedAccessException("A turn may record results only for its own goal.");
        return Access((document, rows) =>
        {
            var goal = rows.FirstOrDefault(row => row.Id == goalId)
                ?? throw new UnauthorizedAccessException("This goal does not exist in this profile.");
            if (goal.ConversationId != conversationId || goal.Status != ContinualObjectiveStatus.InProgress
                || goal.LeaseToken != leaseToken || goal.LeaseExpiresAtUtc <= _time.GetUtcNow())
                throw new UnauthorizedAccessException("This goal's worker is no longer active; the result was not recorded.");
            var now = _time.GetUtcNow();
            // A reported time is kept when plausible (not in the future, not before the goal existed by more than a year); otherwise now.
            var occurred = validated.OccurredAtUtc is { } at && at <= now.AddMinutes(5) && at >= goal.CreatedAtUtc.AddYears(-1) ? at : now;
            var key = GoalResults.IdentityKey(validated);
            var existing = key is null ? null : document.Results.FirstOrDefault(result => result.GoalId == goalId && IdentityKey(result) == key);
            GoalResult saved;
            if (existing is not null)
            {
                // A later record with the same identity updates the earlier one; its time moves only when a plausible new time is given.
                saved = existing with
                {
                    Attempt = goal.AttemptCount, Sequence = ++document.Sequence, RecordedUtc = now,
                    OccurredAtUtc = validated.OccurredAtUtc is null ? existing.OccurredAtUtc : occurred, Kind = validated.Kind, State = validated.State,
                    Title = validated.Title, Url = validated.Url ?? existing.Url, Audience = validated.Audience ?? existing.Audience,
                    Summary = validated.Summary ?? existing.Summary, ExternalId = validated.ExternalId ?? existing.ExternalId,
                    Evidence = validated.Evidence ?? existing.Evidence, Revision = existing.Revision + 1,
                };
                document.Results[document.Results.IndexOf(existing)] = saved;
            }
            else
            {
                var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(goalId.ToString("N") + "\n" + (key ?? Guid.NewGuid().ToString("N")) + "\n" + now.ToString("O")))).ToLowerInvariant();
                saved = new GoalResult(id, goalId, goal.AttemptCount, ++document.Sequence, now, occurred, validated.Kind, validated.State,
                    validated.Title, validated.Url, validated.Audience, validated.Summary, "agent", validated.ExternalId, validated.Evidence);
                document.Results.Add(saved);
            }
            return saved;
        }, write: true, ct);
    }

    public Task<GoalResultPage> ListAsync(Guid goalId, int offset = 0, int take = 50, CancellationToken ct = default)
        => Access((document, _) =>
        {
            var all = document.Results.Where(result => result.GoalId == goalId)
                .OrderByDescending(result => result.OccurredAtUtc).ThenByDescending(result => result.Sequence).ToArray();
            offset = Math.Clamp(offset, 0, all.Length);
            take = Math.Clamp(take, 1, 500);
            var slice = all.Skip(offset).Take(take).ToArray();
            return new GoalResultPage(slice, all.Length, offset, offset + slice.Length < all.Length);
        }, write: false, ct);

    private static string? IdentityKey(GoalResult result)
        => result.ExternalId is { Length: > 0 } id ? "id:" + id : result.Url is { Length: > 0 } url ? "url:" + url : null;

    private async Task<T> Access<T>(Func<Document, IReadOnlyList<ContinualObjectiveSnapshot>, T> action, bool write, CancellationToken ct)
        => await goals.WithGoalsAsync(async rows =>
        {
            var document = Read();
            var result = action(document, rows);
            if (write)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        await JsonSerializer.SerializeAsync(stream, document, Json, ct).ConfigureAwait(false);
                        stream.Flush(flushToDisk: true);
                    }
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    File.Move(temporary, _path, overwrite: true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            return result;
        }, ct).ConfigureAwait(false);

    private Document Read()
    {
        if (!File.Exists(_path)) return new Document();
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var document = JsonSerializer.Deserialize<Document>(stream, Json) ?? throw new InvalidDataException("The saved goal results are unreadable.");
        if (document.Schema > 1) throw new InvalidDataException("The saved goal results use a newer format than this build understands.");
        return document;
    }

    private sealed class Document
    {
        public int Schema { get; set; } = 1;
        public long Sequence { get; set; }
        public List<GoalResult> Results { get; set; } = [];
    }
}
