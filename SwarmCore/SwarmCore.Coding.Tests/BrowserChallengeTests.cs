using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SwarmCore.Abstractions.Computer;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Coding.Computer;

namespace SwarmCore.Coding.Tests;

public sealed class BrowserChallengeTests
{
    private static readonly BrowserChallengeInput Input = new("tab", new(10, 10, 80, 80));
    private static ComputerAction Click => new("click") { X = 30, Y = 30 };
    private static BrowserObservation Shot(int n) => new(new("session", "surface", $"o{n}",
        "https://fixture.example/challenge", DateTimeOffset.UtcNow, 100, 100, 1)
        { PngBase64 = Convert.ToBase64String(BitConverter.GetBytes(n)) }, "fixture");

    private sealed class Planner(Func<BrowserObservation, int, CancellationToken, Task<BrowserChallengeDecision>> next) : IBrowserChallengePlanner
    {
        public int Calls { get; private set; }
        public Task<BrowserChallengeDecision> NextAsync(BrowserObservation observation, BrowserChallengeRegion region, int actions, CancellationToken ct)
        { Calls++; return next(observation, actions, ct); }
    }

    private sealed class Computer : ICodingComputer
    {
        public Func<int, BrowserObservation> Observe { get; set; } = Shot;
        public Func<ComputerActionBatch, CancellationToken, Task<ComputerActionResult>> Act { get; set; }
            = (_, _) => Task.FromResult(new ComputerActionResult(true, 1, null));
        public List<ComputerActionBatch> Batches { get; } = [];
        public int Observations { get; private set; }
        public Task<BrowserObservation> BrowserObserveAsync(string id, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(Observe(Observations++)); }
        public Task<ComputerActionResult> BrowserActAsync(ComputerActionBatch batch, CancellationToken ct)
        { Batches.Add(batch); return Act(batch, ct); }
        public Task<IReadOnlyList<BrowserTabInfo>> BrowserTabsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<BrowserTabInfo> BrowserOpenAsync(string url, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<DesktopWindowInfo>> WindowsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<ComputerObservation> DesktopObserveAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task<ComputerActionResult> DesktopActAsync(ComputerActionBatch batch, CancellationToken ct) => throw new NotSupportedException();
    }

    private static BrowserChallengeSolver Solver(Computer computer, IBrowserChallengePlanner planner, int limit = 12, int seconds = 90)
        => new(computer, planner, Options.Create(new CodingComputerOptions { ChallengeMaxActions = limit, ChallengeTimeoutSeconds = seconds }));
    private static Planner Always(ComputerAction action) => new((_, _, _) => Task.FromResult(new BrowserChallengeDecision("act", action)));

    [Fact]
    public async Task Replaced_tiles_are_planned_from_fresh_screenshots_and_ids()
    {
        var computer = new Computer();
        var planner = new Planner((shot, count, _) =>
        {
            Assert.Equal($"o{count}", shot.Screenshot.ObservationId);
            return Task.FromResult(count == 2 ? new BrowserChallengeDecision("clear") : new("act", Click));
        });
        var result = await Solver(computer, planner).SolveAsync(Input, default);
        Assert.Equal("appears_clear", result.Status);
        Assert.Contains("Verify the original", result.Message);
        Assert.Equal(2, result.Actions);
        Assert.Equal(new[] { "o0", "o1" }, computer.Batches.Select(b => b.ObservationId));
        Assert.Equal("o2", result.Observation!.Screenshot.ObservationId);
        Assert.Single(((IToolResultImages)result).Images);
    }

    [Fact]
    public async Task Types_an_answer_only_after_a_scoped_click()
    {
        var computer = new Computer();
        var planner = new Planner((_, count, _) => Task.FromResult(count switch
        {
            0 => new BrowserChallengeDecision("act", Click),
            1 => new("act", new("type") { Text = "7X2" }),
            _ => new("clear"),
        }));
        Assert.Equal("appears_clear", (await Solver(computer, planner).SolveAsync(Input, default)).Status);
        Assert.Equal("7X2", computer.Batches[1].Actions[0].Text);
    }

    public static IEnumerable<object[]> InvalidActions()
    {
        yield return [new ComputerAction("click") { X = 90, Y = 30 }];
        yield return [new ComputerAction("click") { X = double.NaN, Y = 30 }];
        yield return [Click with { Button = "right" }];
        yield return [Click with { Url = "https://elsewhere.example" }];
        yield return [Click with { Keys = ["ENTER"] }];
        yield return [new ComputerAction("type") { Text = "unfocused" }];
        yield return [new ComputerAction("keypress") { Keys = ["CTRL", "L"] }];
        yield return [new ComputerAction("drag") { Path = [new(30, 30), new(99, 99)] }];
        yield return [new ComputerAction("wait") { WaitMilliseconds = 2001 }];
    }

    [Theory, MemberData(nameof(InvalidActions))]
    public async Task Invalid_or_unbounded_plans_never_reach_browser_input(ComputerAction action)
    {
        var computer = new Computer();
        Assert.Equal("invalid_plan", (await Solver(computer, Always(action)).SolveAsync(Input, default)).Status);
        Assert.Empty(computer.Batches);
    }

    [Fact]
    public async Task Invalid_rectangle_never_reaches_vision_or_input()
    {
        var computer = new Computer(); var planner = Always(Click);
        var result = await Solver(computer, planner).SolveAsync(Input with { Region = new(10, 10, 100, 100) }, default);
        Assert.Equal("invalid_region", result.Status); Assert.Equal(0, planner.Calls); Assert.Empty(computer.Batches);
    }

    [Theory]
    [InlineData("url")]
    [InlineData("session")]
    [InlineData("surface")]
    [InlineData("dimensions")]
    [InlineData("scale")]
    public async Task Changed_target_stops_before_another_input(string changed)
    {
        var computer = new Computer { Observe = n => n == 0 ? Shot(n) : Shot(n) with
        {
            Screenshot = changed switch
            {
                "url" => Shot(n).Screenshot with { Url = "https://fixture.example/purchase" },
                "session" => Shot(n).Screenshot with { SessionId = "other" },
                "surface" => Shot(n).Screenshot with { SurfaceId = "other" },
                "dimensions" => Shot(n).Screenshot with { Width = 101 },
                _ => Shot(n).Screenshot with { DeviceScaleFactor = 2 },
            },
        } };
        Assert.Equal("page_changed", (await Solver(computer, Always(Click)).SolveAsync(Input, default)).Status);
        Assert.Single(computer.Batches);
    }

    [Fact]
    public async Task Uncertain_input_is_observed_once_and_never_replayed()
    {
        var computer = new Computer { Act = (_, _) => Task.FromResult(new ComputerActionResult(false, 1, null) { OutcomeUncertain = true }) };
        var result = await Solver(computer, Always(Click)).SolveAsync(Input, default);
        Assert.Equal("input_not_confirmed", result.Status); Assert.Single(computer.Batches);
        Assert.Equal(2, computer.Observations); Assert.Equal("o1", result.Observation!.Screenshot.ObservationId);
    }

    [Fact]
    public async Task Action_failure_does_not_expose_exception_or_retry()
    {
        var computer = new Computer { Act = (_, _) => throw new InvalidOperationException("sensitive page content") };
        var result = await Solver(computer, Always(Click)).SolveAsync(Input, default);
        Assert.Equal("outcome_uncertain", result.Status); Assert.Single(computer.Batches);
        Assert.DoesNotContain("sensitive", result.Message);
    }

    [Fact]
    public async Task Unchanged_challenge_stops_within_three_inputs()
    {
        var computer = new Computer { Observe = n => Shot(n) with { Screenshot = Shot(n).Screenshot with { PngBase64 = "same" } } };
        var result = await Solver(computer, Always(Click)).SolveAsync(Input, default);
        Assert.Equal("no_progress", result.Status); Assert.Equal(3, computer.Batches.Count);
    }

    [Fact]
    public async Task Changing_challenge_still_has_an_action_limit()
    {
        var computer = new Computer();
        var result = await Solver(computer, Always(Click), limit: 2).SolveAsync(Input, default);
        Assert.Equal("action_limit", result.Status); Assert.Equal(2, computer.Batches.Count);
        Assert.Equal("o2", result.Observation!.Screenshot.ObservationId);
    }

    [Fact]
    public async Task Unconfigured_vision_returns_the_observation_for_the_calling_model()
    {
        var computer = new Computer();
        using var services = new ServiceCollection().BuildServiceProvider();
        var result = await Solver(computer, new RoutedBrowserChallengePlanner(services.GetRequiredService<IServiceScopeFactory>())).SolveAsync(Input, default);
        Assert.Equal("vision_unavailable", result.Status); Assert.NotNull(result.Observation); Assert.Empty(computer.Batches);
    }

    [Fact]
    public async Task Noncooperative_vision_is_bounded_without_sending_late_input()
    {
        var pending = new TaskCompletionSource<BrowserChallengeDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var computer = new Computer();
        var result = await Solver(computer, new Planner((_, _, _) => pending.Task), seconds: 1).SolveAsync(Input, default);
        pending.SetResult(new("act", Click));
        Assert.Equal("timeout", result.Status); Assert.Empty(computer.Batches);
    }

    [Fact]
    public async Task Caller_cancellation_stops_before_vision_or_input()
    {
        var computer = new Computer(); var planner = Always(Click);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Solver(computer, planner).SolveAsync(Input, cancellation.Token));
        Assert.Equal(0, planner.Calls); Assert.Empty(computer.Batches);
    }

    [Fact]
    public void Tool_is_registered_even_without_an_API_vision_route()
    {
        var services = new ServiceCollection(); services.AddSingleton<ICodingComputer>(new Computer()); services.AddCodingComputer(_ => { });
        using var provider = services.BuildServiceProvider();
        var descriptor = Assert.Single(provider.GetRequiredService<CodingComputerTools>().Descriptors, d => d.ToolId == BrowserChallengeSolver.ToolId);
        Assert.Equal(SwarmCore.Agent.Catalog.ToolSideEffect.ExternalSend, descriptor.SideEffect);
    }
}
