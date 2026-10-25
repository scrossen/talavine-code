using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SwarmCore.Agent.Communication;
using SwarmCore.Agent.Continual;
using SwarmCore.Agent.Runtime;

namespace SwarmCode.Hosting.Goals;

/// <summary>
/// Profile-owned daily reports (<c>goals/reports.json</c>). Every mutation runs under the goal inventory's OS lock, so a
/// report is admitted only while the recording worker still holds the goal's lease. One report per goal and day; a later
/// record for the same day replaces the earlier one whole (every field), so a worker resends what still stands. It owns reports alone: never goal control, leases, receipts or the inbox.
/// </summary>
public sealed class FileGoalReports(SwarmCodeDataPaths paths, FileCodingGoalStore goals, TimeProvider? time = null) : IGoalReportLedger
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    private readonly string _path = Path.Combine(paths.RootDirectory, "goals", "reports.json");
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public Task<GoalReport> RecordAsync(Guid goalId, string conversationId, string leaseToken, GoalReportDraft draft, CancellationToken ct = default)
    {
        var validated = GoalReports.Validate(draft);
        if (string.IsNullOrWhiteSpace(conversationId) || string.IsNullOrWhiteSpace(leaseToken))
            throw new UnauthorizedAccessException("Only the goal's bound worker can record a report.");
        if (AsyncLocalCurrentTurnContextAccessor.Ambient is { GoalMessageGoalId: { } ambientGoal } ambient
            && (ambientGoal != goalId || ambient.GoalMessageLeaseToken != leaseToken || ambient.ConversationId != conversationId))
            throw new UnauthorizedAccessException("A turn may record reports only for its own goal.");
        return Access((document, rows) =>
        {
            var goal = rows.FirstOrDefault(row => row.Id == goalId)
                ?? throw new UnauthorizedAccessException("This goal does not exist in this profile.");
            var now = _time.GetUtcNow();
            if (goal.ConversationId != conversationId || goal.Status != ContinualObjectiveStatus.InProgress
                || goal.LeaseToken != leaseToken || goal.LeaseExpiresAtUtc <= now)
                throw new UnauthorizedAccessException("This goal's worker is no longer active; the report was not recorded.");
            // A report is for a day that exists: not later than tomorrow UTC, not before the goal existed.
            if (validated.Day > DateOnly.FromDateTime(now.UtcDateTime).AddDays(1) || validated.Day < DateOnly.FromDateTime(goal.CreatedAtUtc.UtcDateTime).AddDays(-1))
                throw new ArgumentException("day must be a day of this goal's life, not in the future.");
            var existing = document.Reports.FirstOrDefault(report => report.GoalId == goalId && report.Day == validated.Day);
            GoalReport saved;
            if (existing is not null)
            {
                saved = existing with
                {
                    Attempt = goal.AttemptCount, Sequence = ++document.Sequence, RecordedUtc = now, Headline = validated.Headline,
                    Achieved = validated.Achieved, Metrics = validated.Metrics!, Held = validated.Held!, NextActions = validated.NextActions!,
                    ResultIds = validated.ResultIds!, Evidence = validated.Evidence, Revision = existing.Revision + 1,
                };
                document.Reports[document.Reports.IndexOf(existing)] = saved;
            }
            else
            {
                var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(goalId.ToString("N") + "\n" + validated.Day.ToString("O")))).ToLowerInvariant();
                saved = new GoalReport(id, goalId, goal.AttemptCount, ++document.Sequence, now, validated.Day, validated.Headline, validated.Achieved,
                    validated.Metrics!, validated.Held!, validated.NextActions!, validated.ResultIds!, "agent", validated.Evidence);
                document.Reports.Add(saved);
            }
            return saved;
        }, write: true, ct);
    }

    public Task<GoalReportPage> ListAsync(Guid goalId, int offset = 0, int take = 30, CancellationToken ct = default)
        => Access((document, _) =>
        {
            var all = document.Reports.Where(report => report.GoalId == goalId)
                .OrderByDescending(report => report.Day).ThenByDescending(report => report.Sequence).ToArray();
            offset = Math.Clamp(offset, 0, all.Length);
            take = Math.Clamp(take, 1, 366);
            var slice = all.Skip(offset).Take(take).ToArray();
            return new GoalReportPage(slice, all.Length, offset, offset + slice.Length < all.Length);
        }, write: false, ct);

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
        var document = JsonSerializer.Deserialize<Document>(stream, Json) ?? throw new InvalidDataException("The saved goal reports are unreadable.");
        if (document.Schema > 1) throw new InvalidDataException("The saved goal reports use a newer format than this build understands.");
        return document;
    }

    private sealed class Document
    {
        public int Schema { get; set; } = 1;
        public long Sequence { get; set; }
        public List<GoalReport> Reports { get; set; } = [];
    }
}
