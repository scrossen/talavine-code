namespace SwarmCore.RemoteControl;

// Structured payloads carried in CommandResult.Data so JSON hosts can return what the text shows.

public sealed record WindowEntry(string Alias, WindowInfo Window);

public sealed record ControlEntry(string Alias, ControlInfo Control);

public sealed record HelpInfo(string Text);

public sealed record DisplayListing(IReadOnlyList<DisplayInfo> Displays);

public sealed record WindowListing(IReadOnlyList<WindowEntry> Windows);

public sealed record WindowSearchResult(WindowEntry Match, IReadOnlyList<WindowEntry> Alternatives, IReadOnlyList<string> Notes);

public sealed record ControlListing(WindowEntry Window, IReadOnlyList<ControlEntry> Controls, bool Verbose)
{
    public ControlSelector? Filter { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
}

public sealed record ControlDetail(WindowEntry Window, ControlEntry Control);

/// <summary>
/// What an action did. <see cref="Method"/> names how it reached the target ("mouse-click", "value-pattern",
/// "keyboard-replace", "invoke-pattern", "none" for a no-op). <see cref="Outcome"/> is "verified", "unchanged",
/// or "issued, not verified"; <see cref="ObservedState"/> is what the re-read showed afterwards.
/// </summary>
public sealed record ActionReceipt(string Action, string Method, string Outcome)
{
    public WindowEntry? Window { get; init; }
    public ControlEntry? Control { get; init; }
    public ScreenPoint? Point { get; init; }
    public string? Value { get; init; }
    public string? Keys { get; init; }
    public string? ObservedState { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
}

public sealed record ScreenshotReceipt(string Path, int Width, int Height, ScreenRect ScreenBounds, string Subject)
{
    public WindowEntry? Window { get; init; }
    /// <summary>Set only when <see cref="RemoteControlSessionOptions.EmbedScreenshotBase64"/> is true.</summary>
    public string? PngBase64 { get; init; }
    /// <summary>When a scaler produced the embedded image: its MIME type, pixel size and scale relative to the capture (<see cref="Width"/>/<see cref="Height"/> stay the full size).</summary>
    public string? ImageMime { get; init; }
    public int? ImageWidth { get; init; }
    public int? ImageHeight { get; init; }
    public double? ImageScale { get; init; }
    public ControlListing? Controls { get; init; }
}

public sealed record WhereReport(ScreenPoint Mouse, int? DisplayIndex, ControlEntry? ControlUnderMouse, ControlEntry? FocusedControl, WindowEntry? ForegroundWindow);

public sealed record WaitReceipt(TimeSpan Delay);

public sealed record WaitForReceipt(WaitTarget Target, bool Satisfied, TimeSpan Elapsed, string LastObservedState)
{
    public WindowEntry? Window { get; init; }
    public ControlEntry? Control { get; init; }
}

public sealed record CommandFailure(CommandKind Kind, string Message, string Category);

/// <summary>Cost of the last executed command: control-tree walks, their total time, and single-control re-reads.</summary>
public sealed record ExecutionStats(int Walks, TimeSpan WalkElapsed, int Refreshes);
