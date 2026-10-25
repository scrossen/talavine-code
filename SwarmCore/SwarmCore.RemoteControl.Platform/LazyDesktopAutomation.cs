namespace SwarmCore.RemoteControl.Platform;

/// <summary>
/// Defers creating the platform adapter until the first operation. Creation failures surface as
/// <see cref="DesktopAutomationException"/> with a user-facing message, so the session reports them like any other
/// desktop failure; a failed attempt is retried on the next operation (permission may have been granted meanwhile).
/// </summary>
internal sealed class LazyDesktopAutomation(Func<string?>? unavailableReason, Func<IDesktopAutomation> create)
    : IDesktopAutomation, IDisposable
{
    private readonly Lock gate = new();
    private IDesktopAutomation? inner;
    private bool disposed;

    /// <summary>True once the platform adapter exists (for diagnostics and tests).</summary>
    public bool IsCreated { get { lock (gate) return inner is not null; } }

    private IDesktopAutomation Inner
    {
        get
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (inner is not null) return inner;
                if (unavailableReason?.Invoke() is { Length: > 0 } reason)
                    throw new DesktopAutomationException(reason, FailureCategories.Permission);
                try { return inner = create(); }
                catch (PlatformNotSupportedException ex)
                {
                    throw new DesktopAutomationException(ex.Message, ex, FailureCategories.Unsupported);
                }
            }
        }
    }

    public IReadOnlyList<DisplayInfo> GetDisplays() => Inner.GetDisplays();
    public IReadOnlyList<WindowInfo> GetWindows() => Inner.GetWindows();
    public WindowInfo? GetWindow(long handle) => Inner.GetWindow(handle);
    public bool FocusWindow(long handle) => Inner.FocusWindow(handle);
    public Screenshot CaptureDisplay(int displayIndex) => Inner.CaptureDisplay(displayIndex);
    public Screenshot CaptureAllDisplays() => Inner.CaptureAllDisplays();
    public Screenshot CaptureWindow(long handle) => Inner.CaptureWindow(handle);
    public Screenshot CaptureRegion(ScreenRect region) => Inner.CaptureRegion(region);
    public IReadOnlyList<ControlInfo> GetControls(long windowHandle, ControlQuery query) => Inner.GetControls(windowHandle, query);

    public bool LastWalkWasTruncated
    {
        get { lock (gate) return inner is not null && inner.LastWalkWasTruncated; }
    }

    public string? LastWalkTruncationReason
    {
        get { lock (gate) return inner?.LastWalkTruncationReason; }
    }

    public ControlInfo? RefreshControl(long windowHandle, string runtimeKey) => Inner.RefreshControl(windowHandle, runtimeKey);
    public ControlInfo? GetControlAt(ScreenPoint point) => Inner.GetControlAt(point);
    public ControlInfo? GetFocusedControl() => Inner.GetFocusedControl();
    public ControlActionResult PerformAction(long windowHandle, string runtimeKey, ControlAction action, string? value = null)
        => Inner.PerformAction(windowHandle, runtimeKey, action, value);
    public TimeSpan? GetUserIdleTime() => Inner.GetUserIdleTime();
    public ScreenPoint GetMousePosition() => Inner.GetMousePosition();
    public void MoveMouse(ScreenPoint point) => Inner.MoveMouse(point);
    public void Click(ScreenPoint point, MouseButton button = MouseButton.Left, int clicks = 1, string? modifiers = null)
        => Inner.Click(point, button, clicks, modifiers);
    public void Drag(ScreenPoint from, ScreenPoint to, MouseButton button = MouseButton.Left) => Inner.Drag(from, to, button);
    public void Scroll(ScreenPoint point, int deltaX, int deltaY) => Inner.Scroll(point, deltaX, deltaY);
    public void TypeText(string text) => Inner.TypeText(text);
    public void PressKeys(string chord) => Inner.PressKeys(chord);

    public void Dispose()
    {
        IDesktopAutomation? created;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            created = inner;
            inner = null;
        }
        (created as IDisposable)?.Dispose();
    }
}
