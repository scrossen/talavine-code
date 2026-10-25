using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SwarmCore.Agent.Continual;

namespace SwarmCode.Hosting.Goals;

/// <summary>One goal's outcome of a compaction pass. <see cref="Held"/> counts the receipts kept whole, by reason.</summary>
public sealed record GoalReceiptCompactionResult(Guid GoalId, ContinualReceiptRetentionHold GoalHold, int ReceiptsCompacted, long BytesCompacted,
    long LogBytesBefore, long LogBytesAfter, bool LogRewritten, IReadOnlyDictionary<ContinualReceiptRetentionHold, int> Held)
{
    public bool Changed => ReceiptsCompacted > 0 || LogRewritten;
}

/// <summary>A journal line the inventory keeps for every pass that changed a goal's storage.</summary>
public sealed record GoalCompactionRun(DateTimeOffset AtUtc, Guid GoalId, int Receipts, long BytesCompacted, long LogBytesBefore, long LogBytesAfter, string Policy);

/// <summary>A file the goals folder holds, with what it is to the store.</summary>
public sealed record GoalStoreFile(string Path, long Bytes, string Kind);

/// <summary>What the goals folder holds: the inventory, the body logs, and files the store does not use (left by tools or copies).</summary>
public sealed record GoalStoreInventoryReport(string Directory, long InventoryBytes, int BodyLogs, long BodyLogBytes, IReadOnlyList<GoalStoreFile> Stray)
{
    public long StrayBytes => Stray.Sum(file => file.Bytes);
}

public sealed partial class FileCodingGoalStore
{
    /// <summary>Journal lines kept in the inventory; older ones fall off.</summary>
    public const int CompactionJournalLimit = 200;
    private const string StrayKind = "unused";
    /// <summary>The other files the goal stores keep beside the inventory under its lock (mailbox, results ledger); compaction never reads or writes them.</summary>
    private static readonly HashSet<string> SiblingStoreFiles = new(StringComparer.Ordinal) { "objectives.json.lock", "messages.json", "results.json", "reports.json" };

    public Task<IReadOnlyList<Guid>> ListGoalIdsAsync(CancellationToken ct = default)
        => Access((document, _) => Task.FromResult<IReadOnlyList<Guid>>(document.Rows.Select(row => row.Id).ToArray()), false, ct);

    public Task<IReadOnlyList<GoalCompactionRun>> GetCompactionJournalAsync(CancellationToken ct = default)
        => Access((document, _) => Task.FromResult<IReadOnlyList<GoalCompactionRun>>(document.CompactionRuns.ToArray()), false, ct);

    /// <summary>Compacts every goal in turn, each under its own lock acquisition so other writers wait for one goal at most.</summary>
    public async Task<IReadOnlyList<GoalReceiptCompactionResult>> CompactAllAsync(GoalReceiptCompactionOptions options, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var results = new List<GoalReceiptCompactionResult>();
        foreach (var id in await ListGoalIdsAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (await CompactAsync(id, options, now, ct).ConfigureAwait(false) is { } result) results.Add(result);
        }
        return results;
    }

    /// <summary>
    /// Compacts the receipt texts of one goal that <see cref="ContinualReceiptRetentionPolicy"/> releases, then rewrites the
    /// goal's body log without unreferenced records when enough of it is dead. Every other receipt fact stays the same
    /// value; the goal's version, lease and timestamps are untouched. An active, leased, verifying or uncertain goal is
    /// refused whole. Compacted receipts are saved before the log loses their bytes, so a crash between the two steps
    /// leaves dead records, never missing ones; stale offsets after a crash are repaired from the framed log on load.
    /// Null when the goal does not exist.
    /// </summary>
    public Task<GoalReceiptCompactionResult?> CompactAsync(Guid goalId, GoalReceiptCompactionOptions options, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return Access(async (document, _) =>
        {
            var index = document.Rows.FindIndex(row => row.Id == goalId);
            if (index < 0) return null;
            var row = document.Rows[index];
            var log = LogPath(row.Id);
            var logBefore = File.Exists(log) ? new FileInfo(log).Length : 0L;
            var held = new Dictionary<ContinualReceiptRetentionHold, int>();
            var goalHold = ContinualReceiptRetentionPolicy.GoalHold(row, now);
            if (goalHold != ContinualReceiptRetentionHold.None) return new GoalReceiptCompactionResult(goalId, goalHold, 0, 0, logBefore, logBefore, false, held);

            var receipts = row.Receipts.ToArray();
            var attemptEnds = ContinualReceiptRetentionPolicy.AttemptEnds(row);
            var count = 0;
            var bytes = 0L;
            for (var position = 0; position < receipts.Length; position++)
            {
                var receipt = receipts[position];
                var hold = ContinualReceiptRetentionPolicy.ReceiptHold(row, receipt, options.Retention, now, attemptEnds);
                if (hold != ContinualReceiptRetentionHold.None) { Count(held, hold); continue; }
                // A text whose log bytes are unavailable is null here and cannot be digested; it keeps its reference.
                var form = ContinualReceiptRetentionPolicy.Compact(receipt, options.Retention, now);
                if (ReferenceEquals(form, receipt)) { Count(held, ContinualReceiptRetentionHold.NothingToCompact); continue; }
                if (form.CompactedResult is { } result && receipt.CompactedResult is null) bytes += result.ByteLength;
                if (form.CompactedInput is { } input && receipt.CompactedInput is null) bytes += input.ByteLength;
                receipts[position] = form;
                count++;
            }
            if (count > 0)
            {
                document.Rows[index] = row with { Receipts = receipts };
                Journal(document, new GoalCompactionRun(now, row.Id, count, bytes, logBefore, logBefore, Describe(options.Retention)));
                await SaveAsync(document, ct).ConfigureAwait(false);
            }
            var logAfter = logBefore;
            var rewritten = false;
            if (logBefore > 0 && ShouldVacuum(document, row.Id, logBefore, options, out var retained))
            {
                var length = Vacuum(document, row.Id, log, retained);
                if (length >= 0)
                {
                    rewritten = true;
                    logAfter = length;
                    if (count > 0) document.CompactionRuns[^1] = document.CompactionRuns[^1] with { LogBytesAfter = logAfter };
                    else Journal(document, new GoalCompactionRun(now, row.Id, 0, 0, logBefore, logAfter, Describe(options.Retention)));
                    document.Dirty = true;
                }
            }
            return new GoalReceiptCompactionResult(goalId, ContinualReceiptRetentionHold.None, count, bytes, logBefore, logAfter, rewritten, held);
        }, true, ct, replaceRows: false);
    }

    private static void Count(Dictionary<ContinualReceiptRetentionHold, int> held, ContinualReceiptRetentionHold hold)
        => held[hold] = held.TryGetValue(hold, out var current) ? current + 1 : 1;

    private static string Describe(ContinualReceiptRetentionOptions retention)
        => $"closed attempts after {retention.ClosedAttemptAge.TotalDays:0.#} days, finished goals after {retention.TerminalGoalAge.TotalDays:0.#} days, texts over {retention.MinimumTextLength} chars, {retention.ExcerptBytes}-byte excerpt";

    private static void Journal(Document document, GoalCompactionRun run)
    {
        document.CompactionRuns.Add(run);
        while (document.CompactionRuns.Count > CompactionJournalLimit) document.CompactionRuns.RemoveAt(0);
    }

    /// <summary>
    /// Dead bytes are the log minus every referenced record with its frame; the thresholds keep small or mostly-live logs
    /// alone. A goal with a retained text whose bytes are unavailable is never vacuumed: the rewrite could not verify that
    /// record, so it would only rescan the whole log and abort on every pass.
    /// </summary>
    private bool ShouldVacuum(Document document, Guid rowId, long logLength, GoalReceiptCompactionOptions options, out List<KeyValuePair<string, BodyReference>> retained)
    {
        var prefix = rowId.ToString("N") + "/";
        retained = document.Bodies.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(pair => pair.Value.Offset).ToList();
        if (retained.Any(pair => Unavailable.ContainsKey(pair.Key)))
        {
            logger?.LogInformation("The body log of goal {Goal} is not rewritten while a retained text is unavailable; restore the log first.", rowId);
            return false;
        }
        var live = retained.Sum(pair => FrameLength(pair.Key, pair.Value.Length));
        var dead = logLength - live;
        if (dead <= 0) return false;
        if (retained.Count == 0) return true;
        return dead >= options.VacuumMinimumDeadBytes && dead >= logLength * options.VacuumMinimumDeadShare;
    }

    private static long FrameLength(string key, int length)
        => Frame.Length + 1 + Encoding.UTF8.GetByteCount(key) + 1 + length.ToString().Length + 1 + length + 1;

    /// <summary>
    /// Writes the retained records to a new log and swaps it in. Every record is verified against its hash first (falling
    /// back to the framed scan when a reference is stale); one unverifiable record aborts the rewrite and keeps the old log,
    /// so bytes the inventory references are never destroyed. Returns the new length, or -1 when nothing changed.
    /// </summary>
    private long Vacuum(Document document, Guid rowId, string log, List<KeyValuePair<string, BodyReference>> retained)
    {
        var temporary = log + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var moved = new Dictionary<string, BodyReference>(StringComparer.Ordinal);
        Dictionary<(string Key, string Sha256), BodyReference>? scanned = null;
        try
        {
            using (var source = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                foreach (var (key, reference) in retained)
                {
                    var bytes = ReadVerified(source, reference, key) ?? ReadVerified(source, Rescan(log, ref scanned, key, reference), key);
                    if (bytes is null)
                    {
                        logger?.LogWarning("The body log of goal {Goal} was not rewritten: retained text {Key} could not be verified. The log is unchanged.", rowId, key);
                        return -1;
                    }
                    target.Write(Encoding.UTF8.GetBytes(Frame + "\n" + key + "\n" + bytes.Length + "\n"));
                    var offset = target.Position;
                    target.Write(bytes, 0, bytes.Length);
                    target.Write("\n"u8);
                    moved[key] = new BodyReference(offset, bytes.Length, reference.Sha256);
                }
                target.Flush(flushToDisk: true);
            }
            if (moved.Count == 0)
            {
                File.Delete(temporary);
                File.Delete(log);
            }
            else File.Move(temporary, log, overwrite: true);
            foreach (var pair in moved) document.Bodies[pair.Key] = pair.Value;
            return moved.Count == 0 ? 0 : new FileInfo(log).Length;
        }
        catch (IOException ex)
        {
            logger?.LogWarning("The body log of goal {Goal} was not rewritten ({Error}). The log is unchanged.", rowId, ex.GetType().Name);
            return -1;
        }
        catch (UnauthorizedAccessException ex)
        {
            logger?.LogWarning("The body log of goal {Goal} was not rewritten ({Error}). The log is unchanged.", rowId, ex.GetType().Name);
            return -1;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static byte[]? ReadVerified(FileStream stream, BodyReference? reference, string key)
    {
        if (reference is null || reference.Offset < 0 || reference.Length < 0 || reference.Offset > stream.Length - reference.Length) return null;
        var bytes = new byte[reference.Length];
        stream.Seek(reference.Offset, SeekOrigin.Begin);
        stream.ReadExactly(bytes);
        return string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), reference.Sha256, StringComparison.OrdinalIgnoreCase) ? bytes : null;
    }

    /// <summary>Finds the framed record with this key and hash in the log (offsets in the inventory may be stale after an interrupted rewrite).</summary>
    private static BodyReference? Rescan(string log, ref Dictionary<(string Key, string Sha256), BodyReference>? scanned, string key, BodyReference reference)
    {
        scanned ??= IndexLog(log);
        return scanned.TryGetValue((key, reference.Sha256.ToUpperInvariant()), out var found) ? found : null;
    }

    private static Dictionary<(string Key, string Sha256), BodyReference> IndexLog(string log)
    {
        var index = new Dictionary<(string Key, string Sha256), BodyReference>();
        foreach (var pair in ScanLog(log)) index[(pair.Key, pair.Value.Sha256.ToUpperInvariant())] = pair.Value;
        return index;
    }

    /// <summary>
    /// What the goals folder holds, from the file system alone (no lock, no parse): the inventory, the body logs, and every
    /// file or folder the store does not use, with sizes, so the owner can see what a tool or a copy left behind and remove it.
    /// </summary>
    public GoalStoreInventoryReport Inspect()
    {
        var directory = Path.GetDirectoryName(_path)!;
        var stray = new List<GoalStoreFile>();
        long inventory = 0, logBytes = 0;
        var logs = 0;
        if (Directory.Exists(directory))
        {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if (entry is FileInfo file)
                {
                    if (file.Name == Path.GetFileName(_path)) inventory = file.Length;
                    else if (SiblingStoreFiles.Contains(file.Name) || IsInFlightTemporary(file)) continue;
                    else stray.Add(new GoalStoreFile(file.FullName, file.Length, StrayKind));
                }
                else if (entry is DirectoryInfo folder && !string.Equals(folder.FullName, _bodies, StringComparison.Ordinal))
                    stray.Add(new GoalStoreFile(folder.FullName, FolderBytes(folder), StrayKind));
            }
        }
        if (Directory.Exists(_bodies))
        {
            foreach (var file in new DirectoryInfo(_bodies).EnumerateFiles())
            {
                if (BodyLogName().IsMatch(file.Name)) { logs++; logBytes += file.Length; }
                else if (!IsInFlightTemporary(file)) stray.Add(new GoalStoreFile(file.FullName, file.Length, StrayKind));
            }
            foreach (var folder in new DirectoryInfo(_bodies).EnumerateDirectories()) stray.Add(new GoalStoreFile(folder.FullName, FolderBytes(folder), StrayKind));
        }
        return new GoalStoreInventoryReport(directory, inventory, logs, logBytes, stray.OrderByDescending(file => file.Bytes).ThenBy(file => file.Path, StringComparer.Ordinal).ToArray());
    }

    private static long FolderBytes(DirectoryInfo folder)
    {
        try { return folder.EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length); }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    /// <summary>A writer's temporary file (<c>name.&lt;32 hex&gt;.tmp</c>) that is still fresh; one left behind for longer is listed as unused.</summary>
    private static bool IsInFlightTemporary(FileInfo file)
        => TemporaryName().IsMatch(file.Name) && DateTime.UtcNow - file.LastWriteTimeUtc < TimeSpan.FromMinutes(10);

    [GeneratedRegex("^[0-9a-f]{32}\\.log$")]
    private static partial Regex BodyLogName();

    [GeneratedRegex("\\.[0-9a-f]{32}\\.tmp$")]
    private static partial Regex TemporaryName();
}
