namespace SwarmCore.RemoteControl;

public enum CommandKind
{
    Unknown,
    Help,
    Quit,
    ListDisplays,
    ListWindows,
    FindWindow,
    FocusWindow,
    Screenshot,
    ListControls,
    FindControl,
    Inspect,
    Click,
    Invoke,
    SetValue,
    TypeText,
    PressKeys,
    MoveMouse,
    Scroll,
    Wait,
    WaitFor,
    Where,
    /// <summary>Press at <see cref="RemoteCommand.Point"/>/<see cref="RemoteCommand.Control"/> and release at <see cref="RemoteCommand.TargetPoint"/>/<see cref="RemoteCommand.TargetControl"/>.</summary>
    Drag,
    /// <summary>Turn the user-activity guard off, on (the configured threshold), or to <see cref="RemoteCommand.Delay"/> of idle time.</summary>
    SetGuard,
}

public enum WaitTarget
{
    /// <summary>A window matching <see cref="RemoteCommand.Window"/> exists.</summary>
    WindowAppears,
    /// <summary>The window no longer exists.</summary>
    WindowGone,
    /// <summary>A control matching <see cref="RemoteCommand.Control"/> exists in the window.</summary>
    ControlAppears,
    /// <summary>The control no longer exists.</summary>
    ControlGone,
    /// <summary>The control's value equals <see cref="RemoteCommand.Value"/> (case-insensitive, trimmed).</summary>
    ControlValueEquals,
    /// <summary>The control's value differs from what it was when the wait started.</summary>
    ControlValueChanges,
    /// <summary>The control is enabled.</summary>
    ControlEnabled,
}

/// <summary>What <see cref="CommandKind.WaitFor"/> waits for; the timeout is <see cref="RemoteCommand.Delay"/> (default 30 s, polled every 75 ms).</summary>
public sealed record WaitCondition(WaitTarget Target);

/// <summary>
/// How the user named a window. Exactly one of <see cref="Alias"/> (session alias such as "w3"), <see cref="Handle"/>
/// (raw native handle), or <see cref="NameText"/> (case-insensitive substring of the title or process name,
/// e.g. "chrome") is set. <see cref="Hint"/> carries extra descriptive words the user added after an id
/// ("window w3 chrome"), used only to confirm the match.
/// </summary>
public sealed record WindowSelector(string? Alias = null, long? Handle = null, string? NameText = null)
{
    public string? Hint { get; init; }
    public bool IsEmpty => Alias is null && Handle is null && string.IsNullOrWhiteSpace(NameText);
    public override string ToString() => Alias ?? Handle?.ToString() ?? NameText ?? "(none)";
}

/// <summary>
/// How the user named a control. <see cref="Alias"/> is a session alias ("c12"); <see cref="IdText"/> is a literal
/// id the user gave ("control id Y") that may match an alias, an AutomationId, or a name; <see cref="ControlType"/>
/// is a platform-neutral type the user said ("link", "button", "text box"), already normalized by the parser;
/// <see cref="NameText"/> is the visible label; <see cref="Ordinal"/> is 1-based for "the second button".
/// </summary>
public sealed record ControlSelector(string? Alias = null, string? IdText = null, string? ControlType = null, string? NameText = null)
{
    public int? Ordinal { get; init; }
    public bool IsEmpty => Alias is null && IdText is null && ControlType is null && string.IsNullOrWhiteSpace(NameText);
    public override string ToString() => Alias ?? IdText ?? (ControlType is null ? NameText : $"{ControlType} '{NameText}'") ?? "(none)";
}

public enum ScreenshotTarget { Primary, Display, AllDisplays, Window, Region }

/// <summary>The parsed form of one line of user text. The parser never touches the desktop; the session resolves selectors.</summary>
public sealed record RemoteCommand(CommandKind Kind)
{
    public string RawText { get; init; } = "";
    public WindowSelector? Window { get; init; }
    public ControlSelector? Control { get; init; }
    /// <summary>Text for <see cref="CommandKind.SetValue"/> and <see cref="CommandKind.TypeText"/>.</summary>
    public string? Value { get; init; }
    /// <summary>Key chord for <see cref="CommandKind.PressKeys"/>, e.g. "ctrl+s".</summary>
    public string? Keys { get; init; }
    /// <summary>For <see cref="CommandKind.PressKeys"/>: every chord in order ("ctrl+c then ctrl+v"); <see cref="Keys"/> is the first.</summary>
    public IReadOnlyList<string> KeySequence { get; init; } = [];
    /// <summary>For <see cref="CommandKind.PressKeys"/>: how many times to press the whole sequence ("press tab 3 times").</summary>
    public int Repeat { get; init; } = 1;
    /// <summary>For <see cref="CommandKind.Click"/>: modifier chord held during the click ("shift", "primary"), or null.</summary>
    public string? Modifiers { get; init; }
    /// <summary>For <see cref="CommandKind.Drag"/>: where to release, as a point or a control.</summary>
    public ScreenPoint? TargetPoint { get; init; }
    public ControlSelector? TargetControl { get; init; }
    public ScreenshotTarget Target { get; init; } = ScreenshotTarget.Primary;
    /// <summary>1-based display index for screen captures and display-relative points.</summary>
    public int? DisplayIndex { get; init; }
    public ScreenRect? Region { get; init; }
    /// <summary>Absolute virtual-desktop point, or display-relative when <see cref="DisplayIndex"/> is set and <see cref="PointIsDisplayRelative"/> is true.</summary>
    public ScreenPoint? Point { get; init; }
    public bool PointIsDisplayRelative { get; init; }
    public MouseButton Button { get; init; } = MouseButton.Left;
    public int Clicks { get; init; } = 1;
    /// <summary>For <see cref="CommandKind.Screenshot"/> of a window: also list its controls.</summary>
    public bool IncludeControls { get; init; }
    /// <summary>For control listings: the user asked for everything ("all controls", "every element"), including unnamed structural elements.</summary>
    public bool Verbose { get; init; }
    public int ScrollX { get; init; }
    public int ScrollY { get; init; }
    public TimeSpan? Delay { get; init; }
    /// <summary>For <see cref="CommandKind.WaitFor"/>.</summary>
    public WaitCondition? Wait { get; init; }
    /// <summary>Set on <see cref="CommandKind.Unknown"/>: what could not be understood, in user-facing words.</summary>
    public string? Error { get; init; }
}

/// <summary>Result of executing one command. <see cref="Text"/> is ready to print; <see cref="Files"/> are files written (screenshots).</summary>
public sealed record CommandResult(bool Succeeded, string Text)
{
    public IReadOnlyList<string> Files { get; init; } = [];
    /// <summary>Structured payload for JSON hosts: the display/window/control lists or the action result.</summary>
    public object? Data { get; init; }
    public string? Error { get; init; }
    /// <summary>One of <see cref="FailureCategories"/> when <see cref="Succeeded"/> is false, so a caller can pick the right recovery.</summary>
    public string? FailureCategory { get; init; }
    public bool QuitRequested { get; init; }
    public static CommandResult Fail(string error, string? category = null) => new(false, error) { Error = error, FailureCategory = category };
}

/// <summary>Host-supplied settings for a <c>RemoteControlSession</c>.</summary>
public sealed record RemoteControlSessionOptions
{
    /// <summary>Where screenshot PNGs are written. The session bounds the folder by <see cref="MaxRetainedCaptures"/> and <see cref="CaptureMaxAge"/>.</summary>
    public required string CaptureDirectory { get; init; }
    public int MaxRetainedCaptures { get; init; } = 200;
    public TimeSpan CaptureMaxAge { get; init; } = TimeSpan.FromDays(7);
    /// <summary>Also embed each screenshot as base64 in <see cref="CommandResult.Data"/> for hosts that cannot read files (remote callers).</summary>
    public bool EmbedScreenshotBase64 { get; init; }
    /// <summary>
    /// Optional downscaler for embedded screenshots: takes the PNG and the longest side allowed
    /// (<see cref="EmbeddedImageMaxSide"/>) and returns the image to embed with its MIME type, size and scale factor.
    /// The file on disk always keeps the full capture. Null embeds the full PNG.
    /// </summary>
    public Func<byte[], int, (byte[] Bytes, string Mime, int Width, int Height, double Scale)>? ScreenshotScaler { get; init; }
    /// <summary>Longest side passed to <see cref="ScreenshotScaler"/>.</summary>
    public int EmbeddedImageMaxSide { get; init; } = 1568;
    /// <summary>Most rows a control listing's text shows (interactive controls first); 0 shows every row. <c>Data</c> always has all of them.</summary>
    public int ListingMaxRows { get; init; }
    /// <summary>Longest name or value shown in a listing's text before it is cut with an ellipsis; 0 never cuts.</summary>
    public int ListingNameMaxLength { get; init; }
    /// <summary>When a control listing is longer than this many rows, the text output is still complete; this only decides where a "N controls" summary line is added. Zero disables the summary.</summary>
    public int SummaryThreshold { get; init; } = 40;
    /// <summary>How long a control listing may be reused for name lookups before the window is walked again.</summary>
    public TimeSpan ListingFreshness { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>
    /// Before an action that takes focus or sends input, the person must have been idle (no keyboard or mouse input)
    /// at least this long, so their typing never lands in a field the session focused. <see cref="TimeSpan.Zero"/> disables the guard.
    /// </summary>
    public TimeSpan UserIdleRequired { get; init; } = TimeSpan.FromMilliseconds(350);
    /// <summary>
    /// How long an action waits for the person to go idle before failing with <see cref="FailureCategories.UserActive"/>.
    /// The first command of a batch (or a single command) waits this long twice before failing.
    /// </summary>
    public TimeSpan UserIdleWaitLimit { get; init; } = TimeSpan.FromSeconds(1.5);
}
