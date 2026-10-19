using Microsoft.Extensions.Options;
using SwarmCore.Abstractions.Computer;
using SwarmCore.Coding.Computer;

namespace SwarmCore.Coding.Tests;

public sealed class MacComputerSessionTests
{
    private sealed class Native : IMacDesktopDriver
    {
        public MacDesktopWindow Window { get; set; } = new("42", "Synthetic editor", 7, 123, -800, 20, 640, 480);
        public bool Focused { get; set; } = true;
        public bool Changed { get; set; }
        public int Inputs { get; private set; }
        public Action? AfterInput { get; set; }
        public Task<IReadOnlyList<MacDesktopWindow>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<MacDesktopWindow>>([Window]);
        public Task ActivateAsync(MacDesktopWindow w, CancellationToken ct) { Focused = true; return Task.CompletedTask; }
        public Task<byte[]> CaptureAsync(MacDesktopWindow w, CancellationToken ct) => Task.FromResult(new byte[] { 1, 2, 3 });
        public Task<bool> IsCurrentAsync(MacDesktopWindow w, bool focus, CancellationToken ct) => Task.FromResult(!Changed && (!focus || Focused) && w == Window);
        public Task ExecuteAsync(MacDesktopWindow w, ComputerAction a, CancellationToken ct)
        { Inputs++; AfterInput?.Invoke(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    }
    private static MacComputerSession Session(Native native, bool enabled = true) => new(Options.Create(new CodingComputerOptions { Enabled = enabled }), native);
    private static ComputerActionBatch Batch(ComputerObservation o, params ComputerAction[] actions) => new(o.SessionId, o.SurfaceId, o.ObservationId, actions);

    [Fact]
    public async Task Disabled_tools_never_touch_the_native_driver()
    {
        var native = new Native();
        await Assert.ThrowsAsync<ComputerOperationException>(() => Session(native, false).ObserveAsync("42"));
        Assert.Equal(0, native.Inputs);
    }

    [Fact]
    public async Task Observation_has_window_identity_dimensions_and_untrusted_pixels()
    {
        var native = new Native(); var session = Session(native);
        Assert.Equal("42", Assert.Single(await session.ListAsync()).Id);
        var o = await session.ObserveAsync("42");
        Assert.Equal((640, 480, 1d), (o.Width, o.Height, o.DeviceScaleFactor));
        Assert.Equal("untrusted-desktop-evidence", o.Trust);
        Assert.Equal("AQID", o.PngBase64);
        await Assert.ThrowsAsync<ComputerOperationException>(() => session.ObserveAsync("other"));
    }

    [Fact]
    public async Task Whole_batch_validates_before_input_and_consumes_its_observation()
    {
        var native = new Native(); var session = Session(native); var o = await session.ObserveAsync("42");
        var result = await session.ExecuteBatchAsync(Batch(o, new ComputerAction("type") { Text = "safe" }, new ComputerAction("click") { X = 640, Y = 0 }));
        Assert.False(result.Succeeded); Assert.False(result.OutcomeUncertain); Assert.Equal(0, native.Inputs);
        Assert.False((await session.ExecuteBatchAsync(Batch(o, new ComputerAction("type") { Text = "retry" }))).Succeeded);
    }

    [Fact]
    public async Task Foreign_and_stale_observations_cannot_send_input()
    {
        var native = new Native(); var session = Session(native); var o = await session.ObserveAsync("42");
        Assert.False((await session.ExecuteBatchAsync(Batch(o with { SessionId = "foreign" }, new ComputerAction("screenshot")))).Succeeded);
        var fresh = await session.ObserveAsync("42");
        Assert.False((await session.ExecuteBatchAsync(Batch(o, new ComputerAction("screenshot")))).Succeeded);
        Assert.True((await session.ExecuteBatchAsync(Batch(fresh, new ComputerAction("screenshot")))).Succeeded);
    }

    [Theory]
    [InlineData("focus")]
    [InlineData("bounds")]
    [InlineData("process")]
    public async Task Changed_target_refuses_input(string change)
    {
        var native = new Native(); var session = Session(native); var o = await session.ObserveAsync("42");
        if (change == "focus") native.Focused = false;
        else if (change == "bounds") native.Window = native.Window with { X = 30 };
        else native.Window = native.Window with { ProcessStartTicks = 999 };
        var result = await session.ExecuteBatchAsync(Batch(o, new ComputerAction("keypress") { Keys = ["CMD", "A"] }));
        Assert.False(result.Succeeded); Assert.False(result.OutcomeUncertain); Assert.Equal(0, native.Inputs);
    }

    [Fact]
    public async Task Focus_loss_mid_batch_is_uncertain_and_never_replayed()
    {
        var native = new Native(); var session = Session(native); var o = await session.ObserveAsync("42");
        native.AfterInput = () => native.Focused = false;
        var result = await session.ExecuteBatchAsync(Batch(o, new ComputerAction("type") { Text = "first" }, new ComputerAction("type") { Text = "second" }));
        Assert.False(result.Succeeded); Assert.True(result.OutcomeUncertain); Assert.Equal(1, result.CompletedActions);
        Assert.False((await session.ExecuteBatchAsync(Batch(o, new ComputerAction("screenshot")))).Succeeded);
        Assert.Equal(1, native.Inputs);
    }

    [Fact]
    public async Task Cancellation_consumes_the_observation()
    {
        var native = new Native(); var session = Session(native); var o = await session.ObserveAsync("42");
        using var cancel = new CancellationTokenSource();
        native.AfterInput = cancel.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ExecuteBatchAsync(Batch(o, new ComputerAction("keypress") { Keys = ["CMD", "A"] }), cancel.Token));
        Assert.False((await session.ExecuteBatchAsync(Batch(o, new ComputerAction("screenshot")))).Succeeded);
    }

    [Fact]
    public async Task Success_refreshes_pixels_and_invalidates_the_old_observation()
    {
        var native = new Native(); var session = Session(native); var o = await session.ObserveAsync("42");
        var result = await session.ExecuteBatchAsync(Batch(o, new ComputerAction("keypress") { Keys = ["CMD", "A"] }, new ComputerAction("type") { Text = "emoji 😀" }));
        Assert.True(result.Succeeded); Assert.Equal(2, result.CompletedActions);
        Assert.NotEqual(o.ObservationId, result.Observation!.ObservationId);
        Assert.False((await session.ExecuteBatchAsync(Batch(o, new ComputerAction("screenshot")))).Succeeded);
    }

    [Fact]
    public void Mac_shortcuts_and_geometry_validate_before_dispatch()
    {
        MacComputerSession.ValidateAction(new ComputerAction("keypress") { Keys = ["COMMAND", "SHIFT", "A"] }, 100, 100);
        MacComputerSession.ValidateAction(new ComputerAction("click") { X = 99, Y = 99 }, 100, 100);
        Assert.Throws<ArgumentException>(() => MacComputerSession.ValidateAction(new ComputerAction("keypress") { Keys = ["UNSUPPORTED"] }, 100, 100));
        Assert.Throws<ArgumentException>(() => MacComputerSession.ValidateAction(new ComputerAction("keypress"), 100, 100));
        Assert.Throws<ArgumentException>(() => MacComputerSession.ValidateAction(new ComputerAction("click") { X = double.NaN, Y = 0 }, 100, 100));
    }
}
