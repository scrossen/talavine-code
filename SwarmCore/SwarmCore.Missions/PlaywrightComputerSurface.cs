using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Playwright;
using SwarmCore.Abstractions.Computer;

namespace SwarmCore.Missions;

/// <summary>Reusable page input mechanics. The host retains session ownership and supplies
/// its current authorization check; page content can never grant permission.</summary>
public sealed class PlaywrightComputerSurface(IPage page, Func<CancellationToken, Task> authorize,
    TimeSpan? actionTimeout = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string sessionId = Guid.NewGuid().ToString("N");
    private readonly string surfaceId = Guid.NewGuid().ToString("N");
    private ComputerObservation? current;
    private string? layout;
    private float TimeoutMs => (float)(actionTimeout ?? TimeSpan.FromSeconds(15)).TotalMilliseconds;
    public ComputerObservation? LastObservation => current;
    public async Task<ComputerObservation?> InspectLastObservationAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { return page.IsClosed || current is null || await LayoutAsync(ct) != layout ? null : current; }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException) { current = null; layout = null; return null; }
        finally { gate.Release(); }
    }

    public async Task<ComputerObservation> ObserveAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { await authorize(ct); return await CaptureAsync(ct); }
        catch { current = null; layout = null; throw; }
        finally { gate.Release(); }
    }

    public async Task<ComputerActionResult> ExecuteBatchAsync(ComputerActionBatch batch, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        var completed = 0;
        var dispatched = false;
        Task? inFlight = null;
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            await authorize(ct);
            if (current is null || batch.SessionId != sessionId || batch.SurfaceId != surfaceId
                || batch.ObservationId != current.ObservationId) return Failure("stale-or-missing-observation");
            if (batch.Actions.Count is < 1 or > 64) return Failure("invalid-action-batch");
            foreach (var action in batch.Actions) Validate(action);
            var expectedLayout = layout;
            foreach (var action in batch.Actions)
            {
                ct.ThrowIfCancellationRequested();
                await authorize(ct);
                if (page.IsClosed) return Failure("surface-closed");
                // Layout, scroll, frame and document identity matter; animated pixels do not.
                if (await LayoutAsync(ct) != expectedLayout) return Failure("layout-changed-reobserve");
                dispatched = true;
                inFlight = RunActionAsync(action, current.DeviceScaleFactor, operationCancellation.Token);
                await inFlight.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs), ct);
                completed++;
                dispatched = false;
                await authorize(ct);
                if (await LayoutAsync(ct) != expectedLayout && completed < batch.Actions.Count)
                    return new(false, completed, await CaptureAsync(ct), "layout-changed-reobserve") { BeforeObservationId = batch.ObservationId };
            }
            return new(true, completed, await CaptureAsync(ct)) { BeforeObservationId = batch.ObservationId };
        }
        catch (OperationCanceledException)
        {
            current = null; layout = null;
            await operationCancellation.CancelAsync();
            // Playwright lacks cancellation on input. Close and await it before the host gate
            // can admit another action; an already-dispatched action is never claimed undone.
            if (dispatched && !page.IsClosed) await page.CloseAsync();
            if (inFlight is not null) await SettleAsync(inFlight);
            throw;
        }
        catch (TimeoutException)
        {
            current = null; layout = null;
            await operationCancellation.CancelAsync();
            if (dispatched && !page.IsClosed) await page.CloseAsync();
            if (inFlight is not null) await SettleAsync(inFlight);
            return Failure("browser-action-timeout", dispatched);
        }
        catch (Exception ex) when (ex is PlaywrightException or ArgumentException)
        {
            current = null; layout = null;
            var code = ex is ArgumentException ? "invalid-action"
                : ex.Message.Contains("Execution context", StringComparison.OrdinalIgnoreCase) ? "browser-context-changed"
                : ex.Message.Contains("closed", StringComparison.OrdinalIgnoreCase) ? "surface-closed"
                : ex.Message.Contains("frame", StringComparison.OrdinalIgnoreCase) ? "browser-frame-changed"
                : "browser-action-failed";
            return Failure(code, dispatched);
        }
        finally { gate.Release(); }

        ComputerActionResult Failure(string reason, bool uncertain = false) => new(false, completed, null, reason)
        { BeforeObservationId = batch.ObservationId, OutcomeUncertain = uncertain };
    }

    private static void Validate(ComputerAction action)
    {
        if (action.Type is not ("click" or "double_click" or "move" or "drag" or "scroll" or "keypress" or "type" or "wait" or "screenshot"))
            throw new ArgumentException("Unsupported action.");
        if (action.Type is "click" or "double_click" or "move" or "scroll"
            && (action.X is null || action.Y is null || !double.IsFinite(action.X.Value) || !double.IsFinite(action.Y.Value)))
            throw new ArgumentException("Coordinates are required.");
        if (!double.IsFinite(action.ScrollX) || !double.IsFinite(action.ScrollY)
            || action.Path.Count > 256 || action.Path.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y))
            || action.Keys.Count > 16 || action.Text?.Length > 16000 || action.WaitMilliseconds is < 0 or > 15000)
            throw new ArgumentException("Invalid action parameters.");
    }

    private async Task RunActionAsync(ComputerAction a, double scale, CancellationToken ct)
    {
        float X(double x) => x >= 0 && x < current!.Width ? (float)(x / scale) : throw new ArgumentException("Coordinate outside screenshot.");
        float Y(double y) => y >= 0 && y < current!.Height ? (float)(y / scale) : throw new ArgumentException("Coordinate outside screenshot.");
        var button = a.Button switch { null or "left" => MouseButton.Left, "right" => MouseButton.Right, "wheel" or "middle" => MouseButton.Middle, _ => throw new ArgumentException("Unknown button.") };
        switch (a.Type)
        {
            case "click": await page.Mouse.ClickAsync(X(a.X!.Value), Y(a.Y!.Value), new() { Button = button }); break;
            case "double_click": await page.Mouse.DblClickAsync(X(a.X!.Value), Y(a.Y!.Value), new() { Button = button }); break;
            case "move": await page.Mouse.MoveAsync(X(a.X!.Value), Y(a.Y!.Value)); break;
            case "scroll": await page.Mouse.MoveAsync(X(a.X!.Value), Y(a.Y!.Value)); await page.Mouse.WheelAsync((float)(a.ScrollX / scale), (float)(a.ScrollY / scale)); break;
            case "type": await RejectSecretFocusAsync(ct); ct.ThrowIfCancellationRequested(); await page.Keyboard.InsertTextAsync(a.Text ?? ""); break;
            case "keypress":
                await RejectSecretFocusAsync(ct);
                var keys = a.Keys.Select(Key).ToArray();
                if (keys.Length == 0) throw new ArgumentException("Keys required.");
                ct.ThrowIfCancellationRequested(); await page.Keyboard.PressAsync(string.Join("+", keys)); break;
            case "drag":
                if (a.Path.Count < 2) throw new ArgumentException("Drag path required.");
                await page.Mouse.MoveAsync(X(a.Path[0].X), Y(a.Path[0].Y)); await page.Mouse.DownAsync();
                try { foreach (var point in a.Path.Skip(1)) { ct.ThrowIfCancellationRequested(); await page.Mouse.MoveAsync(X(point.X), Y(point.Y)); } }
                finally { await page.Mouse.UpAsync(); } break;
            case "wait": await Task.Delay(a.WaitMilliseconds, ct); break;
            case "screenshot": break;
        }
    }
    private static string Key(string key) => key.ToUpperInvariant() switch
    {
        "CTRL" or "CONTROL" => "Control", "CMD" or "META" or "SUPER" => "Meta", "ALT" => "Alt", "SHIFT" => "Shift",
        "ENTER" or "RETURN" => "Enter", "ESC" or "ESCAPE" => "Escape", "TAB" => "Tab", "SPACE" => "Space",
        "BACKSPACE" => "Backspace", "DELETE" => "Delete", "UP" => "ArrowUp", "DOWN" => "ArrowDown", "LEFT" => "ArrowLeft", "RIGHT" => "ArrowRight",
        "HOME" => "Home", "END" => "End", "PAGEUP" => "PageUp", "PAGEDOWN" => "PageDown",
        _ when key.Length == 1 => key, _ => throw new ArgumentException("Unsupported key.")
    };
    private async Task RejectSecretFocusAsync(CancellationToken ct)
    {
        foreach (var frame in page.Frames)
            if (await BoundedReadAsync(page, frame.EvaluateAsync<bool>("() => {const e=document.activeElement; return document.hasFocus() && !!e?.matches('input[type=password],input[autocomplete*=password],input[autocomplete=username],input:-webkit-autofill,input[autocomplete=one-time-code],input[autocomplete^=cc-]');}"), TimeSpan.FromMilliseconds(TimeoutMs), ct))
                throw new ArgumentException("Credential input remains browser-owned.");
    }

    private async Task<ComputerObservation> CaptureAsync(CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await CaptureCoreAsync(ct); }
            catch (Exception ex) when (attempt < 2 && IsTransientCaptureChange(ex))
            {
                await BoundedReadAsync(page, WaitForDocumentAsync(page), TimeSpan.FromMilliseconds(TimeoutMs), ct);
            }
        }
    }
    private static bool IsTransientCaptureChange(Exception ex) =>
        ex is PlaywrightException
            && (ex.Message.Contains("Execution context", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("frame", StringComparison.OrdinalIgnoreCase))
        || ex is InvalidOperationException && ex.Message == "layout-changed-during-capture";
    private async Task<ComputerObservation> CaptureCoreAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await authorize(ct);
        if (page.IsClosed) throw new InvalidOperationException("surface-closed");
        var before = await LayoutAsync(ct);
        var timeout = TimeSpan.FromMilliseconds(TimeoutMs);
        var scale = await BoundedReadAsync(page, page.EvaluateAsync<double>("() => devicePixelRatio"), timeout, ct);
        var png = await BoundedReadAsync(page, page.ScreenshotAsync(new() { Type = ScreenshotType.Png, Scale = ScreenshotScale.Device,
            Timeout = TimeoutMs, Mask = page.Frames.Select(frame => frame.Locator("input[type=password],input[autocomplete*=password],input[autocomplete=username],input:-webkit-autofill,input[autocomplete=one-time-code],input[autocomplete^=cc-],[data-private]")).ToArray() }), timeout, ct);
        await authorize(ct);
        var after = await LayoutAsync(ct);
        if (before != after) throw new InvalidOperationException("layout-changed-during-capture");
        layout = after;
        current = new(sessionId, surfaceId, Guid.NewGuid().ToString("N"), SafeUrl(page.Url), DateTimeOffset.UtcNow,
            BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)), scale)
        { PngBase64 = Convert.ToBase64String(png) };
        return current;
    }
    public static string SafeUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        ? new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty, Query = string.Empty, Fragment = string.Empty }.Uri.GetLeftPart(UriPartial.Path) : "about:blank";
    public static async Task RejectCredentialTargetAsync(IPage page, ILocator target, TimeSpan timeout, CancellationToken ct)
    {
        if (await RunBoundedAsync(page, target.EvaluateAsync<bool>("e => e.matches('input[type=password],input[autocomplete*=password],input[autocomplete=username],input:-webkit-autofill,input[autocomplete=one-time-code],input[autocomplete^=cc-]')"), timeout, ct))
            throw new ArgumentException("Credential input remains browser-owned.");
    }
    private Task<string> LayoutAsync(CancellationToken ct = default) => CaptureLayoutFingerprintAsync(page, ct, TimeSpan.FromMilliseconds(TimeoutMs));
    public static Task<string> CaptureLayoutFingerprintAsync(IPage page, CancellationToken ct = default, TimeSpan? timeout = null)
        => BoundedReadAsync(page, CaptureNavigatingLayoutAsync(page), timeout ?? TimeSpan.FromSeconds(15), ct);
    private static async Task<string> CaptureNavigatingLayoutAsync(IPage page)
    {
        try { return await CaptureLayoutCoreAsync(page); }
        catch (PlaywrightException ex) when (ex.Message.Contains("Execution context was destroyed", StringComparison.OrdinalIgnoreCase))
        {
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            return await CaptureLayoutCoreAsync(page);
        }
    }
    private static async Task<string> CaptureLayoutCoreAsync(IPage page)
    {
        const string script = """
            () => {
              const k='__swarmComputerDocumentIdentity';
              if(!window[k]) Object.defineProperty(window,k,{value:{id:Date.now()+':'+Math.random(),nodes:new WeakMap(),next:0}});
              const state=window[k], nodes=[...document.querySelectorAll('body,body *')].filter(e=>{const r=e.getBoundingClientRect();return r.width>0&&r.height>0&&r.bottom>0&&r.right>0&&r.top<innerHeight&&r.left<innerWidth});
              if(nodes.length>4096) throw new Error('surface-too-complex');
              return JSON.stringify([state.id,location.href,innerWidth,innerHeight,devicePixelRatio,scrollX,scrollY,
                nodes.map(e=>{if(!state.nodes.has(e))state.nodes.set(e,++state.next);const r=e.getBoundingClientRect(),s=getComputedStyle(e);
                  const label=e.matches('button,a,[role],[contenteditable]')?(e.innerText||'').slice(0,160):'';
                  return [state.nodes.get(e),e.tagName,e.id,r.x,r.y,r.width,r.height,e.hidden,e.disabled,s.visibility,s.pointerEvents,s.zIndex,s.opacity,label,e.getAttribute('aria-label'),e.getAttribute('title')];})]);
            }
            """;
        if (page.Frames.Count > 64) throw new InvalidOperationException("surface-frame-limit");
        var states = new List<string>();
        foreach (var frame in page.Frames) states.Add(await frame.EvaluateAsync<string>(script));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", states))));
    }
    private static async Task<T> BoundedReadAsync<T>(IPage page, Task<T> operation, TimeSpan timeout, CancellationToken ct)
    {
        try { return await operation.WaitAsync(timeout, ct); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            if (!page.IsClosed) await page.CloseAsync();
            await SettleAsync(operation);
            throw;
        }
    }
    public static Task<T> RunBoundedAsync<T>(IPage page, Task<T> operation, TimeSpan timeout, CancellationToken ct)
        => BoundedReadAsync(page, operation, timeout, ct);
    public static async Task RunBoundedAsync(IPage page, Task operation, TimeSpan timeout, CancellationToken ct)
    {
        try { await operation.WaitAsync(timeout, ct); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            if (!page.IsClosed) await page.CloseAsync();
            await SettleAsync(operation);
            throw;
        }
    }
    private static async Task<bool> WaitForDocumentAsync(IPage page)
    { await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded); return true; }
    private static async Task SettleAsync(Task operation)
    { try { await operation; } catch { /* Preserve the original cancellation/timeout after the operation terminates. */ } }
}
