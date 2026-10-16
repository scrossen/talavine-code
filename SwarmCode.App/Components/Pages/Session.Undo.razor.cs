using SwarmCode.Hosting;
using SwarmCore.Coding.Checkpoints;

namespace SwarmCode.App.Components.Pages;

/// <summary>
/// Undo for a finished turn's file edits: the turn's "N files changed · Undo" row asks the runner for a preview (off the
/// dispatcher; the runner runs git on the thread pool), shows the files and any that changed since the turn, and
/// applies the choice. The outcome lands in the transcript as a notice.
/// </summary>
public partial class Session
{
    private const int UndoListLimit = 20;

    private sealed record UndoPrompt(string EntryId, TurnUndoResult Preview);

    private UndoPrompt? _undoPrompt;
    private bool _undoBusy;

    private static string Files(int count) => count == 1 ? "1 file" : $"{count} files";

    private static string FilesChangedLabel(TurnChangesEntry entry)
        => $"{Files(entry.Files.Count)} changed" + (entry.HeadMoved ? " · commits made" : "");

    private static string UndoVerb(TurnFileChange file) => file.Kind switch
    {
        TurnFileChangeKind.Added => "Delete",
        TurnFileChangeKind.Deleted => "Restore",
        _ => "Revert",
    };

    private async Task PrepareUndoAsync(TurnChangesEntry entry)
    {
        var runner = _runner;
        if (runner is null || _undoBusy) return;
        _undoBusy = true;
        try
        {
            var preview = await runner.PreviewUndoAsync(entry.Id);
            if (preview.Status is TurnUndoStatus.Undone or TurnUndoStatus.ConflictsNeedDecision)
                _undoPrompt = new UndoPrompt(entry.Id, preview);
            else
            {
                _undoPrompt = null;
                AddNote(preview.Status == TurnUndoStatus.Failed ? "error" : "info", preview.Summary);
            }
        }
        catch (Exception ex) { AddNote("error", "Could not prepare the undo.", ex.Message); }
        finally { _undoBusy = false; }
    }

    private async Task RunUndoAsync(TurnChangesEntry entry, TurnUndoConflictMode mode)
    {
        var runner = _runner;
        if (runner is null || _undoBusy) return;
        _undoBusy = true;
        try
        {
            var outcome = await runner.UndoAsync(entry.Id, mode);
            if (outcome.Status == TurnUndoStatus.ConflictsNeedDecision)
            {
                // Something changed between the preview and the click: show the new conflicts instead.
                _undoPrompt = new UndoPrompt(entry.Id, outcome);
                return;
            }
            _undoPrompt = null;
            if (outcome.Status == TurnUndoStatus.Unavailable) AddNote("warn", outcome.Summary);
            _refreshToken++;
            _forceScroll = true;
        }
        catch (Exception ex) { AddNote("error", "Undo failed.", ex.Message); }
        finally { _undoBusy = false; }
    }

    private void CancelUndo() => _undoPrompt = null;

    /// <summary><c>/undo</c>: prepares the undo of the newest turn that changed files.</summary>
    private async Task UndoLastTurnAsync()
    {
        if (_runner?.LastUndoableTurn() is { } entry) await PrepareUndoAsync(entry);
        else AddNote("info", "No turn in this session has file changes to undo.");
    }
}
