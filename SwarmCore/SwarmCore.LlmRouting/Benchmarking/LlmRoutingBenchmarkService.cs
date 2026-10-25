using System.Diagnostics;
using SwarmCore.Abstractions.LlmRouting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SwarmCore.LlmRouting.Services;

namespace SwarmCore.LlmRouting.Benchmarking;

public sealed class LlmRoutingBenchmarkService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LlmRoutingBenchmarkService> _logger;

    public LlmRoutingBenchmarkService(
        IServiceScopeFactory scopeFactory,
        ILogger<LlmRoutingBenchmarkService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<LlmBenchmarkSuiteResult> RunAsync(LlmBenchmarkSuite suite, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Resolve all scoped dependencies within a dedicated scope (this service is a singleton)
        using var scope = _scopeFactory.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<ILlmRouter>();

        await router.ReloadCostsAsync();

        var settingsProvider = scope.ServiceProvider.GetRequiredService<ILlmSettingsProvider>();
        var providerRegistry = scope.ServiceProvider.GetRequiredService<ILlmProviderRegistry>();
        var providerDiscovery = scope.ServiceProvider.GetRequiredService<LlmProviderDiscoveryCoordinator>();

        var candidates = suite.ExplicitCandidates?.ToList() ?? await DiscoverCandidatesAsync(router, settingsProvider, providerRegistry, providerDiscovery, suite.CandidateDiscovery, cancellationToken);
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException("No benchmark candidates were discovered.");
        }

        var functionResults = new List<LlmBenchmarkFunctionResult>(suite.Functions.Count);
        foreach (var function in suite.Functions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var functionResult = await RunFunctionAsync(router, function, candidates, cancellationToken);
            functionResults.Add(functionResult);

            if (suite.ApplyRecommendations && functionResult.Recommended is not null)
            {
                await router.SetFunctionPreferenceAsync(
                    function.FunctionType,
                    functionResult.Recommended.Provider,
                    functionResult.Recommended.Model,
                    functionResult.Fallback?.Provider,
                    functionResult.Fallback?.Model,
                    suite.ApplyUserId ?? "llm-benchmark");
            }
        }

        return new LlmBenchmarkSuiteResult
        {
            Candidates = candidates,
            Functions = functionResults
        };
    }

    private async Task<List<LlmBenchmarkCandidate>> DiscoverCandidatesAsync(
        ILlmRouter router,
        ILlmSettingsProvider settingsProvider,
        ILlmProviderRegistry providerRegistry,
        LlmProviderDiscoveryCoordinator providerDiscovery,
        LlmBenchmarkCandidateDiscoveryOptions options,
        CancellationToken cancellationToken)
    {
        var health = await router.TestProvidersAsync();
        var providerSettings = await settingsProvider.GetAllProviderSettingsAsync();
        var priorityMap = providerSettings.ToDictionary(x => x.ProviderName, x => x.Priority, StringComparer.OrdinalIgnoreCase);

        var providerModels = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providerSettings.Where(x => x.IsEnabled))
        {
            if (options.IncludeOnlyHealthyProviders &&
                health.TryGetValue(provider.ProviderName, out var state) &&
                !state.IsHealthy)
            {
                continue;
            }

            if (!providerModels.ContainsKey(provider.ProviderName))
            {
                providerModels[provider.ProviderName] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            if (options.IncludeProviderDefaultModels && !string.IsNullOrWhiteSpace(provider.DefaultModel))
            {
                providerModels[provider.ProviderName].Add(provider.DefaultModel!);
            }
        }

        if (options.DiscoverProviderModels)
        {
            foreach (var provider in providerRegistry.GetAllProviders())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!providerModels.ContainsKey(provider.ProviderName))
                {
                    continue;
                }

                try
                {
                    var models = await providerDiscovery.GetAvailableModelsAsync(
                        provider,
                        TimeSpan.FromSeconds(10),
                        cancellationToken,
                        forceRefresh: true);
                    foreach (var model in models.Take(options.MaxModelsPerProvider))
                    {
                        providerModels[provider.ProviderName].Add(model);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to discover models for provider {Provider}", provider.ProviderName);
                }
            }
        }

        var candidates = providerModels
            .SelectMany(kvp => kvp.Value.Select(model => new LlmBenchmarkCandidate(kvp.Key, model)))
            .OrderBy(c => priorityMap.TryGetValue(c.Provider, out var p) ? p : int.MaxValue)
            .ThenBy(c => c.Provider, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Model, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!options.ValidateModelUsability)
        {
            return candidates;
        }

        var validated = new List<LlmBenchmarkCandidate>(candidates.Count);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var (isValid, message) = await router.ValidateModelAsync(candidate.Provider, candidate.Model);
                if (isValid)
                {
                    validated.Add(candidate);
                }
                else
                {
                    _logger.LogInformation(
                        "Skipping benchmark candidate {Provider}/{Model}: {Message}",
                        candidate.Provider,
                        candidate.Model,
                        message);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Skipping benchmark candidate {Provider}/{Model}: validation error", candidate.Provider, candidate.Model);
            }
        }

        return validated;
    }

    private async Task<LlmBenchmarkFunctionResult> RunFunctionAsync(
        ILlmRouter router,
        LlmBenchmarkFunctionPlan function,
        IReadOnlyList<LlmBenchmarkCandidate> allCandidates,
        CancellationToken cancellationToken)
    {
        var passResults = new List<LlmBenchmarkPassResult>(function.Passes.Count);
        var candidatePool = allCandidates.ToList();

        foreach (var pass in function.Passes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var caseResults = new List<LlmBenchmarkCaseResult>();
            foreach (var candidate in candidatePool)
            {
                foreach (var benchmarkCase in pass.Cases)
                {
                    var result = await ExecuteCaseAsync(router, function.FunctionType, candidate, benchmarkCase, cancellationToken);
                    caseResults.Add(result);
                }
            }

            var rankings = BuildRankings(pass, caseResults);
            passResults.Add(new LlmBenchmarkPassResult
            {
                PassName = pass.Name,
                CaseResults = caseResults,
                CandidateRankings = rankings
            });

            if (pass.ShortlistCount.HasValue && pass.ShortlistCount.Value > 0)
            {
                candidatePool = rankings
                    .Take(pass.ShortlistCount.Value)
                    .Select(r => new LlmBenchmarkCandidate(r.Provider, r.Model))
                    .ToList();

                if (candidatePool.Count == 0)
                {
                    candidatePool = allCandidates.ToList();
                }
            }
        }

        var finalRanking = passResults.Last().CandidateRankings;
        var recommended = finalRanking.FirstOrDefault();
        var fallback = finalRanking.Skip(1).FirstOrDefault();

        return new LlmBenchmarkFunctionResult
        {
            FunctionType = function.FunctionType,
            PassResults = passResults,
            Recommended = recommended is null ? null : new LlmBenchmarkCandidate(recommended.Provider, recommended.Model),
            Fallback = fallback is null ? null : new LlmBenchmarkCandidate(fallback.Provider, fallback.Model)
        };
    }

    private async Task<LlmBenchmarkCaseResult> ExecuteCaseAsync(
        ILlmRouter router,
        string functionType,
        LlmBenchmarkCandidate candidate,
        LlmBenchmarkCase benchmarkCase,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        string? response = null;
        string? error = null;
        LlmBenchmarkValidationResult validation;
        // The exact physical calls this case made, reported by the router itself;
        // a "recent usage" read could return another caller's row or none yet.
        var completions = new List<LlmUsageCompletion>();

        try
        {
            var options = CloneOptions(benchmarkCase.Options);
            options.OverrideProvider = candidate.Provider;
            options.OverrideModel = candidate.Model;
            options.OverrideProvenance = LlmOverrideProvenance.Benchmark;
            // Score only the candidate: no runtime fallback or substitute route.
            options.DisableFallback = true;
            options.Interactivity = LlmInteractivity.Background;
            options.RequestCancellationToken = cancellationToken;
            options.UsageCompletionObserver = completion =>
            {
                lock (completions)
                {
                    completions.Add(completion);
                }
                return Task.CompletedTask;
            };

            response = await router.GenerateResponseAsync(functionType, benchmarkCase.Prompt, options);
            var foreign = completions.FirstOrDefault(c => !IsCandidateRoute(c, candidate));
            if (foreign is not null)
            {
                error = $"Answered by {foreign.Provider}/{foreign.Model} instead of the candidate route.";
                validation = LlmBenchmarkValidationResult.PassFail(false, error);
            }
            else
            {
                validation = benchmarkCase.Validator(response);
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            validation = LlmBenchmarkValidationResult.PassFail(false, ex.Message);
        }
        finally
        {
            sw.Stop();
        }

        List<LlmUsageCompletion> measured;
        lock (completions)
        {
            measured = completions.Where(c => IsCandidateRoute(c, candidate)).ToList();
        }

        return new LlmBenchmarkCaseResult
        {
            CaseName = benchmarkCase.Name,
            Provider = candidate.Provider,
            Model = candidate.Model,
            IsValid = validation.IsValid,
            Score = validation.Score,
            MaxScore = validation.MaxScore,
            LatencyMs = sw.ElapsedMilliseconds,
            EstimatedCost = measured.Sum(c => c.EstimatedCost),
            InputTokens = measured.Sum(c => c.InputTokens),
            OutputTokens = measured.Sum(c => c.OutputTokens),
            Error = error,
            Notes = validation.Notes
        };
    }

    private static bool IsCandidateRoute(LlmUsageCompletion completion, LlmBenchmarkCandidate candidate)
        => string.Equals(completion.Provider, candidate.Provider, StringComparison.OrdinalIgnoreCase)
           && string.Equals(completion.Model, candidate.Model, StringComparison.OrdinalIgnoreCase);

    private static List<LlmBenchmarkCandidateAggregate> BuildRankings(
        LlmBenchmarkPass pass,
        IReadOnlyList<LlmBenchmarkCaseResult> caseResults)
    {
        var weights = pass.Cases.ToDictionary(c => c.Name, c => Math.Max(0.0001d, c.Weight), StringComparer.OrdinalIgnoreCase);

        var grouped = caseResults
            .GroupBy(r => $"{r.Provider}::{r.Model}", StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var rows = g.ToList();
                var totalWeight = rows.Sum(r => weights.TryGetValue(r.CaseName, out var w) ? w : 1.0d);
                if (totalWeight <= 0d) totalWeight = 1d;

                var weightedValidity = rows.Sum(r => (weights.TryGetValue(r.CaseName, out var w) ? w : 1.0d) * (r.IsValid ? 1d : 0d)) / totalWeight;
                var weightedAccuracy = rows.Sum(r =>
                {
                    var weight = weights.TryGetValue(r.CaseName, out var w) ? w : 1.0d;
                    var max = Math.Max(1, r.MaxScore);
                    return weight * ((double)r.Score / max);
                }) / totalWeight;

                return new LlmBenchmarkCandidateAggregate
                {
                    Provider = rows[0].Provider,
                    Model = rows[0].Model,
                    WeightedValidity = weightedValidity,
                    WeightedAccuracy = weightedAccuracy,
                    AverageLatencyMs = Convert.ToInt64(rows.Average(r => r.LatencyMs)),
                    AverageCost = rows.Average(r => r.EstimatedCost),
                    ErrorCount = rows.Count(r => !string.IsNullOrWhiteSpace(r.Error))
                };
            })
            .OrderByDescending(r => r.WeightedValidity)
            .ThenByDescending(r => r.WeightedAccuracy)
            .ThenBy(r => r.ErrorCount)
            .ThenBy(r => r.AverageCost)
            .ThenBy(r => r.AverageLatencyMs)
            .ToList();

        return grouped;
    }

    private static LlmRouterOptions CloneOptions(LlmRouterOptions? source)
    {
        if (source is null)
        {
            return new LlmRouterOptions();
        }

        return new LlmRouterOptions
        {
            ExecutionSettings = source.ExecutionSettings is null
                ? null
                : new Dictionary<string, object>(source.ExecutionSettings, StringComparer.OrdinalIgnoreCase),
            UserId = source.UserId,
            SessionId = source.SessionId,
            TurnId = source.TurnId,
            ForceRefresh = source.ForceRefresh,
            MaxTokens = source.MaxTokens,
            SuppressOutputTokenLimit = source.SuppressOutputTokenLimit,
            Temperature = source.Temperature,
            ExpectedFormat = source.ExpectedFormat,
            SourceType = source.SourceType,
            SourceId = source.SourceId,
            ForceJson = source.ForceJson,
            RequiresVision = source.RequiresVision,
            RequiresToolUse = source.RequiresToolUse,
            ModelRequest = source.ModelRequest?.Snapshot(),
            RequiresStructuredOutput = source.RequiresStructuredOutput,
            AllowTextJsonContractRepair = source.AllowTextJsonContractRepair,
            MinimumContextTokens = source.MinimumContextTokens,
            RequireCurrentWorkloadSizing = source.RequireCurrentWorkloadSizing,
            TaskDemand = source.TaskDemand,
            MinimumCapabilityScore = source.MinimumCapabilityScore,
            RequireMeasuredCapabilityScore = source.RequireMeasuredCapabilityScore,
            RequiredTier = source.RequiredTier,
            BlockWeakerPolicyFallback = source.BlockWeakerPolicyFallback,
            RequireCurrentFunctionEvidence = source.RequireCurrentFunctionEvidence,
        };
    }
}
