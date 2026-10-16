using SwarmCore.Coding.History;

namespace SwarmCode.Hosting;

/// <summary>
/// The history tools' view of every saved session, across repositories: the session index (title, repository,
/// activity) joined with each session's visible turns from its turns journal. Works with or without an open
/// workspace, since every file lives under the shared data directory. Each load reads the journals fresh, so a
/// search sees turns another workspace or process wrote a moment ago; the per-session header is what makes
/// filtering by period and project cheap before any journal is opened.
/// </summary>
public sealed class ConversationHistorySource(SwarmCodeDataPaths paths, WorkspaceSessionCatalog catalog) : IConversationHistorySource
{
    public async Task<IReadOnlyList<HistorySessionDocument>> LoadAsync(HistoryScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var sessions = await catalog.ListAsync(ct: ct).ConfigureAwait(false);
        // A fresh store reads what is on disk now; the workspace's own instance caches the sessions it writes.
        var store = new FileConversationStore(paths);
        var result = new List<HistorySessionDocument>();
        foreach (var session in sessions)
        {
            ct.ThrowIfCancellationRequested();
            if (!scope.IncludeAgentSessions && session.ParentSessionId is not null) continue;
            if (!scope.Covers(session.RepositoryRoot, session.CreatedUtc, session.LastActivityUtc)) continue;
            var turns = await store.LoadTurnsAsync(session.Id, ct).ConfigureAwait(false);
            result.Add(ToDocument(session, turns));
        }
        return result;
    }

    public static HistorySessionDocument ToDocument(WorkspaceSessionInfo session, IReadOnlyList<SwarmCore.Agent.Runtime.ChatTurnInfo> turns)
        => new(session.Id, session.Title, session.RepositoryRoot, session.CreatedUtc, session.LastActivityUtc,
            turns.OrderBy(t => t.Sequence).Select(t => new HistoryTurn(t.Id, t.UserText, t.AssistantText, t.CreatedUtc, t.CompletedUtc, t.IsContinuation)).ToList())
        { Lane = session.LaneName };
}
