using Microsoft.Extensions.Options;
using SwarmCore.Abstractions.Computer;

namespace SwarmCore.Coding.Computer;

/// <summary>Native window geometry is in global screen points. Captures use one pixel per point.</summary>
public sealed record MacDesktopWindow(string Id, string Title, uint ProcessId, long ProcessStartTicks,
    double X, double Y, int Width, int Height);

/// <summary>The host owns Apple APIs and permissions. Input implementations recheck identity/focus/geometry
/// before each event, including hit ownership for pointer input, and always release admitted held input.</summary>
public interface IMacDesktopDriver
{
    Task<IReadOnlyList<MacDesktopWindow>> ListAsync(CancellationToken ct);
    Task ActivateAsync(MacDesktopWindow window, CancellationToken ct);
    Task<byte[]> CaptureAsync(MacDesktopWindow window, CancellationToken ct);
    Task<bool> IsCurrentAsync(MacDesktopWindow window, bool requireFocus, CancellationToken ct);
    Task ExecuteAsync(MacDesktopWindow window, ComputerAction action, CancellationToken ct);
}

/// <summary>One-shot observations and bounded batches, shared by the API harness and native agent bridge.</summary>
public sealed class MacComputerSession(IOptions<CodingComputerOptions> options, IMacDesktopDriver? driver = null) : IComputerSession
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string sessionId = Guid.NewGuid().ToString("N");
    private MacDesktopWindow? selected;
    private ComputerObservation? current;

    private IMacDesktopDriver RequireDriver()
    {
        if (!options.Value.Enabled) throw new ComputerOperationException("Computer access is disabled in Settings.");
        return driver ?? throw new ComputerOperationException("Native Mac app control requires the Talavine Code macOS desktop host.");
    }

    public async Task<IReadOnlyList<DesktopWindowInfo>> ListAsync(CancellationToken ct = default)
        => (await RequireDriver().ListAsync(ct).ConfigureAwait(false))
            .Select(w => new DesktopWindowInfo(w.Id, w.Title, w.ProcessId, w.Width, w.Height)).ToArray();

    public async Task<ComputerObservation> ObserveAsync(string? url = null, CancellationToken ct = default)
    {
        var native = RequireDriver();
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            current = null;
            var windows = await native.ListAsync(ct).ConfigureAwait(false);
            selected = windows.FirstOrDefault(w => w.Id == (url ?? selected?.Id))
                ?? throw new ComputerOperationException("Select a visible window ID returned by computer.windows.");
            if (selected.Width is < 1 or > 8192 || selected.Height is < 1 or > 8192)
                throw new ComputerOperationException("Invalid window size.");
            await native.ActivateAsync(selected, ct).ConfigureAwait(false);
            return await CaptureAsync(native, ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task<ComputerActionResult> ExecuteBatchAsync(ComputerActionBatch batch, CancellationToken ct = default)
    {
        var native = RequireDriver();
        await gate.WaitAsync(ct).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        ct = deadline.Token;
        var completed = 0;
        var dispatched = false;
        try
        {
            if (current is null || selected is null || batch.SessionId != sessionId || batch.SurfaceId != selected.Id
                || batch.ObservationId != current.ObservationId) return Failure("stale-or-missing-observation");
            current = null; // Invalid, cancelled and uncertain attempts cannot reuse the observation either.
            if (batch.Actions.Count is < 1 or > 64) return Failure("invalid-action-batch");
            foreach (var action in batch.Actions) ValidateAction(action, selected.Width, selected.Height);
            foreach (var action in batch.Actions)
            {
                ct.ThrowIfCancellationRequested();
                if (!await native.IsCurrentAsync(selected, true, ct).ConfigureAwait(false))
                    return Failure("window-focus-or-bounds-changed; observe again");
                // Driver may throw after input; conservatively mark that attempt uncertain.
                dispatched |= action.Type is not ("wait" or "screenshot");
                await native.ExecuteAsync(selected, action, ct).ConfigureAwait(false);
                completed++;
            }
            await Task.Delay(100, ct).ConfigureAwait(false);
            return new(true, completed, await CaptureAsync(native, ct).ConfigureAwait(false))
                { Mode = "desktop", BeforeObservationId = batch.ObservationId };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            current = null;
            return Failure(ex is ComputerOperationException or ArgumentException ? ex.Message : "Mac app control failed; observe the window before retrying.");
        }
        finally { gate.Release(); }
        ComputerActionResult Failure(string error) => new(false, completed, null, error)
            { Mode = "desktop", BeforeObservationId = batch.ObservationId, OutcomeUncertain = dispatched };
    }

    private async Task<ComputerObservation> CaptureAsync(IMacDesktopDriver native, CancellationToken ct)
    {
        current = null;
        if (selected is null || !await native.IsCurrentAsync(selected, false, ct).ConfigureAwait(false))
            throw new ComputerOperationException("Window changed; select and observe it again.");
        var png = await native.CaptureAsync(selected, ct).ConfigureAwait(false);
        if (!await native.IsCurrentAsync(selected, false, ct).ConfigureAwait(false))
            throw new ComputerOperationException("Window changed during capture; observe again.");
        return current = new(sessionId, selected.Id, Guid.NewGuid().ToString("N"), "", DateTimeOffset.UtcNow,
            selected.Width, selected.Height, 1)
        { Title = selected.Title, Trust = "untrusted-desktop-evidence", PngBase64 = Convert.ToBase64String(png) };
    }

    public static void ValidateAction(ComputerAction action, int width, int height)
    {
        // Geometry and batch limits are the same on both platforms; validate Mac key names separately.
        WindowsComputerSession.ValidateAction(action with { Keys = [] , Type = action.Type == "keypress" ? "screenshot" : action.Type }, width, height);
        if (action.Keys.Count > 16 || action.Type == "keypress" && action.Keys.Count == 0)
            throw new ArgumentException("Keys required (at most 16).");
        foreach (var key in action.Keys) _ = MacDesktopKeys.Code(key);
    }
}

public static class MacDesktopKeys
{
    private static readonly Dictionary<string, ushort> Codes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"]=0,["S"]=1,["D"]=2,["F"]=3,["H"]=4,["G"]=5,["Z"]=6,["X"]=7,["C"]=8,["V"]=9,["B"]=11,
        ["Q"]=12,["W"]=13,["E"]=14,["R"]=15,["Y"]=16,["T"]=17,["1"]=18,["2"]=19,["3"]=20,["4"]=21,["6"]=22,["5"]=23,
        ["9"]=25,["7"]=26,["8"]=28,["0"]=29,["O"]=31,["U"]=32,["I"]=34,["P"]=35,["L"]=37,["J"]=38,["K"]=40,["N"]=45,["M"]=46,
        ["ENTER"]=36,["RETURN"]=36,["TAB"]=48,["SPACE"]=49,["BACKSPACE"]=51,["ESC"]=53,["ESCAPE"]=53,
        ["CMD"]=55,["COMMAND"]=55,["META"]=55,["SHIFT"]=56,["ALT"]=58,["OPTION"]=58,["CTRL"]=59,["CONTROL"]=59,
        ["HOME"]=115,["PAGEUP"]=116,["DELETE"]=117,["END"]=119,["PAGEDOWN"]=121,
        ["LEFT"]=123,["ARROWLEFT"]=123,["RIGHT"]=124,["ARROWRIGHT"]=124,["DOWN"]=125,["ARROWDOWN"]=125,["UP"]=126,["ARROWUP"]=126,
        ["F1"]=122,["F2"]=120,["F3"]=99,["F4"]=118,["F5"]=96,["F6"]=97,["F7"]=98,["F8"]=100,["F9"]=101,["F10"]=109,["F11"]=103,["F12"]=111,
    };
    public static ushort Code(string key) => Codes.TryGetValue(key, out var code) ? code : throw new ArgumentException("Unsupported Mac key: " + key);
}
