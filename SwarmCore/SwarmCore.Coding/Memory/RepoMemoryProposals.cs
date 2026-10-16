using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using SwarmCore.Abstractions.Security;

namespace SwarmCore.Coding.Memory;

public enum RepoMemoryProposalKind
{
    /// <summary>Replace one exact, unique passage of an existing file.</summary>
    Replace = 0,

    /// <summary>Append to the end of an existing file.</summary>
    Append = 1,

    /// <summary>Create a new topic file.</summary>
    Create = 2,
}

public enum RepoMemoryProposalStatus
{
    Pending = 0,
    Applied = 1,
    Discarded = 2,

    /// <summary>The file changed so the anchored edit no longer applies; the proposal is kept for reference.</summary>
    Stale = 3,
}

/// <summary>
/// A reviewable edit to repository memory proposed by an agent. Nothing is written to the repository until the
/// person applies it, and applying never commits. The edit is anchored: <see cref="OldText"/> must still occur
/// exactly once when applied (a checked baseline), so an edit made against an old version of the file cannot
/// silently overwrite newer text.
/// </summary>
public sealed record RepoMemoryProposal
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string RepositoryRoot { get; init; }
    public required string RelativePath { get; init; }
    public required RepoMemoryProposalKind Kind { get; init; }
    public string OldText { get; init; } = string.Empty;
    public required string NewText { get; init; }
    public required string Rationale { get; init; }

    /// <summary>SHA-256 of the file when the proposal was made (null for a new file). Informational; the anchor is the check.</summary>
    public string? BaseSha256 { get; init; }

    /// <summary>Unified diff against the file as it was when proposed.</summary>
    public required string Diff { get; init; }
    public string? ConversationId { get; init; }
    public RepoMemoryProposalStatus Status { get; init; } = RepoMemoryProposalStatus.Pending;
    public string? StatusReason { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Store-assigned optimistic concurrency version (0 = not yet stored).</summary>
    public long Version { get; init; }
}

/// <summary>Host persistence for proposals. Status changes are version-checked; nothing else is ever rewritten.</summary>
public interface IRepoMemoryProposalStore
{
    /// <summary>Stores a new proposal (Version must be 0; the store assigns 1).</summary>
    Task<RepoMemoryProposal> AddAsync(RepoMemoryProposal proposal, CancellationToken ct = default);

    Task<RepoMemoryProposal?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Proposals for one repository (null = all), newest first.</summary>
    Task<IReadOnlyList<RepoMemoryProposal>> ListAsync(string? repositoryRoot, bool includeClosed, CancellationToken ct = default);

    /// <summary>Moves a proposal to a new status when its version still equals <paramref name="expectedVersion"/>; null on conflict or missing.</summary>
    Task<RepoMemoryProposal?> SetStatusAsync(Guid id, long expectedVersion, RepoMemoryProposalStatus status, string? reason, CancellationToken ct = default);
}

/// <summary>In-process proposal store (tests and hosts without durable storage).</summary>
public sealed class InMemoryRepoMemoryProposalStore(TimeProvider? time = null) : IRepoMemoryProposalStore
{
    private readonly ConcurrentDictionary<Guid, RepoMemoryProposal> _items = new();
    private readonly object _gate = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public Task<RepoMemoryProposal> AddAsync(RepoMemoryProposal proposal, CancellationToken ct = default)
    {
        if (proposal.Version != 0) throw new ArgumentException("A new proposal must have version 0.", nameof(proposal));
        var now = _time.GetUtcNow();
        var stored = proposal with { Version = 1, CreatedAt = proposal.CreatedAt == default ? now : proposal.CreatedAt, UpdatedAt = now };
        if (!_items.TryAdd(stored.Id, stored)) throw new InvalidOperationException("Proposal id already exists.");
        return Task.FromResult(stored);
    }

    public Task<RepoMemoryProposal?> GetAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(_items.TryGetValue(id, out var p) ? p : null);

    public Task<IReadOnlyList<RepoMemoryProposal>> ListAsync(string? repositoryRoot, bool includeClosed, CancellationToken ct = default)
    {
        IReadOnlyList<RepoMemoryProposal> list = _items.Values
            .Where(p => repositoryRoot is null || RepoMemoryProposals.SameRoot(p.RepositoryRoot, repositoryRoot))
            .Where(p => includeClosed || p.Status == RepoMemoryProposalStatus.Pending)
            .OrderByDescending(p => p.CreatedAt).ThenBy(p => p.Id)
            .ToList();
        return Task.FromResult(list);
    }

    public Task<RepoMemoryProposal?> SetStatusAsync(Guid id, long expectedVersion, RepoMemoryProposalStatus status, string? reason, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(id, out var current) || current.Version != expectedVersion) return Task.FromResult<RepoMemoryProposal?>(null);
            var next = current with { Status = status, StatusReason = reason, UpdatedAt = _time.GetUtcNow(), Version = current.Version + 1 };
            _items[id] = next;
            return Task.FromResult<RepoMemoryProposal?>(next);
        }
    }
}

public sealed record RepoMemoryProposalResult(bool Ok, string Message, RepoMemoryProposal? Proposal = null);

/// <summary>Creates, applies and discards repository-memory proposals. Never commits; writes only on <see cref="ApplyAsync"/>.</summary>
public static class RepoMemoryProposals
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private const int MaxRationale = 600;

    public static bool SameRoot(string a, string b)
        => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), PathComparison);

    /// <summary>
    /// Validates a proposed edit against the current file and stores it as Pending with its diff. Rejects paths outside
    /// repository memory, credential-shaped text, an anchor that is missing or ambiguous, and no-op edits.
    /// </summary>
    public static async Task<RepoMemoryProposalResult> ProposeAsync(
        IRepoMemoryProposalStore store,
        string repositoryRoot,
        RepoMemoryOptions options,
        string? relativePath,
        string? oldText,
        string? newText,
        string? rationale,
        string? conversationId = null,
        CancellationToken ct = default)
    {
        var full = RepoMemoryFiles.ResolvePath(repositoryRoot, relativePath, options, out var rel, out var problem);
        if (full is null) return new(false, problem ?? "Invalid memory path.");
        var replacement = Normalize(newText);
        if (replacement.Trim().Length == 0) return new(false, "newText is required: the exact text to add or the replacement passage.");
        if (replacement.Length > Math.Max(200, options.MaxProposalCharacters)) return new(false, "The proposed text is too long; propose a focused edit.");
        var why = (rationale ?? "").Trim();
        if (why.Length == 0) return new(false, "rationale is required: say which durable convention this turn established and why it belongs in memory.");
        if (why.Length > MaxRationale) why = why[..MaxRationale];
        if (ContainsCredential(replacement) || ContainsCredential(why) || ContainsCredential(oldText ?? ""))
            return new(false, "Not proposed: repository memory must never contain passwords, tokens or keys.");

        var anchor = Normalize(oldText);
        string? current = null;
        if (File.Exists(full))
        {
            if (new FileInfo(full).Length > options.MaxFileBytes) return new(false, $"{rel} is larger than the memory edit limit.");
            current = Normalize(await File.ReadAllTextAsync(full, ct).ConfigureAwait(false));
        }

        RepoMemoryProposalKind kind;
        string updated;
        if (current is null)
        {
            if (anchor.Length > 0) return new(false, $"{rel} does not exist, so oldText cannot match. Leave oldText empty to propose a new topic file.");
            kind = RepoMemoryProposalKind.Create;
            updated = EnsureTrailingNewline(replacement);
        }
        else if (anchor.Length == 0)
        {
            kind = RepoMemoryProposalKind.Append;
            updated = EnsureTrailingNewline(current) + (current.Length == 0 || current.EndsWith("\n\n", StringComparison.Ordinal) ? "" : "\n") + EnsureTrailingNewline(replacement);
        }
        else
        {
            var count = CountOccurrences(current, anchor);
            if (count == 0) return new(false, $"oldText was not found in {rel}. Read the file and copy the exact passage to replace.");
            if (count > 1) return new(false, $"oldText occurs {count} times in {rel}; include more surrounding text so it is unique.");
            kind = RepoMemoryProposalKind.Replace;
            updated = current.Replace(anchor, replacement, StringComparison.Ordinal);
        }
        if (current is not null && string.Equals(updated, current, StringComparison.Ordinal)) return new(false, "The proposal does not change the file.");

        var proposal = await store.AddAsync(new RepoMemoryProposal
        {
            RepositoryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot)),
            RelativePath = rel,
            Kind = kind,
            OldText = anchor,
            NewText = replacement,
            Rationale = why,
            BaseSha256 = current is null ? null : Sha256(current),
            Diff = UnifiedDiff(rel, current ?? "", updated, isNew: current is null),
            ConversationId = conversationId,
        }, ct).ConfigureAwait(false);
        return new(true, "Proposed for the person's review. Nothing was written to the repository and nothing was committed.", proposal);
    }

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileGates = new(
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>
    /// Applies a pending proposal to the working tree (never commits). The anchored edit must still apply to the current
    /// file; otherwise the proposal becomes Stale and nothing is written. The file write is atomic.
    /// <para>Read, validate and write happen under one per-file lock, so two proposals for the same file applied at once
    /// are serialized (the second is computed from the first one's result). Just before the atomic replace the file is
    /// re-read: if anything else (an editor, another process) changed it since it was read, nothing is written and the
    /// proposal returns to Pending.</para>
    /// </summary>
    public static async Task<RepoMemoryProposalResult> ApplyAsync(IRepoMemoryProposalStore store, Guid id, long expectedVersion, RepoMemoryOptions options, CancellationToken ct = default)
    {
        var proposal = await store.GetAsync(id, ct).ConfigureAwait(false);
        if (proposal is null) return new(false, "The proposal no longer exists.");
        if (proposal.Version != expectedVersion) return new(false, "The proposal changed since it was shown; refresh and try again.");
        if (proposal.Status != RepoMemoryProposalStatus.Pending) return new(false, $"The proposal is already {proposal.Status.ToString().ToLowerInvariant()}.");
        var full = RepoMemoryFiles.ResolvePath(proposal.RepositoryRoot, proposal.RelativePath, options, out _, out var problem);
        if (full is null) return new(false, problem ?? "Invalid memory path.");

        var gate = FileGates.GetOrAdd(Path.GetFullPath(full), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ApplyLockedAsync(store, id, expectedVersion, proposal, full, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<RepoMemoryProposalResult> ApplyLockedAsync(IRepoMemoryProposalStore store, Guid id, long expectedVersion, RepoMemoryProposal proposal, string full, CancellationToken ct)
    {
        var raw = File.Exists(full) ? await File.ReadAllTextAsync(full, ct).ConfigureAwait(false) : null;
        var crlf = raw?.Contains("\r\n", StringComparison.Ordinal) == true;
        string? current = raw is null ? null : Normalize(raw);
        string? updated = proposal.Kind switch
        {
            RepoMemoryProposalKind.Create => current is null ? EnsureTrailingNewline(proposal.NewText) : null,
            RepoMemoryProposalKind.Append => current is null ? null
                : EnsureTrailingNewline(current) + (current.Length == 0 || current.EndsWith("\n\n", StringComparison.Ordinal) ? "" : "\n") + EnsureTrailingNewline(proposal.NewText),
            RepoMemoryProposalKind.Replace => current is not null && CountOccurrences(current, proposal.OldText) == 1
                ? current.Replace(proposal.OldText, proposal.NewText, StringComparison.Ordinal) : null,
            _ => null,
        };
        if (updated is null)
        {
            var stale = await store.SetStatusAsync(id, expectedVersion, RepoMemoryProposalStatus.Stale,
                "The file changed since the proposal was made, so the edit no longer applies cleanly.", ct).ConfigureAwait(false);
            return new(false, "The memory file changed since this was proposed; nothing was written.", stale);
        }

        // Claim the proposal first so two clicks (or two windows) cannot both write.
        var claimed = await store.SetStatusAsync(id, expectedVersion, RepoMemoryProposalStatus.Applied, "applied to the working tree (not committed)", ct).ConfigureAwait(false);
        if (claimed is null) return new(false, "The proposal changed since it was shown; refresh and try again.");
        string? tmp = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var text = crlf ? updated.Replace("\n", "\r\n", StringComparison.Ordinal) : updated;
            tmp = full + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            await File.WriteAllTextAsync(tmp, text, new UTF8Encoding(false), ct).ConfigureAwait(false);
            // The file is the row that matters: never replace content that changed after the edit was computed.
            var now = File.Exists(full) ? await File.ReadAllTextAsync(full, ct).ConfigureAwait(false) : null;
            if (!string.Equals(now, raw, StringComparison.Ordinal))
            {
                TryDelete(tmp);
                await store.SetStatusAsync(id, claimed.Version, RepoMemoryProposalStatus.Pending,
                    "the file changed while the edit was being applied", ct).ConfigureAwait(false);
                return new(false, "The memory file changed while the edit was being applied; nothing was written. Try again.");
            }
            File.Move(tmp, full, overwrite: true);
            tmp = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (tmp is not null) TryDelete(tmp);
            await store.SetStatusAsync(id, claimed.Version, RepoMemoryProposalStatus.Pending, "write failed: " + ex.GetType().Name, ct).ConfigureAwait(false);
            return new(false, "Could not write the memory file: " + ex.Message);
        }
        return new(true, $"Applied to {proposal.RelativePath} in the working tree. Review and commit it yourself.", claimed);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static async Task<RepoMemoryProposalResult> DiscardAsync(IRepoMemoryProposalStore store, Guid id, long expectedVersion, CancellationToken ct = default)
    {
        var updated = await store.SetStatusAsync(id, expectedVersion, RepoMemoryProposalStatus.Discarded, "discarded by the person", ct).ConfigureAwait(false);
        return updated is null ? new(false, "The proposal changed since it was shown; refresh and try again.") : new(true, "Discarded.", updated);
    }

    /// <summary>A single-hunk unified diff (proposals are one contiguous change) with three lines of context.</summary>
    public static string UnifiedDiff(string relativePath, string before, string after, bool isNew = false)
    {
        var a = SplitLines(before);
        var b = SplitLines(after);
        var prefix = 0;
        while (prefix < a.Count && prefix < b.Count && a[prefix] == b[prefix]) prefix++;
        var suffix = 0;
        while (suffix < a.Count - prefix && suffix < b.Count - prefix && a[a.Count - 1 - suffix] == b[b.Count - 1 - suffix]) suffix++;
        const int context = 3;
        var start = Math.Max(0, prefix - context);
        var aEnd = Math.Min(a.Count, a.Count - suffix + context);
        var bEnd = Math.Min(b.Count, b.Count - suffix + context);
        var sb = new StringBuilder();
        sb.Append("--- ").Append(isNew ? "/dev/null" : "a/" + relativePath).Append('\n');
        sb.Append("+++ b/").Append(relativePath).Append('\n');
        var aCount = aEnd - start;
        var bCount = bEnd - start;
        sb.Append("@@ -").Append(aCount == 0 ? start : start + 1).Append(',').Append(aCount)
          .Append(" +").Append(bCount == 0 ? start : start + 1).Append(',').Append(bCount).Append(" @@\n");
        for (var i = start; i < prefix; i++) sb.Append(' ').Append(a[i]).Append('\n');
        for (var i = prefix; i < a.Count - suffix; i++) sb.Append('-').Append(a[i]).Append('\n');
        for (var i = prefix; i < b.Count - suffix; i++) sb.Append('+').Append(b[i]).Append('\n');
        for (var i = a.Count - suffix; i < aEnd; i++) sb.Append(' ').Append(a[i]).Append('\n');
        return sb.ToString();
    }

    internal static bool ContainsCredential(string text)
        => text.Length > 0 && (!string.Equals(CredentialPatterns.Scrub(text, "\u0000"), text, StringComparison.Ordinal)
                               || Secrets.SecretMessageScrubber.Redact(text, Secrets.SecretScrubOptions.Governed).Text != text);

    private static List<string> SplitLines(string text)
    {
        if (text.Length == 0) return [];
        var lines = text.Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static int CountOccurrences(string text, string value)
    {
        if (value.Length == 0) return 0;
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    private static string Normalize(string? text) => (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string EnsureTrailingNewline(string text) => text.Length == 0 || text.EndsWith('\n') ? text : text + "\n";

    private static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
