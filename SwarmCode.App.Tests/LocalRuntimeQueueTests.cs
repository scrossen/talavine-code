using Bunit;
using SwarmCode.App.Components.Chat;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCode.App.Tests;

public sealed class LocalRuntimeQueueTests : BunitContext
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static LocalLlmRuntimeQueueItem Item(string id, string? owner, LocalLlmRuntimeQueuePhase phase) => new(
        id, Environment.ProcessId, DateTime.UtcNow, "Background helper", owner, "Local provider", "example-model",
        LocalLlmWorkloadKind.Text, owner is null ? LlmInteractivity.Background : LlmInteractivity.UserWaiting,
        LlmWorkloadKind.DeferredEnrichment, phase, Now.AddSeconds(-40), Now.AddSeconds(-30), Now)
        { AdmissionDeadlineUtc = Now.AddSeconds(80) };

    [Fact]
    public void Waiting_shows_actual_blocker_countdown_and_actions_without_sending_anything()
    {
        var stopped = 0; var models = 0;
        var view = Render<LocalRuntimeQueue>(p => p
            .Add(x => x.Snapshot, new([Item("active", null, LocalLlmRuntimeQueuePhase.UsingModel), Item("mine", "chat", LocalLlmRuntimeQueuePhase.WaitingForDevice)], false))
            .Add(x => x.OwnerId, "chat").Add(x => x.IsRunning, true).Add(x => x.UtcNow, Now)
            .Add(x => x.OnStop, () => stopped++).Add(x => x.OnChooseModel, () => models++));
        Assert.Contains("Waiting for local AI on this device", view.Markup);
        Assert.Contains("Background helper", view.Markup);
        Assert.Contains("Data processing", view.Markup);
        Assert.Contains("1m 20s", view.Markup);
        Assert.Contains("has not been sent", view.Markup);
        view.FindAll("button").Single(x => x.TextContent == "Stop waiting").Click();
        view.FindAll("button").Single(x => x.TextContent == "Model choices").Click();
        Assert.Equal(1, stopped); Assert.Equal(1, models);
    }

    [Fact]
    public void Missing_blocker_does_not_invent_a_name_position_or_completion_time()
    {
        var view = Render<LocalRuntimeQueue>(p => p
            .Add(x => x.Snapshot, new([Item("mine", "chat", LocalLlmRuntimeQueuePhase.WaitingForDevice)], true))
            .Add(x => x.OwnerId, "chat").Add(x => x.IsRunning, true));
        Assert.Contains("has not shared its current task", view.Markup);
        Assert.Contains("not a guaranteed turn order", view.Markup);
        Assert.Contains("Some activity could not be read", view.Markup);
        Assert.DoesNotContain("Restart Ollama", view.Markup);
    }

    [Fact]
    public void Active_model_does_not_offer_stop_waiting_or_claim_that_inference_is_stuck()
    {
        var view = Render<LocalRuntimeQueue>(p => p
            .Add(x => x.Snapshot, new([Item("mine", "chat", LocalLlmRuntimeQueuePhase.UsingModel)], false))
            .Add(x => x.OwnerId, "chat").Add(x => x.IsRunning, true));
        Assert.Contains("Using local model", view.Markup);
        Assert.DoesNotContain("Stop waiting</button>", view.Markup);
        Assert.DoesNotContain("Code will stop waiting in", view.Markup);
    }

    [Fact]
    public void Other_conversations_are_not_mislabeled_this_chat_and_markup_is_encoded()
    {
        var item = Item("other", "other-chat", LocalLlmRuntimeQueuePhase.UsingModel) with { Application = "<script>alert(1)</script>" };
        var view = Render<LocalRuntimeQueue>(p => p.Add(x => x.Snapshot, new([item], false)).Add(x => x.OwnerId, "chat").Add(x => x.IsRunning, true));
        Assert.DoesNotContain("<script>", view.Markup);
        Assert.DoesNotContain("This chat", view.Markup);
        Assert.Contains("&lt;script&gt;", view.Markup);
    }

    [Fact]
    public void Live_polling_updates_the_wait_state_and_clears_it_after_admission()
    {
        var statuses = new List<string?>();
        var snapshot = new LocalLlmRuntimeQueueSnapshot([Item("mine", "chat", LocalLlmRuntimeQueuePhase.WaitingInApp)], false);
        var view = Render<LocalRuntimeQueue>(p => p.Add(x => x.OwnerId, "chat").Add(x => x.IsRunning, true)
            .Add(x => x.ReadQueue, _ => Task.FromResult(snapshot))
            .Add(x => x.OnStatusChanged, value => statuses.Add(value)));
        view.WaitForAssertion(() => Assert.Contains("Waiting for local AI in this app", statuses), TimeSpan.FromSeconds(3));
        snapshot = new([Item("mine", "chat", LocalLlmRuntimeQueuePhase.UsingModel)], false);
        view.WaitForAssertion(() => Assert.Contains("Using local model", statuses), TimeSpan.FromSeconds(3));
        Assert.DoesNotContain("Stop waiting</button>", view.Markup);
    }
}
