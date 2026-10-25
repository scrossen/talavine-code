using SwarmCore.Coding.Goals;

namespace SwarmCode.Hosting.Goals;

/// <summary>
/// Reads a goal's ledger from the profile's goal inventory. Descriptions are reused per receipt instance, so a poll
/// costs the new receipts only; reported relative file paths are resolved against the goal's repository.
/// </summary>
public sealed class FileGoalLedger(FileCodingGoalStore goals) : ICodingGoalLedger
{
    private readonly CodingGoalLedgerBuilder _builder = new();

    public async Task<CodingGoalLedgerPage?> ReadAsync(Guid goalId, int offset = 0, int take = 50, bool includeObservations = false,
        CancellationToken ct = default)
    {
        var goal = await goals.GetAsync(goalId, ct).ConfigureAwait(false);
        if (goal is null) return null;
        var root = CodingGoalPayload.Read(goal).RepositoryRoot;
        var entries = _builder.Build(goal, includeObservations);
        var resolved = string.IsNullOrEmpty(root) ? entries
            : entries.Select(entry => entry.Files.Count == 0 ? entry : entry with { Files = Resolve(entry.Files, root) }).ToArray();
        return CodingGoalLedger.Page(goal, resolved, offset, take, root);
    }

    private static IReadOnlyList<string> Resolve(IReadOnlyList<string> files, string root)
    {
        var resolved = new List<string>(files.Count);
        foreach (var file in files)
        {
            var path = file;
            if (!Path.IsPathRooted(path))
            {
                try { path = Path.GetFullPath(Path.Combine(root, path)); }
                catch (ArgumentException) { }
                catch (NotSupportedException) { }
                catch (PathTooLongException) { }
            }
            if (!resolved.Contains(path, StringComparer.Ordinal)) resolved.Add(path);
        }
        return resolved;
    }
}
