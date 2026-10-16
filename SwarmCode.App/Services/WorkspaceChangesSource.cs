using Microsoft.Extensions.DependencyInjection;
using SwarmCode.Hosting;
using SwarmCore.Chat.Components;
using SwarmCore.Coding.Tools;

namespace SwarmCode.App.Services;

/// <summary>
/// The shared <see cref="ChangesPanel"/>'s data for one open repository: the workspace host's
/// <c>git.status</c>/<c>git.diff</c> tools through <see cref="WorkingTreeChangesReader"/>. A SwarmCode
/// workspace host is built for exactly one repository, so its configured sandbox root is the repository.
/// </summary>
public sealed class WorkspaceChangesSource(WorkspaceHost host) : IChangesSource
{
    public WorkspaceHost Host { get; } = host;

    private WorkingTreeChangesReader Reader => Host.Services.GetService<WorkingTreeChangesReader>()
        ?? throw new InvalidOperationException("git.status is not available.");

    public async Task<ChangesSnapshot> GetStatusAsync(CancellationToken ct)
    {
        var status = await Reader.GetStatusAsync(workspaceRoot: null, ct).ConfigureAwait(false);
        return ToSnapshot(status);
    }

    public async Task<ChangesDiff?> GetDiffAsync(ChangedFile file, CancellationToken ct)
    {
        var diff = await Reader.GetFileDiffAsync(workspaceRoot: null, file.Path, file.Status, ct).ConfigureAwait(false);
        return new ChangesDiff(diff.Diff, diff.Truncated);
    }

    /// <summary>The panel's model of a <c>git.status</c> result.</summary>
    public static ChangesSnapshot ToSnapshot(GitStatusTool.Output status)
        => status.Error is not null
            ? ChangesSnapshot.Failed(status.Error)
            : new ChangesSnapshot(status.Branch, status.Upstream, status.Ahead, status.Behind,
                status.Entries.Select(e => new ChangedFile(e.Path, e.Status)).ToList());
}
