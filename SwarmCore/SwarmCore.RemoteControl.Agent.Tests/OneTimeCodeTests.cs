using System.Text.Json.Nodes;
using SwarmCore.Agent.Procedures;

namespace SwarmCore.RemoteControl.Agent.Tests;

/// <summary>
/// Sign-in one-time codes. With an email choice the procedure records the email button as a literal and the code as the
/// otp:email slot the host fills from its mailbox at replay; with only text/call it records a person boundary that replay
/// stops at deliberately, keeping the revision active.
/// </summary>
public sealed class OneTimeCodeTests : IDisposable
{
    private const long App = 300;
    private const string Welcome = "wait until a text called \"Welcome back\" appears in accountapp";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "desktop-otp-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>A sign-in page that asks where to send a code. <paramref name="offerEmail"/> false offers only text and call.</summary>
    private static ShopDesktop SignIn(bool offerEmail, string? expectedCode = null, bool personEntersCode = false)
    {
        var d = new ShopDesktop();
        d.AddWindow(App, "accountapp", "Sign in", foreground: true);
        d.Add(App, "edit", "Email");
        d.Add(App, "button", "Continue");
        d.OnActivate = (w, c) =>
        {
            switch (c.Name)
            {
                case "Continue":
                    if (offerEmail) d.Add(w, "button", "Email me a code");
                    d.Add(w, "button", "Text me a code");
                    d.Add(w, "button", "Call me");
                    break;
                case "Email me a code":
                case "Text me a code":
                    d.Add(w, "edit", "Code");
                    d.Add(w, "button", "Verify");
                    if (personEntersCode) d.Add(w, "text", "Welcome back");
                    break;
                case "Verify":
                    if (expectedCode is null || d.Find(w, "Code")!.Value == expectedCode) d.Add(w, "text", "Welcome back");
                    break;
            }
        };
        return d;
    }

    private DesktopControlTools Tools(IDesktopAutomation desktop) => new(new RemoteControlSession(desktop, new RemoteControlSessionOptions
    {
        CaptureDirectory = Path.Combine(_dir, "captures"), UserIdleRequired = TimeSpan.Zero,
    }));

    private static DesktopProcedureTask Task(Func<string, CancellationToken, Task<string?>>? mailbox, TimeSpan personTimeout = default)
    {
        var inputs = new JsonObject { ["email"] = "person@example.test" };
        string[] slots = mailbox is null ? [] : [DesktopSlots.EmailCode];
        return new()
        {
            Key = DesktopProcedureKeys.Key("profile", "desktop-agent", "accountapp", "sign in"),
            BindingFingerprint = DesktopProcedureKeys.BindingFingerprint("accountapp", "sign in", inputs, slots),
            Inputs = inputs, SlotNames = slots, ResolveSlotAsync = mailbox, PersonTimeout = personTimeout,
            ReadinessTimeout = TimeSpan.FromMilliseconds(300), VerifyTimeout = TimeSpan.FromMilliseconds(500), RecheckTimeout = TimeSpan.FromMilliseconds(300),
        };
    }

    private static DesktopRecordedCall Call(DesktopControlTools tools, string toolId, JsonObject args)
    {
        var descriptor = tools.Descriptors.Single(d => d.ToolId == toolId);
        var input = System.Text.Json.JsonSerializer.Deserialize(args, descriptor.InputType, DesktopControlTools.Json)!;
        var result = (DesktopToolResult)descriptor.Invoke(input, CancellationToken.None).GetAwaiter().GetResult()!;
        Assert.True(result.Ok, $"{toolId} failed: {result.Error ?? result.Text}");
        return new(toolId, args, result);
    }

    private static JsonObject Act(string control, string action, string type, string? value = null, string? secret = null)
    {
        var o = new JsonObject { ["control"] = control, ["action"] = action, ["window"] = "accountapp", ["controlType"] = type };
        if (value is not null) o["value"] = value;
        if (secret is not null) o["secret"] = secret;
        return o;
    }

    [Fact]
    public async Task AnEmailChoiceIsRecordedAndTheCodeIsReadFromTheMailboxAtReplay()
    {
        var store = new JsonFileAgentProcedureStore(Path.Combine(_dir, "procedures"));
        var mailboxReads = new List<string>();
        var codes = new Queue<string>(["481516", "232342"]);
        Task<string?> Mailbox(string slot, CancellationToken _) { mailboxReads.Add(slot); return System.Threading.Tasks.Task.FromResult<string?>(codes.Dequeue()); }
        var task = Task(Mailbox);

        var learn = SignIn(offerEmail: true, expectedCode: "481516");
        var tools = Tools(learn).WithSlots(new DelegateSecretSlots(task.SlotNames, Mailbox));
        var saved = await new DesktopProcedureRunner(Tools(learn)).SaveAsync(task, store,
        [
            Call(tools, DesktopControlTools.ActTool, Act("Email", "set", "edit", value: "person@example.test")),
            Call(tools, DesktopControlTools.ActTool, Act("Continue", "click", "button")),
            Call(tools, DesktopControlTools.ActTool, Act("Email me a code", "click", "button")),
            Call(tools, DesktopControlTools.ActTool, Act("Code", "set", "edit", secret: DesktopSlots.EmailCode)),
            Call(tools, DesktopControlTools.ActTool, Act("Verify", "click", "button")),
        ], Welcome);
        Assert.Equal(DesktopProcedureStatus.Saved, saved.Status);

        var history = (await store.GetAsync(task.Key))!;
        var steps = history.Revisions.Single(r => r.Id == saved.RevisionId).Steps;
        Assert.Equal("Email me a code", steps[2].Arguments["control"]!.GetValue<string>());
        Assert.Equal(DesktopSlots.EmailCode, steps[3].Arguments["secret"]!.GetValue<string>());
        Assert.Null(steps[3].Arguments["value"]);

        // Replay reads a fresh code from the mailbox at the code step; neither code is ever stored.
        var replayDesktop = SignIn(offerEmail: true, expectedCode: "232342");
        var replay = await new DesktopProcedureRunner(Tools(replayDesktop)).ReplayAsync(task, store);
        Assert.Equal(DesktopProcedureStatus.Completed, replay.Status);
        Assert.Contains(replayDesktop.Actions, a => a.Value == "232342");
        Assert.Equal([DesktopSlots.EmailCode, DesktopSlots.EmailCode], mailboxReads);
        var json = File.ReadAllText(store.PathFor(task.Key));
        Assert.DoesNotContain("481516", json);
        Assert.DoesNotContain("232342", json);

        // No code in the mailbox: a person must enter it. The revision stays active for the next run.
        var noMail = SignIn(offerEmail: true);
        var stopped = await new DesktopProcedureRunner(Tools(noMail)).ReplayAsync(Task((_, _) => System.Threading.Tasks.Task.FromResult<string?>(null)), store);
        Assert.Equal(DesktopProcedureStatus.PersonRequired, stopped.Status);
        Assert.Equal(3, stopped.Stop!.StepIndex);
        Assert.Equal(FailureCategories.AuthenticationRequired, stopped.Stop.FailureCategory);
        Assert.Equal(saved.RevisionId, (await store.GetAsync(task.Key))!.ActiveRevisionId);
    }

    [Fact]
    public async Task WithoutAnEmailChoiceReplayStopsDeliberatelyAtThePersonBoundary()
    {
        var store = new JsonFileAgentProcedureStore(Path.Combine(_dir, "procedures"));
        var task = Task(null);

        // Learning: the site offers only text or call, so the agent records a person boundary; the person enters the code.
        var learn = SignIn(offerEmail: false, personEntersCode: true);
        var tools = Tools(learn);
        var runner = new DesktopProcedureRunner(tools);
        var calls = new List<DesktopRecordedCall>
        {
            Call(tools, DesktopControlTools.ActTool, Act("Email", "set", "edit", value: "person@example.test")),
            Call(tools, DesktopControlTools.ActTool, Act("Continue", "click", "button")),
            Call(tools, DesktopControlTools.ActTool, Act("Text me a code", "click", "button")),
            new(DesktopProcedureRunner.PersonStepTool, new JsonObject { ["reason"] = "one-time-code", ["until"] = Welcome },
                new DesktopToolResult(true, "The person entered the code.")),
        };
        var saved = await runner.SaveAsync(task, store, calls, Welcome);
        Assert.Equal(DesktopProcedureStatus.Saved, saved.Status);

        // Replay without a waiting person stops at the boundary, twice, and keeps the same active revision.
        for (var run = 0; run < 2; run++)
        {
            var desktop = SignIn(offerEmail: false);
            var outcome = await new DesktopProcedureRunner(Tools(desktop)).ReplayAsync(task, store);
            Assert.Equal(DesktopProcedureStatus.PersonRequired, outcome.Status);
            Assert.Equal(3, outcome.Stop!.StepIndex);
            Assert.Equal("authentication_required", outcome.Stop.Reason);
            Assert.Equal(FailureCategories.AuthenticationRequired, outcome.Stop.FailureCategory);
            Assert.Contains(outcome.Stop.Observation.Controls, c => c.Name == "Code");
            var history = (await store.GetAsync(task.Key))!;
            Assert.Equal(saved.RevisionId, history.ActiveRevisionId);
        }

        // When the host waits for a person (who enters the code), the same revision runs to a verified finish.
        var notified = new List<string>();
        var attended = SignIn(offerEmail: false, personEntersCode: true);
        var finished = await new DesktopProcedureRunner(Tools(attended)).ReplayAsync(task with
        {
            PersonTimeout = TimeSpan.FromMilliseconds(500),
            NotifyPersonAsync = (reason, _) => { notified.Add(reason); return System.Threading.Tasks.Task.CompletedTask; },
        }, store);
        Assert.Equal(DesktopProcedureStatus.Completed, finished.Status);
        Assert.Equal(["one-time-code"], notified);
    }
}
