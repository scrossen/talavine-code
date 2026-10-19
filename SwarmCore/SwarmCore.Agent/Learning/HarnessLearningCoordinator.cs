using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;
using SwarmCore.HarnessLearning;

namespace SwarmCore.Agent.Learning;

/// <summary>Host-owned data location and current profile/tenant authority. No customer body data in this descriptor.</summary>
public sealed record HarnessLearningHost(string Directory, string Partition, Func<bool> IsCurrent);
public sealed record PreparationInput(string Source, string[] Fields);
public sealed record PreparationResult(string Strategy, JsonElement Values);

public sealed class HarnessLearningCoordinator(HarnessLearningHost host, ILlmRouter router, HarnessLearningOptions options,
    ICurrentTurnContextAccessor? currentTurn = null, ILlmProviderRegistry? providers = null,
    IModelScout? scout = null, IModelScoutRoutingGate? routingGate = null)
{
    public FileHarnessLearningState Store => new(host.Directory);
    private async Task<HarnessLearningModel> ModelAsync(CancellationToken ct, (string Provider, string Model)? inheritedRoute = null, bool research = false)
    {
        if (!host.IsCurrent()) throw new OperationCanceledException("Learning scope changed.");
        var preference = await router.GetFunctionPreferenceAsync(HarnessLearningModel.Function);
        var selectedRoute = preference is not null ? (preference.Provider, preference.Model) : inheritedRoute;
        // Preparation research needs text generation, not the agent-chat tool protocol.
        // Foreground admission stays ordinary; exact fixture research uses Scout
        // benchmark admission and never grants production model eligibility.
        var function = research ? HarnessLearningModel.Function : FunctionTypes.AgentChat;
        var route = await router.SelectOptimalModelAsync(function, "Literal structured preparation",
            new() { Interactivity = inheritedRoute is null ? LlmInteractivity.Background : LlmInteractivity.UserWaiting,
                PrivacyClass = inheritedRoute is { } parent && LlmProviderLocalityPolicy.IsLocalProviderName(parent.Provider)
                    ? LlmPrivacyClass.LocalOnly : LlmPrivacyClass.Unspecified,
                OverrideProvider = selectedRoute?.Provider, OverrideModel = selectedRoute?.Model,
                OverrideProvenance = research ? LlmOverrideProvenance.Benchmark : LlmOverrideProvenance.Policy, DisableFallback = true, RequestCancellationToken = ct });
        if (!host.IsCurrent()) throw new OperationCanceledException("Learning scope changed.");
        bool CanBound(LlmModelSelection candidate)
        {
            if (providers is null) return true; // Test/custom hosts may enforce the contract in their model adapter.
            if (providers.GetProvider(candidate.Provider) is not ILlmProviderExecutionContract contract) return false;
            try
            {
                return !contract.TranslateExecution(candidate.Model, new() { MaxOutputTokens = 512, Temperature = 0 },
                    new() { MaxPhysicalRequests = 1, RequireExactModel = true }).IsRejected;
            }
            catch (NotSupportedException) { return false; }
        }
        if (!CanBound(route) && research && selectedRoute is null && !route.IsUserPinned && scout is not null
            && (routingGate is null || await routingGate.IsAutomaticSelectionEnabledAsync(ct)))
        {
            // Ask the owning ranker for eligible alternatives; protocol compatibility only rejects routes.
            // Never override a person's pin, invent eligibility, or branch on a provider name.
            var ranked = await scout.RecommendAsync(new() { FunctionType = function,
                Tier = ModelScoutTier.Economy, Interactivity = LlmInteractivity.Background,
                PrivacyClass = route.Context?.PrivacyClass ?? LlmPrivacyClass.CloudOk }, ct);
            foreach (var candidate in ranked.Candidates.Where(c => c.IsEligible).Take(20))
            {
                var alternative = new LlmModelSelection { Provider = candidate.Candidate.Provider, Model = candidate.Candidate.Model };
                if (!CanBound(alternative)) continue;
                try
                {
                    var admitted = await router.SelectOptimalModelAsync(function, "Literal structured preparation",
                        new() { OverrideProvider = alternative.Provider, OverrideModel = alternative.Model,
                            OverrideProvenance = research ? LlmOverrideProvenance.Benchmark : LlmOverrideProvenance.Policy, DisableFallback = true,
                            PrivacyClass = ranked.Request.PrivacyClass, Interactivity = LlmInteractivity.Background,
                            RequestCancellationToken = ct });
                    if (CanBound(admitted)) { route = admitted; break; }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (InvalidOperationException) { } // Candidate changed between recommendation and admission; try the next eligible route.
            }
        }
        if (!CanBound(route)) throw new NotSupportedException("The learning connection cannot enforce bounded inference.");
        var contractIdentity = (providers?.GetProvider(route.Provider) as ILlmProviderExecutionContract)?.GetExecutionContract(route.Model).CanonicalHash;
        return new(router, route.Provider, route.Model, route.ExecutionSettings, host.IsCurrent, research, contractIdentity);
    }
    public async Task TickAsync(CancellationToken ct)
    {
        var store = Store;
        await store.ReconcileInterruptedAsync(ct);
        if (!options.Enabled || await store.IsPausedAsync(ct)) return;
        var state = await store.ReadAsync(ct);
        // Avoid routing probes every poll while a daily reservation is current.
        if (state.NextRunAt > DateTimeOffset.UtcNow) return;
        HarnessLearningModel model;
        try { model = await ModelAsync(ct, research: true); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { await store.RecordWaitingAsync(ex.GetType().Name, ct); return; }
        await new AutomaticHarnessLearning(store, host.Partition, options, host.IsCurrent).TickAsync(model, ct);
    }
    public async Task<PreparationResult> PrepareAsync(PreparationInput input, CancellationToken ct)
        => await PrepareOnRouteAsync(input, ct, currentTurn?.Current is { Provider: { Length: > 0 } p, Model: { Length: > 0 } m } ? (p, m) : null);
    private async Task<PreparationResult> PrepareOnRouteAsync(PreparationInput input, CancellationToken ct, (string Provider, string Model)? route)
    {
        StructuredPreparationContract.Prompt(input.Source, input.Fields, "plain"); // Validate before selection or inference.
        var store = Store;
        var model = await ModelAsync(ct, route);
        var state = await store.ReadAsync(ct);
        var strategy = "plain";
        if (state.EnvironmentHash == AutomaticHarnessLearning.EnvironmentHash(model, options: options)
            && StructuredPreparationContract.Strategies.ContainsKey(state.ActiveStrategy) && state.ActiveStrategy != "plain")
        {
            var scope = new HarnessExperimentScope(host.Partition, StructuredPreparationContract.Version,
                state.EnvironmentHash, StructuredPreparationContract.SuiteHash);
            var reports = await new FileHarnessExperimentHistory(Path.Combine(store.Root, "reports"), store.EnsureCurrentScope).ReadAsync(scope, ct);
            if (reports.Any(r => r.StopReason is null && r.RecommendedRevisionId == state.ActiveStrategy)) strategy = state.ActiveStrategy;
        }
        var reply = await model.CompleteAsync(StructuredPreparationContract.Prompt(input.Source, input.Fields, strategy), ct);
        store.EnsureCurrentScope();
        if (!host.IsCurrent() || !StructuredPreparationContract.Verify(reply.Text, input.Source, input.Fields))
            throw new InvalidDataException("Prepared data did not satisfy the literal-source contract.");
        return new(strategy, JsonSerializer.Deserialize<JsonElement>(reply.Text));
    }
    public ToolDescriptor PreparationTool((string Provider, string Model)? route = null) => ToolDescriptor.Create<PreparationInput, PreparationResult>(
        "learning.prepare", "learning", "Prepare a JSON object from text already available in this turn. Fields are required output keys; absent values are null. "
        + "Values must be copied literally from Source and are verified before return. Uses this profile's measured preparation strategy. "
        + "No record changes or external actions. Use for structured preparation, not reasoning or paraphrasing.",
        ToolCostClass.Expensive, ToolSideEffect.ReadOnly, (input, ct) => PrepareOnRouteAsync(input, ct, route), keywords: ["extract", "structured", "prepare", "fields", "json"]);
}

/// <summary>Lifecycle only: each pass resolves fresh host-scoped services; shared engine owns cadence/budgets/selection.
/// CRM uses its tenant runner rather than this single-profile scheduler.</summary>
public sealed class HarnessLearningHostedService(IServiceScopeFactory scopes, ILogger<HarnessLearningHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<HarnessLearningCoordinator>().TickAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogWarning("Automatic learning is waiting: {FailureType}", ex.GetType().Name); }
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}

/// <summary>Tool invocation resolves the current profile instead of capturing a startup profile or scoped router.</summary>
public sealed class HarnessPreparationToolRegistration(IServiceScopeFactory scopes, ICapabilityCatalog catalog) : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        catalog.Register(ToolDescriptor.Create<PreparationInput, PreparationResult>("learning.prepare", "learning",
            "Extract verified literal values from source text into requested JSON Fields. Uses measured preparation learning; no external actions. "
            + "Supply only text already authorized and available in the current turn. Missing values become null.",
            ToolCostClass.Expensive, ToolSideEffect.ReadOnly, async (input, token) =>
            {
                using var scope = scopes.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<HarnessLearningCoordinator>().PrepareAsync(input, token);
            }, keywords: ["extract", "structured", "prepare", "fields", "json"]));
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken ct) { catalog.Unregister("learning.prepare"); return Task.CompletedTask; }
}

public static class HarnessLearningRegistration
{
    public static IServiceCollection AddHarnessLearning(this IServiceCollection services,
        Func<IServiceProvider, HarnessLearningHost> host, HarnessLearningOptions options,
        bool schedule = true, bool registerTool = true)
    {
        services.AddSingleton(options);
        services.TryAddScoped(host);
        services.AddScoped<HarnessLearningCoordinator>();
        if (schedule) services.AddHostedService<HarnessLearningHostedService>();
        if (registerTool) services.AddHostedService<HarnessPreparationToolRegistration>();
        return services;
    }
}
