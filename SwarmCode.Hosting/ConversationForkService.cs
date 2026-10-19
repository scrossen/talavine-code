using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.History;

namespace SwarmCode.Hosting;

/// <summary>Copies durable history into new private files, then atomically publishes the fork in the catalog.</summary>
public sealed class ConversationForkService(WorkspaceSessionCatalog catalog, FileAgentSessionStore transcripts,
    IConversationStore conversations, FileTranscriptJournal journal, FileConversationJobStore jobs)
{
    public async Task<WorkspaceSessionInfo> ForkAsync(string sourceId, CancellationToken ct = default)
    {
        WorkspaceSessionInfo? target = null;
        try
        {
            return await catalog.PublishForkAsync(sourceId, async (source, fork) =>
            {
                target = fork;
                if (jobs.Load(sourceId)?.State == "Running")
                    throw new InvalidOperationException("Stop the current turn before forking this conversation.");
                if (conversations is not FileConversationStore store)
                    throw new NotSupportedException("This conversation store does not support forking.");
                var history = ConversationFork.CopyTranscript(await transcripts.LoadAsync(sourceId, ct).ConfigureAwait(false));
                var turns = await conversations.LoadTurnsAsync(sourceId, ct).ConfigureAwait(false);
                await store.ImportForkAsync(fork.Id, fork.Title, turns, ct).ConfigureAwait(false);
                await transcripts.AppendRangeAsync(fork.Id, history, ct).ConfigureAwait(false);
                if (journal.Exists(sourceId))
                {
                    var visible = journal.Load(sourceId);
                    // Clear markers stay effective. Historical approvals, questions and undo receipts cannot be acted on in a fork.
                    // The visible snapshot is already complete through this entire historical transcript.
                    // Reconciliation must never reconstruct hidden/cleared cards or newly closed uncertain calls.
                    journal.AppendClear(fork.Id, history.Count);
                    journal.AppendRange(fork.Id, visible.Entries.Where(e => e is UserEntry or AssistantEntry or ToolCallEntry or NoticeEntry));
                }
                else journal.AppendRange(fork.Id, TranscriptHistory.Build(turns, history));
                journal.Append(fork.Id, new NoticeEntry
                {
                    Kind = NoticeKind.Info,
                    Text = "Forked conversation. Earlier messages and tool results are historical; verify current workspace state before continuing. The original chat and working files are unchanged.",
                });
            }, ct).ConfigureAwait(false);
        }
        catch
        {
            // A notification subscriber can throw after publication; never erase a published fork.
            if (target is not null && await catalog.GetAsync(target.Id, CancellationToken.None).ConfigureAwait(false) is null)
            {
                await transcripts.ClearAsync(target.Id).ConfigureAwait(false);
                await conversations.DeleteAsync(target.Id, CancellationToken.None).ConfigureAwait(false);
                journal.Delete(target.Id);
            }
            throw;
        }
    }
}
