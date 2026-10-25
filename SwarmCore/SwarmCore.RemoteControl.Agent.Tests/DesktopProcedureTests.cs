using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.Procedures;
using SwarmCore.Agent.Runtime;
using SwarmCore.Agent.ToolUseEngine;

namespace SwarmCore.RemoteControl.Agent.Tests;

/// <summary>
/// The desktop learned-procedure cycle over an in-memory desktop (nothing touches the real mouse or keyboard).
/// The shopping site is only data: a key, inputs and recorded steps.
/// </summary>
public sealed class DesktopProcedureTests : IDisposable
{
    private const long Chrome = 100;
    private const long SignIn = 200;
    private const string Done = "wait until a text called \"Added to cart\" appears in chrome";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "desktop-procedure-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static ShopDesktop Shop(string addButton = "Add to cart")
    {
        var d = new ShopDesktop();
        d.AddWindow(Chrome, "chrome", "Shop - Google Chrome", foreground: true);
        d.Add(Chrome, "edit", "Address and search bar", "www.walmart.com/");
        d.Add(Chrome, "edit", "Search Walmart");
        d.Add(Chrome, "button", "Search");
        d.Add(Chrome, "text", "Cart", "0 items");
        d.OnActivate = (w, c) =>
        {
            if (w != Chrome) return;
            if (c.Name == "Search")
            {
                d.Add(w, "hyperlink", d.Find(w, "Search Walmart")!.Value ?? "");
                d.Add(w, "button", addButton);
            }
            else if (c.Name == addButton)
            {
                d.SetValue(w, "Cart", "1 item");
                d.Add(w, "text", "Added to cart");
            }
        };
        return d;
    }

    private (DesktopProcedureRunner Runner, DesktopControlTools Tools) Runner(IDesktopAutomation desktop, IDesktopSecretSlots? slots = null)
    {
        var session = new RemoteControlSession(desktop, new RemoteControlSessionOptions
        {
            CaptureDirectory = Path.Combine(_dir, "captures"), UserIdleRequired = TimeSpan.Zero,
        });
        var tools = new DesktopControlTools(session, slots);
        return (new DesktopProcedureRunner(tools), tools);
    }

    private static DesktopProcedureTask Task(string item, string site = "walmart.com", string task = "Add the bread I usually buy to my cart") => new()
    {
        Key = DesktopProcedureKeys.Key("profile", "desktop-agent", site, task),
        BindingFingerprint = DesktopProcedureKeys.BindingFingerprint(site, task, new JsonObject { ["item"] = item }),
        Inputs = new JsonObject { ["item"] = item },
        ReadinessTimeout = TimeSpan.FromMilliseconds(300),
        VerifyTimeout = TimeSpan.FromMilliseconds(500),
        RecheckTimeout = TimeSpan.FromMilliseconds(300),
    };

    private static DesktopRecordedCall Call(DesktopControlTools tools, string toolId, JsonObject args)
    {
        var descriptor = tools.Descriptors.Single(d => d.ToolId == toolId);
        var input = args.Deserialize(descriptor.InputType, DesktopControlTools.Json)!;
        var result = (DesktopToolResult)descriptor.Invoke(input, CancellationToken.None).GetAwaiter().GetResult()!;
        Assert.True(result.Ok, $"{toolId} failed: {result.Error ?? result.Text}");
        return new(toolId, args, result);
    }

    private static List<DesktopRecordedCall> AddToCart(DesktopControlTools tools, string item, string addButton = "Add to cart") =>
    [
        Call(tools, DesktopControlTools.ActTool, new() { ["control"] = "Search Walmart", ["action"] = "set", ["window"] = "chrome", ["value"] = item, ["controlType"] = "edit" }),
        Call(tools, DesktopControlTools.ActTool, new() { ["control"] = "Search", ["action"] = "click", ["window"] = "chrome", ["controlType"] = "button" }),
        Call(tools, DesktopControlTools.ActTool, new() { ["control"] = addButton, ["action"] = "click", ["window"] = "chrome", ["controlType"] = "button" }),
        Call(tools, DesktopControlTools.WaitForTool, new() { ["text"] = Done }),
    ];

    private JsonFileAgentProcedureStore Store() => new(Path.Combine(_dir, "procedures"));

    [Fact]
    public void TheSuccessConditionParsesAsAWait()
    {
        Assert.Equal(CommandKind.WaitFor, RemoteCommandParser.Parse(Done).Kind);
    }

    [Fact]
    public async Task ARecordedRunCompilesTheItemNameIntoABindingNotALiteral()
    {
        var desktop = Shop();
        var (runner, tools) = Runner(desktop);
        var store = Store();
        var task = Task("Great Value Bread");

        var saved = await runner.SaveAsync(task, store, AddToCart(tools, "Great Value Bread"));

        Assert.Equal(DesktopProcedureStatus.Saved, saved.Status);
        var history = (await store.GetAsync(task.Key))!;
        var active = history.Revisions.Single(r => r.Id == history.ActiveRevisionId);
        Assert.Equal(4, active.Steps.Count);
        Assert.Equal(DesktopProcedureRunner.VerifyTool, active.Steps[^1].ToolId);
        var set = active.Steps[0];
        var binding = Assert.Single(set.Bindings);
        Assert.Equal("/value", binding.TargetPointer);
        Assert.Equal(AgentProcedureValueSource.Input, binding.Source);
        Assert.Equal("/item", binding.SourcePointer);
        Assert.Null(set.Arguments["value"]);
        Assert.Equal("Search Walmart", set.Arguments["control"]!.GetValue<string>());
        var json = File.ReadAllText(store.PathFor(task.Key));
        Assert.DoesNotContain("Great Value Bread", json);
        Assert.Equal("desktop/walmart.com/add the bread i usually buy to my cart", task.Key.TaskId);
    }

    [Fact]
    public async Task ReplayRunsWithZeroModelCallsAndIsVerified()
    {
        var store = Store();
        var (learner, learnTools) = Runner(Shop());
        Assert.Equal(DesktopProcedureStatus.Saved,
            (await learner.SaveAsync(Task("Great Value Bread"), store, AddToCart(learnTools, "Great Value Bread"))).Status);

        // A different item on a fresh desktop, through an engine session whose model must never be called.
        var desktop = Shop();
        var (runner, _) = Runner(desktop);
        var (router, calls) = Scripted();
        var outcome = await runner.RunWithModelAsync(router, Task("Wonder Bread"), store, "Add Wonder Bread to my cart.");

        Assert.Equal(DesktopProcedureStatus.Completed, outcome.Status);
        Assert.Equal(0, calls());
        Assert.Contains(desktop.Actions, a => a.Action == ControlAction.SetValue && a.Value == "Wonder Bread");
        Assert.Equal("1 item", desktop.Find(Chrome, "Cart")!.Value);

        // The direct replay path (no session at all) also completes and reports the verified revision.
        var again = Shop();
        var (direct, _) = Runner(again);
        var replay = await direct.ReplayAsync(Task("Rye Bread"), store);
        Assert.Equal(DesktopProcedureStatus.Completed, replay.Status);
        Assert.Equal(4, replay.StepsRun);
        Assert.Contains(again.Actions, a => a.Value == "Rye Bread");
        var history = (await store.GetAsync(Task("Rye Bread").Key))!;
        Assert.Equal(replay.RevisionId, history.ActiveRevisionId);
    }

    [Fact]
    public async Task AChangedPageStopsReplayAtTheStepWithAFreshObservation()
    {
        var store = Store();
        var (learner, learnTools) = Runner(Shop());
        await learner.SaveAsync(Task("Great Value Bread"), store, AddToCart(learnTools, "Great Value Bread"));

        var changed = Shop(addButton: "Add to basket");
        var (runner, _) = Runner(changed);
        var outcome = await runner.ReplayAsync(Task("Great Value Bread"), store);

        Assert.Equal(DesktopProcedureStatus.Stopped, outcome.Status);
        var stop = outcome.Stop!;
        Assert.Equal(2, stop.StepIndex);
        Assert.Equal("precondition_failed", stop.Reason);
        Assert.Equal(FailureCategories.NotFound, stop.FailureCategory);
        Assert.Equal("Add to cart", stop.Step!.Arguments["control"]!.GetValue<string>());
        Assert.Contains(stop.Observation.Windows, w => w.Process == "chrome");
        Assert.Contains(stop.Observation.Controls, c => c.Name == "Add to basket");
        Assert.DoesNotContain(changed.Actions, a => a.Key == changed.Find(Chrome, "Add to basket")!.RuntimeKey);
    }

    [Fact]
    public async Task ARepairedRevisionSupersedesTheOldOneWhichStaysInHistoryForRollback()
    {
        var store = Store();
        var task = Task("Great Value Bread");
        var (learner, learnTools) = Runner(Shop());
        var first = await learner.SaveAsync(task, store, AddToCart(learnTools, "Great Value Bread"));

        // The site renames the button: replay stops, the agent repairs by doing the corrected steps, then saves.
        var changed = Shop(addButton: "Add to basket");
        var (runner, _) = Runner(changed);
        Assert.Equal(DesktopProcedureStatus.Stopped, (await runner.ReplayAsync(task, store)).Status);
        // The agent repeats the corrected sequence on the current page and saves it.
        var fresh = Shop(addButton: "Add to basket");
        var (freshRunner, freshTools) = Runner(fresh);
        var repaired = await freshRunner.SaveAsync(task, store, AddToCart(freshTools, "Great Value Bread", "Add to basket"));
        Assert.Equal(DesktopProcedureStatus.Saved, repaired.Status);

        var history = (await store.GetAsync(task.Key))!;
        Assert.Equal(repaired.RevisionId, history.ActiveRevisionId);
        var old = history.Revisions.Single(r => r.Id == first.RevisionId);
        Assert.NotEqual(AgentProcedureRevisionState.Active, old.State);
        Assert.NotNull(old.Verification);

        // A later repair while a revision is active supersedes it (not rejected, not deleted).
        var third = Shop(addButton: "Add to basket");
        var (thirdRunner, thirdTools) = Runner(third);
        var again = await thirdRunner.SaveAsync(task, store, AddToCart(thirdTools, "Great Value Bread", "Add to basket"));
        history = (await store.GetAsync(task.Key))!;
        Assert.Equal(AgentProcedureRevisionState.Superseded, history.Revisions.Single(r => r.Id == repaired.RevisionId).State);
        Assert.Equal(again.RevisionId, history.ActiveRevisionId);

        // Rollback to the original revision is a host CAS over the same history.
        var toolFingerprint = AgentProcedureRuntime.FingerprintTools(thirdRunner.Descriptors(task));
        Assert.True(await AgentProcedureRuntime.RollbackAsync(store, task.Key, history.Version, first.RevisionId!.Value,
            task.BindingFingerprint, toolFingerprint));
        Assert.Equal(first.RevisionId, (await store.GetAsync(task.Key))!.ActiveRevisionId);
    }

    [Fact]
    public async Task ASecretTypedThroughASlotNeverAppearsInStoredStepsOrJson()
    {
        const string secret = "Hunter2-very-secret";
        var desktop = new ShopDesktop();
        desktop.AddWindow(SignIn, "accountapp", "Sign in", foreground: true);
        desktop.Add(SignIn, "edit", "Email");
        desktop.Add(SignIn, "edit", "Password", password: true);
        desktop.Add(SignIn, "button", "Sign in");
        desktop.OnActivate = (w, c) => { if (c.Name == "Sign in") desktop.Add(w, "text", "Welcome back"); };
        var slots = new DesktopSecretSlots(new Dictionary<string, string> { ["password"] = secret });
        var (runner, tools) = Runner(desktop, slots);
        var inputs = new JsonObject { ["email"] = "person@example.test" };
        var task = new DesktopProcedureTask
        {
            Key = DesktopProcedureKeys.Key("profile", "desktop-agent", "accountapp", "sign in"),
            BindingFingerprint = DesktopProcedureKeys.BindingFingerprint("accountapp", "sign in", inputs, slots.SlotNames),
            Inputs = inputs, ReadinessTimeout = TimeSpan.FromMilliseconds(300), VerifyTimeout = TimeSpan.FromMilliseconds(500),
        };
        var store = Store();
        var calls = new List<DesktopRecordedCall>
        {
            Call(tools, DesktopControlTools.ActTool, new() { ["control"] = "Email", ["action"] = "set", ["window"] = "accountapp", ["value"] = "person@example.test", ["controlType"] = "edit" }),
            Call(tools, DesktopControlTools.ActTool, new() { ["control"] = "Password", ["action"] = "set", ["window"] = "accountapp", ["secret"] = "password", ["controlType"] = "edit" }),
            Call(tools, DesktopControlTools.ActTool, new() { ["control"] = "Sign in", ["action"] = "click", ["window"] = "accountapp", ["controlType"] = "button" }),
        };
        Assert.Contains(desktop.Actions, a => a.Value == secret);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(calls[1].Result, DesktopControlTools.Json));

        var saved = await runner.SaveAsync(task, store, calls, "wait until a text called \"Welcome back\" appears in accountapp");
        Assert.Equal(DesktopProcedureStatus.Saved, saved.Status);
        var json = File.ReadAllText(store.PathFor(task.Key));
        Assert.DoesNotContain(secret, json);
        var step = (await store.GetAsync(task.Key))!.Revisions.Single(r => r.Id == saved.RevisionId).Steps[1];
        Assert.Equal("password", step.Arguments["secret"]!.GetValue<string>());
        Assert.Null(step.Arguments["value"]);

        // Typing the password as a plain value is refused: it would become a stored literal.
        var leaky = new ShopDesktop();
        leaky.AddWindow(SignIn, "accountapp", "Sign in", foreground: true);
        leaky.Add(SignIn, "edit", "Password", password: true);
        leaky.Add(SignIn, "text", "Welcome back");
        var (leakyRunner, leakyTools) = Runner(leaky, slots);
        var refused = await leakyRunner.SaveAsync(task with { Key = task.Key with { TaskId = task.Key.TaskId + "-2" } }, store,
            [Call(leakyTools, DesktopControlTools.ActTool, new() { ["control"] = "Password", ["action"] = "set", ["window"] = "accountapp", ["value"] = secret, ["controlType"] = "edit" })],
            "wait until a text called \"Welcome back\" appears in accountapp");
        Assert.Equal(DesktopProcedureStatus.Refused, refused.Status);
        Assert.DoesNotContain(secret, string.Join("\n", Directory.GetFiles(store.Directory).Select(File.ReadAllText)));
    }

    [Fact]
    public async Task TheFileStoreRejectsAStaleVersion()
    {
        var store = Store();
        var key = new AgentProcedureKey("profile", "agent", "desktop/notepad/save");
        var v1 = new AgentProcedureHistory(key, 1, null, []);
        Assert.True(await store.CompareExchangeAsync(key, 0, v1));
        Assert.False(await store.CompareExchangeAsync(key, 0, v1));
        Assert.True(await store.CompareExchangeAsync(key, 1, v1 with { Version = 2 }));
        Assert.False(await store.CompareExchangeAsync(key, 1, v1 with { Version = 2 }));
        Assert.Equal(2, (await store.GetAsync(key))!.Version);
        Assert.Single(await store.ListAsync("profile", "agent"));
        Assert.Empty(Directory.GetFiles(store.Directory, "*.tmp"));
    }

    [Fact]
    public async Task AModelDrivenEngineRunIsLearnedThenReplayedWithoutTheModel()
    {
        var store = Store();
        var task = Task("Great Value Bread");
        var learnDesktop = Shop();
        var (learner, _) = Runner(learnDesktop);
        var (router, calls) = Scripted(
            ToolCall(DesktopControlTools.ActTool, new() { ["control"] = "Search Walmart", ["action"] = "set", ["window"] = "chrome", ["value"] = "Great Value Bread", ["controlType"] = "edit" }),
            ToolCall(DesktopControlTools.ActTool, new() { ["control"] = "Search", ["action"] = "click", ["window"] = "chrome", ["controlType"] = "button" }),
            ToolCall(DesktopControlTools.ActTool, new() { ["control"] = "Add to cart", ["action"] = "click", ["window"] = "chrome", ["controlType"] = "button" }),
            ToolCall(DesktopProcedureRunner.VerifyTool, new() { ["condition"] = Done }),
            new() { Provider = "fixture", Model = "fixture", StopReason = ChatStopReason.EndTurn, Text = "Added.", ToolCalls = [] });
        var learned = await learner.RunWithModelAsync(router, task, store, "Add Great Value Bread to my cart.");

        Assert.Equal(DesktopProcedureStatus.Completed, learned.Status);
        Assert.True(calls() > 0);
        var history = (await store.GetAsync(task.Key))!;
        var active = history.Revisions.Single(r => r.Id == history.ActiveRevisionId);
        Assert.Equal("/item", Assert.Single(active.Steps[0].Bindings).SourcePointer);

        var desktop = Shop();
        var (runner, _) = Runner(desktop);
        var replay = await runner.ReplayAsync(Task("Wonder Bread"), store);
        Assert.Equal(DesktopProcedureStatus.Completed, replay.Status);
        Assert.Contains(desktop.Actions, a => a.Value == "Wonder Bread");
    }

    [Fact]
    public void KeysUseTheRegistrableDomainOfABrowserWindow()
    {
        var window = new WindowInfo(1, "Bread - Google Chrome", "chrome", 1, new ScreenRect(0, 0, 10, 10), false, true, 1);
        var address = new ControlInfo("a", "edit", "Address and search bar", null, null, "https://www.walmart.com/ip/123", true, false, false,
            new ScreenRect(0, 0, 10, 10), 1, []);
        Assert.Equal("walmart.com", DesktopProcedureKeys.SiteOf(window, [address]));
        Assert.Equal("notepad", DesktopProcedureKeys.SiteOf(window with { ProcessName = "notepad.exe" }, [address]));
        Assert.NotEqual(DesktopProcedureKeys.BindingFingerprint("walmart.com", "t", new JsonObject { ["item"] = "x" }),
            DesktopProcedureKeys.BindingFingerprint("walmart.com", "t", new JsonObject { ["item"] = "x", ["quantity"] = "2" }));
    }

    private static ChatToolUseResponse ToolCall(string name, JsonObject input) => new()
    {
        Provider = "fixture", Model = "fixture", StopReason = ChatStopReason.ToolUse,
        ToolCalls = [new(Guid.NewGuid().ToString(), name, input)],
    };

    private static (IDurableAgentSessionRunner Runner, Func<int> Calls) Scripted(params ChatToolUseResponse[] script)
    {
        var responses = new Queue<ChatToolUseResponse>(script);
        var calls = 0;
        async IAsyncEnumerable<ToolUseStreamEvent> Stream()
        {
            calls++;
            var response = responses.Dequeue();
            foreach (var tool in response.ToolCalls)
            {
                yield return new ToolUseStartedEvent(tool.Id, tool.Name);
                yield return new ToolUseFinishedEvent(tool);
            }
            if (response.Text is { } text) yield return new TextDeltaEvent(text);
            yield return new MessageStoppedEvent(response);
            await System.Threading.Tasks.Task.CompletedTask;
        }
        var router = new Mock<ILlmRouter>(MockBehavior.Strict);
        router.Setup(r => r.StreamChatWithToolsAsync(It.IsAny<string>(), It.IsAny<ChatToolUseRequest>(), It.IsAny<CancellationToken>()))
            .Returns(() => Stream());
        var runner = new DurableAgentSessionRunner(new ToolUseAgentEngine(router.Object, NullLogger<ToolUseAgentEngine>.Instance),
            new TurnEventMapper(), new InMemoryAgentThreadEventStore());
        return (runner, () => calls);
    }
}
