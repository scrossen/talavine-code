using Microsoft.AspNetCore.Components;
using SwarmCode.App.Services;
using SwarmCode.Hosting;
using SwarmCore.Coding.Ide;

namespace SwarmCode.App.Components.Pages;

/// <summary>
/// Editor integration on the session page: "Open in VS Code" targets for completed tool cards, and selections the
/// VS Code extension sent, inserted into this session's composer as quoted reference blocks (never sent).
/// </summary>
public partial class Session
{
    [Inject] private IdeIntegration Ide { get; set; } = default!;

    private readonly Dictionary<string, (int Version, IReadOnlyList<EditorTarget> Targets)> _editorTargets = new(StringComparer.Ordinal);
    private bool _ideAttached;

    /// <summary>The file locations a finished tool call touched; parsed once per result.</summary>
    private IReadOnlyList<EditorTarget> EditorTargets(ToolCallEntry tool)
    {
        if (tool.State == ToolCallState.Running) return [];
        var version = HashCode.Combine(tool.InputJson.Length, tool.Result?.Length ?? -1, tool.State);
        if (_editorTargets.TryGetValue(tool.Id, out var cached) && cached.Version == version) return cached.Targets;
        var targets = ToolFileReferences.Extract(tool.InputJson, tool.Result);
        _editorTargets[tool.Id] = (version, targets);
        return targets;
    }

    private void AttachIde()
    {
        if (_ideAttached) return;
        _ideAttached = true;
        Ide.SelectionRouted += OnIdeSelectionRouted;
    }

    private void DetachIde()
    {
        if (!_ideAttached) return;
        _ideAttached = false;
        Ide.SelectionRouted -= OnIdeSelectionRouted;
    }

    private void OnIdeSelectionRouted(string sessionId)
    {
        if (_disposed || !string.Equals(sessionId, SessionId, StringComparison.Ordinal)) return;
        _ = InvokeAsync(async () =>
        {
            if (_disposed || _attaching) return; // AttachAsync takes it once the runner is attached
            if (TakeIdeSelection()) { StateHasChanged(); await FocusComposerAsync(); }
        });
    }

    /// <summary>Appends any parked editor selections to the draft; true when something was added.</summary>
    private bool TakeIdeSelection()
    {
        if (_runner is null || Ide.TakePending(SessionId) is not { } block) return false;
        _draft = IdeReferenceFormatter.AppendToDraft(_draft, block);
        return true;
    }

    private async Task FocusComposerAsync()
    {
        try { if (_composer is not null) await _composer.FocusAsync(); }
        catch (Exception ex) when (ex is Microsoft.JSInterop.JSException or InvalidOperationException) { }
    }
}
