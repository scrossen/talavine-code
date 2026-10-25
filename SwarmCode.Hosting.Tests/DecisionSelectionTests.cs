using SwarmCore.Abstractions.LlmRouting.Decisions;
using SwarmCore.Coding.Routing;
using SwarmCore.LlmRouting.Decisions;
using Microsoft.Extensions.DependencyInjection;
using SwarmCore.Agent.Runtime;
using Microsoft.Extensions.Options;
using SwarmCore.LlmRouting.Configuration;
using SwarmCore.LlmRouting.LocalRuntime;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCode.Hosting.Tests;

public sealed class DecisionSelectionTests : TempDirTest
{
    private static readonly CodingLane[] Lanes = [new("fast", "test", "small"), new("strong", "test", "large@high")];
    private async Task<DecisionSessionCoordinator> CoordinatorAsync(IChoiceDecisionProvider provider, WorkspaceSessionCatalog catalog)
    {
        var store = new DecisionSettingsStore(Paths);
        await store.SaveAsync(new() { Enabled = true, Connection = new() { Provider = provider.Name },
            Candidates = [new("fast", "fast", "small", "Simple"), new("strong", "strong", "large@high", "Difficult")] });
        return new(store, catalog, new ModelDecisionSelector([provider]));
    }

    [Fact]
    public async Task FirstPromptSelects_Persists_AndDoesNotRerun()
    {
        var catalog = new WorkspaceSessionCatalog(Paths);
        var session = await catalog.CreateAsync(Tmp, laneName: "fast", model: "small");
        var provider = new Fake();
        var coordinator = await CoordinatorAsync(provider, catalog);
        var first = await coordinator.PrepareAsync(session.Id, "complex task", "fast", "small", Lanes, false, default);
        Assert.Equal("strong", first.Lane); Assert.Equal("large@high", first.Model);
        var reopened = new DecisionSessionCoordinator(new(Paths), new(Paths), new([provider]));
        var second = await reopened.PrepareAsync(session.Id, "followup", "fast", "small", Lanes, false, default);
        Assert.Equal("strong", second.Lane); Assert.Equal(1, provider.Calls);
        await reopened.MarkFailedAsync(session.Id, second.Revision, default);
        await reopened.PrepareAsync(session.Id, "failed again", "fast", "small", Lanes, false, default);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task ExplicitSameModelSkipsDecision()
    {
        var catalog = new WorkspaceSessionCatalog(Paths);
        var session = await catalog.CreateAsync(Tmp, laneName: "fast", model: "small");
        await catalog.SetModelAsync(session.Id, "fast", "small");
        var provider = new Fake(); var coordinator = await CoordinatorAsync(provider, catalog);
        var result = await coordinator.PrepareAsync(session.Id, "hard", "fast", "small", Lanes, false, default);
        Assert.Equal("small", result.Model); Assert.Equal(0, provider.Calls);
        Assert.Null(await coordinator.ObserveAsync(session.Id, result.Revision, Lanes[1], true, default));
        var reopened = await new WorkspaceSessionCatalog(Paths).GetAsync(session.Id);
        Assert.Equal("small", reopened!.Selection!.Model);
        Assert.Equal(ModelSelectionOrigin.User, reopened.Selection.Origin);
    }

    [Fact]
    public async Task UserChoiceDuringInferenceWins_AcrossCatalogInstances()
    {
        var catalog = new WorkspaceSessionCatalog(Paths); var other = new WorkspaceSessionCatalog(Paths);
        var session = await catalog.CreateAsync(Tmp);
        var provider = new Fake { Before = () => other.SetModelAsync(session.Id, "fast", "user-model") };
        var coordinator = await CoordinatorAsync(provider, catalog);
        var result = await coordinator.PrepareAsync(session.Id, "hard", null, null, Lanes, false, default);
        Assert.Equal("user-model", result.Model); Assert.Null(result.Notice);
        Assert.True((await catalog.GetAsync(session.Id))!.Selection!.UserSelected);
    }

    [Fact]
    public async Task DeleteDuringInferenceDoesNotResurrect()
    {
        var catalog = new WorkspaceSessionCatalog(Paths); var session = await catalog.CreateAsync(Tmp);
        var provider = new Fake { Before = () => new WorkspaceSessionCatalog(Paths).DeleteAsync(session.Id) };
        var coordinator = await CoordinatorAsync(provider, catalog);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.PrepareAsync(session.Id, "hard", null, null, Lanes, false, default));
        Assert.Null(await catalog.GetAsync(session.Id));
    }

    [Fact]
    public async Task HandoffPersists_AndCannotOverwriteLaterUserChoice()
    {
        var catalog = new WorkspaceSessionCatalog(Paths); var session = await catalog.CreateAsync(Tmp);
        var coordinator = await CoordinatorAsync(new Fake(), catalog);
        var first = await coordinator.PrepareAsync(session.Id, "hard", null, null, Lanes, false, default);
        var revision = await coordinator.ObserveAsync(session.Id, first.Revision, Lanes[0], true, default);
        Assert.Equal(ModelSelectionOrigin.Handoff, (await catalog.GetAsync(session.Id))!.Selection!.Origin);
        await catalog.SetModelAsync(session.Id, "strong", "user-model");
        Assert.Null(await coordinator.ObserveAsync(session.Id, revision, Lanes[0], true, default));
        Assert.Equal("user-model", (await catalog.GetAsync(session.Id))!.Selection!.Model);
    }

    [Fact]
    public async Task FailureUsesDefault_AndDoesNotLoopOnEveryPrompt()
    {
        var catalog = new WorkspaceSessionCatalog(Paths); var session = await catalog.CreateAsync(Tmp, laneName:"fast", model:"small");
        var provider = new Fake { Before = () => throw new HttpRequestException("secret-body") };
        var coordinator = await CoordinatorAsync(provider, catalog);
        var result = await coordinator.PrepareAsync(session.Id, "hard", "fast", "small", Lanes, false, default);
        Assert.Equal("small", result.Model); Assert.DoesNotContain("secret-body", result.Notice);
        await coordinator.PrepareAsync(session.Id, "next", "fast", "small", Lanes, false, default);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task IndependentWritersPreserveTitleAndTemporaryFlag_AndSettingsRejectStaleEdit()
    {
        var a = new WorkspaceSessionCatalog(Paths); var b = new WorkspaceSessionCatalog(Paths);
        var session = await a.CreateAsync(Tmp);
        await a.GetAsync(session.Id);
        await b.RenameAsync(session.Id, "Renamed"); await b.SetTemporaryAsync(session.Id, true);
        await a.SetModelAsync(session.Id, "fast", "small");
        var fresh = await b.GetAsync(session.Id);
        Assert.Equal("Renamed", fresh!.Title); Assert.True(fresh.Temporary);
        var settings = new DecisionSettingsStore(Paths); var stale = await settings.ReadAsync();
        await new DecisionSettingsStore(Paths).SaveAsync(stale);
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.SaveAsync(stale));
    }

    private sealed class Fake : IChoiceDecisionProvider
    {
        public string Name => "fake";
        public int Calls;
        public Func<Task>? Before;
        public Func<ChoiceDecisionRequest, string>? Choice;
        public async Task<ChoiceDecision> ChooseAsync(DecisionConnection c, ChoiceDecisionRequest r, CancellationToken ct = default)
        { Calls++; if (Before is not null) await Before(); return new(Choice?.Invoke(r) ?? "strong", .9); }
    }

    private sealed class DelegatedInventory : IDelegatedModelInventory
    {
        public int Calls;
        public Func<CancellationToken, Task>? Before;
        public IReadOnlyList<ModelDecisionCandidate> Choices = [new("local", "fast", "small", "Installed local tool worker")];
        public async Task<IReadOnlyList<ModelDecisionCandidate>> GetEligibleAsync(string id, string task,
            IReadOnlyList<ModelDecisionCandidate> configured, CancellationToken ct)
        { Calls++; if (Before is not null) await Before(ct); return Choices; }
    }

    private async Task<(DecisionSessionCoordinator Coordinator, WorkspaceSessionCatalog Sessions, WorkspaceSessionInfo Parent,
        WorkspaceSessionInfo Child, Fake Provider, DelegatedInventory Inventory)> DelegationAsync(bool enabled = true)
    {
        var sessions = new WorkspaceSessionCatalog(Paths);
        var parent = await sessions.CreateAsync(Tmp, "Lead", "strong", model: "large@high");
        await sessions.SetModelAsync(parent.Id, "strong", "large@high");
        parent = (await sessions.GetAsync(parent.Id))!;
        var child = await sessions.CreateAsync(Tmp, "Worker", "strong", model: "large@high", parentSessionId: parent.Id);
        var provider = new Fake { Choice = request => request.Options.Any(o => o.Id == "routine") ? "routine" : request.Options[0].Id };
        var inventory = new DelegatedInventory();
        var settings = new DecisionSettingsStore(Paths);
        await settings.SaveAsync(new() { Enabled = true, AutoDelegateRoutineTasks = enabled,
            Connection = new() { Provider = provider.Name },
            Candidates = [new("local", "fast", "small", "Local"), new("strong", "strong", "large@high", "Strong")] });
        return (new(settings, sessions, new([provider]), delegatedInventory: inventory), sessions, parent, child, provider, inventory);
    }

    [Fact]
    public async Task Delegated_routine_task_selects_local_once_while_lead_and_followups_keep_their_selection()
    {
        var fixture = await DelegationAsync();
        var selected = await fixture.Coordinator.PrepareDelegatedAsync(fixture.Child.Id, fixture.Parent.Id,
            "Read two files and list their declared options.", false);
        Assert.Equal("fast", selected.Lane); Assert.Equal("small", selected.Model);
        Assert.Equal(SwarmCore.Abstractions.LlmRouting.LlmOverrideProvenance.Policy, selected.Provenance);
        Assert.True(selected.RoutineDelegation);
        Assert.Contains("verify", selected.Notice, StringComparison.OrdinalIgnoreCase);
        var child = (await fixture.Sessions.GetAsync(fixture.Child.Id))!;
        Assert.Equal(ModelSelectionOrigin.Decision, child.Selection!.Origin);
        Assert.True(child.Selection.InitialAssessmentComplete); Assert.False(child.Selection.UserSelected);
        Assert.Equal(fixture.Parent.Selection, (await fixture.Sessions.GetAsync(fixture.Parent.Id))!.Selection);
        var repeated = await fixture.Coordinator.PrepareDelegatedAsync(child.Id, fixture.Parent.Id, "Follow up", false);
        var continuation = await fixture.Coordinator.PrepareAsync(child.Id, "Follow up", "strong", "large@high", Lanes, true, default);
        Assert.Equal("small", repeated.Model); Assert.Equal("small", continuation.Model);
        Assert.Equal(SwarmCore.Abstractions.LlmRouting.LlmOverrideProvenance.Policy, continuation.Provenance);
        Assert.True(continuation.RoutineDelegation);
        Assert.Equal(2, fixture.Provider.Calls); Assert.Equal(1, fixture.Inventory.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Delegation_disabled_or_explicit_route_preserves_inheritance_without_decision(bool enabled, bool explicitRoute)
    {
        var fixture = await DelegationAsync(enabled);
        var selected = await fixture.Coordinator.PrepareDelegatedAsync(fixture.Child.Id, fixture.Parent.Id,
            "Routine work", explicitRoute);
        Assert.Equal("large@high", selected.Model);
        Assert.Equal(0, fixture.Provider.Calls); Assert.Equal(0, fixture.Inventory.Calls);
    }

    [Theory]
    [InlineData("difficult")]
    [InlineData("uncertain")]
    public async Task Delegated_difficult_or_uncertain_task_keeps_the_lead_model(string classification)
    {
        var fixture = await DelegationAsync();
        fixture.Provider.Choice = _ => classification;
        var result = await fixture.Coordinator.PrepareDelegatedAsync(fixture.Child.Id, fixture.Parent.Id, "Investigate state corruption", false);
        Assert.Equal("large@high", result.Model); Assert.Equal(1, fixture.Provider.Calls);
        Assert.Equal("large@high", (await fixture.Sessions.GetAsync(fixture.Parent.Id))!.Selection!.Model);
    }

    [Fact]
    public async Task Delegation_without_fresh_local_inventory_does_not_call_the_decider_or_substitute_a_worker()
    {
        var fixture = await DelegationAsync(); fixture.Inventory.Choices = [];
        var result = await fixture.Coordinator.PrepareDelegatedAsync(fixture.Child.Id, fixture.Parent.Id, "Routine work", false);
        Assert.Equal("large@high", result.Model); Assert.Equal(0, fixture.Provider.Calls);
    }

    [Fact]
    public async Task Delegated_child_manual_choice_wins_during_inference_and_independent_title_changes_survive()
    {
        var fixture = await DelegationAsync();
        fixture.Provider.Before = async () =>
        {
            var other = new WorkspaceSessionCatalog(Paths);
            await other.RenameAsync(fixture.Child.Id, "Worker renamed");
            await other.SetModelAsync(fixture.Child.Id, "strong", "manual-model");
        };
        var result = await fixture.Coordinator.PrepareDelegatedAsync(fixture.Child.Id, fixture.Parent.Id, "Routine work", false);
        Assert.Equal("manual-model", result.Model);
        Assert.Equal(SwarmCore.Abstractions.LlmRouting.LlmOverrideProvenance.User, result.Provenance);
        Assert.False(result.RoutineDelegation);
        Assert.Contains("newer selection", result.Notice, StringComparison.OrdinalIgnoreCase);
        var saved = (await new WorkspaceSessionCatalog(Paths).GetAsync(fixture.Child.Id))!;
        Assert.Equal("Worker renamed", saved.Title); Assert.True(saved.Temporary); Assert.True(saved.Selection!.UserSelected);
    }

    [Fact]
    public async Task Deleting_child_during_delegated_decision_never_resurrects_it()
    {
        var fixture = await DelegationAsync();
        fixture.Provider.Before = () => new WorkspaceSessionCatalog(Paths).DeleteAsync(fixture.Child.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.PrepareDelegatedAsync(
            fixture.Child.Id, fixture.Parent.Id, "Routine work", false));
        Assert.Null(await new WorkspaceSessionCatalog(Paths).GetAsync(fixture.Child.Id));
    }

    [Fact]
    public async Task Disabling_delegation_during_a_pending_decision_discards_the_old_local_selection()
    {
        var fixture = await DelegationAsync();
        fixture.Provider.Before = async () =>
        {
            var store = new DecisionSettingsStore(Paths);
            var current = await store.ReadAsync();
            await store.SaveAsync(current with { AutoDelegateRoutineTasks = false });
        };
        var selected = await fixture.Coordinator.PrepareDelegatedAsync(fixture.Child.Id, fixture.Parent.Id, "Routine work", false);
        Assert.Equal("large@high", selected.Model);
        Assert.Contains("settings changed", selected.Notice, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Coordinator_excludes_each_local_wait_from_the_whole_decision_budget(bool delegated)
    {
        var fixture = await DelegationAsync();
        var store = new DecisionSettingsStore(Paths);
        var config = await store.ReadAsync();
        await store.SaveAsync(config with { Connection = new() { Provider = "ollama", TimeoutSeconds = 1 } });
        var requests = new List<LocalLlmRuntimeRequest>();
        var runtime = new DecisionRuntime(async (request, ct) =>
        {
            requests.Add(request);
            await Task.Delay(1200, ct); // Longer than the complete active decision budget.
            return new EmptyLease();
        });
        using var http = new HttpClient(new DecisionHandler(async (_, ct) => await Task.Delay(30, ct)));
        var coordinator = new DecisionSessionCoordinator(store, fixture.Sessions,
            new([new OllamaDecisionProvider(http, runtime)]), delegatedInventory: fixture.Inventory);
        var result = delegated
            ? await coordinator.PrepareDelegatedAsync(fixture.Child.Id, fixture.Parent.Id, "Read two files", false)
            : await coordinator.PrepareAsync(fixture.Child.Id, "Read two files", "strong", "large@high", Lanes, false, default);
        Assert.Equal("small", result.Model);
        Assert.Equal(delegated ? 2 : 1, requests.Count);
        Assert.All(requests, request => Assert.Equal(LocalLlmSchedulingClass.RoutingDecision, request.SchedulingClass));
        if (delegated) Assert.True(requests[1].AdmissionTimeout < requests[0].AdmissionTimeout);
        Assert.False((await fixture.Sessions.GetAsync(fixture.Child.Id))!.Selection!.UserSelected);
    }

    [Fact]
    public async Task Delegated_classification_and_selection_share_one_active_budget()
    {
        var fixture = await DelegationAsync();
        var store = new DecisionSettingsStore(Paths); var config = await store.ReadAsync();
        await store.SaveAsync(config with { Connection = new() { Provider = "ollama", TimeoutSeconds = 1 } });
        var runtime = new DecisionRuntime((_, _) => Task.FromResult<IDisposable>(new EmptyLease()));
        using var handler = new DecisionHandler(async (_, ct) => await Task.Delay(600, ct));
        using var http = new HttpClient(handler);
        var coordinator = new DecisionSessionCoordinator(store, fixture.Sessions,
            new([new OllamaDecisionProvider(http, runtime)]), delegatedInventory: fixture.Inventory);
        var result = await coordinator.PrepareDelegatedAsync(fixture.Child.Id, fixture.Parent.Id, "Read two files", false);
        Assert.Equal("large@high", result.Model);
        Assert.False(result.RoutineDelegation);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Inventory_which_ignores_cancellation_cannot_make_assessment_unbounded()
    {
        var fixture = await DelegationAsync();
        var store = new DecisionSettingsStore(Paths); var config = await store.ReadAsync();
        await store.SaveAsync(config with { Connection = config.Connection with { TimeoutSeconds = 1 } });
        fixture.Inventory.Before = _ => new TaskCompletionSource().Task;
        var result = await fixture.Coordinator.PrepareDelegatedAsync(fixture.Child.Id, fixture.Parent.Id, "Routine", false)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("large@high", result.Model);
        Assert.Equal(0, fixture.Provider.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Custom_provider_which_ignores_cancellation_is_bounded_in_both_paths(bool delegated)
    {
        var fixture = await DelegationAsync();
        var store = new DecisionSettingsStore(Paths); var config = await store.ReadAsync();
        await store.SaveAsync(config with { Connection = config.Connection with { TimeoutSeconds = 1 } });
        fixture.Provider.Before = () => new TaskCompletionSource().Task;
        var result = await (delegated
            ? fixture.Coordinator.PrepareDelegatedAsync(fixture.Child.Id, fixture.Parent.Id, "Routine", false)
            : fixture.Coordinator.PrepareAsync(fixture.Child.Id, "Routine", "strong", "large@high", Lanes, false, default))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("large@high", result.Model);
        Assert.Equal(1, fixture.Provider.Calls);
    }

    [Fact]
    public async Task Ordinary_selection_discards_a_decision_after_settings_change()
    {
        var catalog = new WorkspaceSessionCatalog(Paths);
        var session = await catalog.CreateAsync(Tmp, laneName: "fast", model: "small");
        var provider = new Fake { Before = async () =>
        {
            var store = new DecisionSettingsStore(Paths); var current = await store.ReadAsync();
            await store.SaveAsync(current with { Enabled = false });
        } };
        var coordinator = await CoordinatorAsync(provider, catalog);
        var result = await coordinator.PrepareAsync(session.Id, "hard", "fast", "small", Lanes, false, default);
        Assert.Equal("small", result.Model);
        Assert.Contains("settings changed", result.Notice, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("small", (await new WorkspaceSessionCatalog(Paths).GetAsync(session.Id))!.Selection!.Model);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_cancellation_during_admission_never_saves_an_assessment(bool delegated)
    {
        var fixture = await DelegationAsync();
        var before = (await fixture.Sessions.GetAsync(fixture.Child.Id))!.Selection;
        var store = new DecisionSettingsStore(Paths); var config = await store.ReadAsync();
        await store.SaveAsync(config with { Connection = new() { Provider = "ollama", TimeoutSeconds = 1 } });
        using var caller = new CancellationTokenSource();
        var runtime = new DecisionRuntime(async (_, ct) =>
        { caller.Cancel(); await Task.Delay(Timeout.Infinite, ct); return new EmptyLease(); });
        using var http = new HttpClient(new DecisionHandler((_, _) => throw new InvalidOperationException("Must not send")));
        var coordinator = new DecisionSessionCoordinator(store, fixture.Sessions,
            new([new OllamaDecisionProvider(http, runtime)]), delegatedInventory: fixture.Inventory);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delegated
            ? coordinator.PrepareDelegatedAsync(fixture.Child.Id, fixture.Parent.Id, "Routine", false, caller.Token)
            : coordinator.PrepareAsync(fixture.Child.Id, "Routine", "strong", "large@high", Lanes, false, caller.Token));
        Assert.Equal(before, (await fixture.Sessions.GetAsync(fixture.Child.Id))!.Selection);
    }

    [Theory]
    [InlineData(false, "user")]
    [InlineData(true, "user")]
    [InlineData(false, "settings")]
    [InlineData(true, "settings")]
    [InlineData(false, "deleted-settings")]
    [InlineData(true, "deleted-settings")]
    [InlineData(false, "deleted-session")]
    [InlineData(true, "deleted-session")]
    public async Task Concurrent_edits_or_deletion_during_actual_local_admission_win_over_the_old_decision(bool delegated, string change)
    {
        var fixture = await DelegationAsync();
        var store = new DecisionSettingsStore(Paths); var config = await store.ReadAsync();
        await store.SaveAsync(config with { Connection = new() { Provider = "ollama", TimeoutSeconds = 1 } });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var runtime = new DecisionRuntime(async (_, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.SetResult();
                await release.Task.WaitAsync(ct);
            }
            return new EmptyLease();
        });
        using var http = new HttpClient(new DecisionHandler((_, _) => Task.CompletedTask));
        var coordinator = new DecisionSessionCoordinator(store, fixture.Sessions,
            new([new OllamaDecisionProvider(http, runtime)]), delegatedInventory: fixture.Inventory);
        var pending = delegated
            ? coordinator.PrepareDelegatedAsync(fixture.Child.Id, fixture.Parent.Id, "Routine", false)
            : coordinator.PrepareAsync(fixture.Child.Id, "Routine", "strong", "large@high", Lanes, false, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var other = new WorkspaceSessionCatalog(Paths);
            if (change == "user") await other.SetModelAsync(fixture.Child.Id, "strong", "manual-model");
            else if (change == "settings")
            {
                var current = await store.ReadAsync();
                await new DecisionSettingsStore(Paths).SaveAsync(current with { Enabled = false });
            }
            else if (change == "deleted-settings") File.Delete(Path.Combine(Paths.RootDirectory, "decision-selection.json"));
            else await other.DeleteAsync(fixture.Child.Id);
        }
        finally { release.TrySetResult(); }
        if (change == "deleted-session")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
            Assert.Null(await new WorkspaceSessionCatalog(Paths).GetAsync(fixture.Child.Id));
        }
        else
        {
            var result = await pending;
            Assert.Equal(change == "user" ? "manual-model" : "large@high", result.Model);
            Assert.Equal(change == "user" ? LlmOverrideProvenance.User
                : delegated ? LlmOverrideProvenance.Policy : (LlmOverrideProvenance?)null, result.Provenance);
            Assert.False(result.RoutineDelegation);
            Assert.Equal(result.Model, (await new WorkspaceSessionCatalog(Paths).GetAsync(fixture.Child.Id))!.Selection!.Model);
        }
    }

    private sealed class EmptyLease : IDisposable { public void Dispose() { } }
    private sealed class DecisionHandler(Func<HttpRequestMessage, CancellationToken, Task> before) : HttpMessageHandler
    {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            await before(request, ct);
            using var json = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var options = json.RootElement.GetProperty("questions").GetProperty("selection").GetProperty("criteria");
            var choice = options.TryGetProperty("routine", out _) ? "routine" : options.EnumerateObject().First().Name;
            return new(System.Net.HttpStatusCode.OK)
                { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { answers = new { selection = new { choice } } })) };
        }
    }
    private sealed class DecisionRuntime(Func<LocalLlmRuntimeRequest, CancellationToken, Task<IDisposable>> acquire) : ILocalLlmRuntimeManager
    {
        public Task<IDisposable> AcquireAsync(LocalLlmRuntimeRequest request, CancellationToken ct = default) => acquire(request, ct);
        public Task<ILocalLlmRuntimeExclusiveSession> AcquireExclusiveActivityAsync(LocalLlmRuntimeRequest request,
            LocalLlmRuntimeActivity activity, CancellationToken ct = default) => throw new NotSupportedException();
        public IDisposable TrackActivity(LocalLlmRuntimeActivity activity) => throw new NotSupportedException();
        public void ApplyExecutionPolicy(LocalLlmRuntimeRequest request, IDictionary<string, object> settings) => throw new NotSupportedException();
        public int HighPriorityPending => 0;
        public LocalLlmRuntimeSnapshot GetSnapshot() => new(new Dictionary<LocalLlmWorkloadKind, string>(), [], null, 0, 0, 0);
    }

    private sealed class InventoryHandler : HttpMessageHandler, IHttpClientFactory
    {
        public int Calls;
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            string body;
            if (request.RequestUri!.AbsolutePath == "/api/version") body = "{\"version\":\"fixture\"}";
            else if (request.RequestUri.AbsolutePath == "/api/tags")
                body = """{"models":[{"name":"worker","digest":"worker-digest","size":100},{"name":"embedding","digest":"embedding-digest","size":100},{"name":"vision","digest":"vision-digest","size":100},{"name":"remote-alias","digest":"remote-digest","size":100}]}""";
            else
            {
                using var json = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                body = json.RootElement.GetProperty("model").GetString() switch
                {
                    "worker" => """{"capabilities":["completion","tools"],"model_info":{}}""",
                    "embedding" => """{"capabilities":["embedding"],"model_info":{}}""",
                    "vision" => """{"capabilities":["vision"],"model_info":{}}""",
                    _ => """{"capabilities":["completion","tools"],"model_info":{},"remote_host":"https://example.invalid"}""",
                };
            }
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }

    [Fact]
    public async Task Fresh_inventory_excludes_embedding_vision_remote_alias_missing_and_unhealthy_routes()
    {
        using var handler = new InventoryHandler();
        var choices = new[] { "worker", "embedding", "vision", "remote-alias", "missing" }
            .Select(model => new ModelDecisionCandidate(model, model, model, "Fixture metadata")).ToArray();
        var lanes = new OptionsCodingLaneSource(Options.Create(new CodingLaneOptions
        { Lanes = choices.Select(c => new CodingLane(c.Route, "Ollama", c.Model)).ToList() }));
        var health = new LaneHealthTracker();
        var inventory = new DelegatedModelInventory(lanes, new OllamaSetupClient(handler),
            Options.Create(new OllamaProviderOptions { Endpoint = "http://127.0.0.1:11434" }), new ModelSourcesStore(Paths), health);
        var selected = await inventory.GetEligibleAsync("child", "Read one file", choices, default);
        Assert.Equal("worker", Assert.Single(selected).Model);
        health.MarkUnavailable("worker", "Fixture unavailable", TimeSpan.FromMinutes(1));
        Assert.Empty(await inventory.GetEligibleAsync("child", "Read one file", choices, default));
        Assert.True(handler.Calls > 0);
    }

    [Fact]
    public async Task Non_loopback_inventory_endpoint_is_never_queried_for_local_delegation()
    {
        using var handler = new InventoryHandler();
        var inventory = new DelegatedModelInventory(new OptionsCodingLaneSource(Options.Create(new CodingLaneOptions())),
            new OllamaSetupClient(handler), Options.Create(new OllamaProviderOptions { Endpoint = "https://example.invalid" }),
            new ModelSourcesStore(Paths));
        Assert.Empty(await inventory.GetEligibleAsync("child", "Routine", [], default));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(false, true, "small")]
    [InlineData(true, true, "small")]
    [InlineData(true, false, "small")]
    [InlineData(true, true, "small@high")]
    public async Task ActualConversationRunner_DispatchesStartingChoice_WithoutDisablingHandoff_AndPreservesSessionAfterRestart(bool userSelected, bool allowHandOff, string userModel)
    {
        var catalog = new WorkspaceSessionCatalog(Paths); var session = await catalog.CreateAsync(Tmp);
        if (userSelected) await catalog.SetModelAsync(session.Id, "fast", userModel);
        var coordinator = await CoordinatorAsync(new Fake(), catalog);
        var orchestrator = new CaptureOrchestrator();
        var services = new ServiceCollection(); services.AddLogging();
        services.AddSingleton(catalog); services.AddSingleton(coordinator);
        services.AddSingleton<ICodingTurnOrchestrator>(orchestrator);
        services.AddSingleton<IConversationStore>(new FileConversationStore(Paths));
        services.AddSingleton(new FileConversationJobStore(Paths));
        using var provider = services.BuildServiceProvider();
        using var runner = new ConversationRunner(provider, session.Id);
        var finished = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.TurnFinished += r => finished.TrySetResult(r);
        Assert.True(runner.TrySend("Investigate concurrency", "fast", SwarmCore.Coding.Permissions.CodingPermissionMode.Auto, allowHandOff, "small"));
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(userSelected ? "fast" : "strong", orchestrator.Request!.LaneName);
        Assert.Equal(userSelected ? userModel : "large@high", orchestrator.Request.ModelOverride);
        Assert.False(orchestrator.Request.SelectionPinned);
        Assert.Equal(allowHandOff, CodingRoutingPolicy.CanHandOff(orchestrator.Request, configured: true));
        // A refusal hands off to the next lane (the point of SwarmCode); the inner router still may not swap a lane's model.
        Assert.False(orchestrator.Request.StopOnRefusal);
        Assert.True(orchestrator.Request.DisableRouteFallback);
        var saved = await new WorkspaceSessionCatalog(Paths).GetAsync(session.Id);
        Assert.Equal(userSelected ? userModel : "large@high", saved!.Selection!.Model);
        Assert.Equal(userSelected ? "fast" : "strong", saved.LaneName);
    }

    private sealed class CaptureOrchestrator : ICodingTurnOrchestrator
    {
        public CodingTurnRequest? Request;
        public IReadOnlyList<CodingLane> Lanes => DecisionSelectionTests.Lanes;
        public CodingLane? ResolveLane(string? name) => Lanes.FirstOrDefault(l => l.Name == name);
        public async IAsyncEnumerable<TurnEvent> RunAsync(CodingTurnRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Request = request; await Task.Yield();
            yield return new CodingLaneSelected("test", Lanes[1], 1);
            yield return new TextChunk("test", "Synthetic execution receipt");
            // No false completion: the host marks this incomplete and requests reassessment.
        }
    }
}
