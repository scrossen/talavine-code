using System.Text.Json;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting.Benchmarking;

public sealed class LlmRoutingOptimizationService : ILlmRoutingOptimizationService
{
    private readonly LlmRoutingBenchmarkService _benchmarkService;

    public LlmRoutingOptimizationService(LlmRoutingBenchmarkService benchmarkService)
    {
        _benchmarkService = benchmarkService;
    }

    public async Task<LlmRoutingOptimizationResult> OptimizeAsync(
        LlmRoutingOptimizationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.FunctionTypes.Count == 0)
        {
            throw new ArgumentException("At least one function type is required.", nameof(request));
        }

        if (request.Cases.Count == 0)
        {
            throw new ArgumentException("At least one optimization case is required.", nameof(request));
        }

        var groupedCases = request.Cases
            .GroupBy(c => c.Pass)
            .OrderBy(g => g.Key)
            .ToList();

        var functionPlans = request.FunctionTypes
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(functionType =>
            {
                var passes = groupedCases.Select(g => new LlmBenchmarkPass
                {
                    Name = $"pass-{g.Key}",
                    ShortlistCount = g.Key == 1 ? request.ShortlistCountAfterPass1 : null,
                    Cases = g.Select(c => new LlmBenchmarkCase
                    {
                        Name = c.Name,
                        Prompt = c.Prompt,
                        Weight = c.Weight,
                        Options = new LlmRouterOptions
                        {
                            ForceJson = c.ForceJson,
                            ExpectedFormat = c.ExpectedFormat,
                            MaxTokens = c.MaxTokens,
                            Temperature = c.Temperature
                        },
                        Validator = response => ValidateByCaseSpec(response, c)
                    }).ToList()
                }).ToList();

                return new LlmBenchmarkFunctionPlan
                {
                    FunctionType = functionType,
                    Passes = passes
                };
            })
            .ToList();

        var benchmarkSuite = new LlmBenchmarkSuite
        {
            ApplyRecommendations = request.ApplyRecommendations,
            ApplyUserId = request.ApplyUserId,
            CandidateDiscovery = new LlmBenchmarkCandidateDiscoveryOptions
            {
                IncludeProviderDefaultModels = true,
                DiscoverProviderModels = request.DiscoverProviderModels,
                IncludeOnlyHealthyProviders = request.IncludeOnlyHealthyProviders,
                ValidateModelUsability = request.ValidateModelUsability,
                MaxModelsPerProvider = Math.Max(1, request.MaxModelsPerProvider)
            },
            Functions = functionPlans
        };

        var benchmarkResult = await _benchmarkService.RunAsync(benchmarkSuite, cancellationToken);

        var result = new LlmRoutingOptimizationResult
        {
            TimestampUtc = DateTime.UtcNow,
            Candidates = benchmarkResult.Candidates
                .Select(c => (c.Provider, c.Model))
                .ToList(),
            Functions = benchmarkResult.Functions.Select(f =>
            {
                var finalPass = f.PassResults.Last();
                return new LlmRoutingOptimizationFunctionResult
                {
                    FunctionType = f.FunctionType,
                    RecommendedProvider = f.Recommended?.Provider ?? "unknown",
                    RecommendedModel = f.Recommended?.Model ?? "unknown",
                    FallbackProvider = f.Fallback?.Provider,
                    FallbackModel = f.Fallback?.Model,
                    Rankings = finalPass.CandidateRankings.Select(r => new LlmRoutingOptimizationCandidateResult
                    {
                        Provider = r.Provider,
                        Model = r.Model,
                        Validity = r.WeightedValidity,
                        Accuracy = r.WeightedAccuracy,
                        AvgLatencyMs = r.AverageLatencyMs,
                        AvgCost = r.AverageCost,
                        ErrorCount = r.ErrorCount
                    }).ToList()
                };
            }).ToList()
        };

        return result;
    }

    private static LlmBenchmarkValidationResult ValidateByCaseSpec(string response, LlmOptimizationCaseSpec spec)
    {
        try
        {
            var json = ExtractJsonObject(response);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new LlmBenchmarkValidationResult { IsValid = false, Score = 0, MaxScore = 1, Notes = "Response is not a JSON object" };
            }

            var maxScore = Math.Max(1, spec.RequiredJsonFields.Count) + 1; // +1 for reason/decision semantic checks
            var score = 0;
            var missingFields = new List<string>();

            foreach (var field in spec.RequiredJsonFields)
            {
                if (TryGetCaseInsensitive(root, field, out _))
                {
                    score++;
                }
                else
                {
                    missingFields.Add(field);
                }
            }

            var semanticPass = true;

            if (spec.AllowedDecisionValues is { Count: > 0 } &&
                TryGetCaseInsensitive(root, "Decision", out var decisionEl))
            {
                var decision = decisionEl.GetString() ?? "";
                semanticPass &= spec.AllowedDecisionValues.Any(x =>
                    x.Equals(decision, StringComparison.OrdinalIgnoreCase));
            }

            if (spec.ReasonMustContainAny is { Count: > 0 } &&
                TryGetCaseInsensitive(root, "Reason", out var reasonEl))
            {
                var reason = reasonEl.GetString() ?? "";
                semanticPass &= spec.ReasonMustContainAny.Any(k =>
                    reason.Contains(k, StringComparison.OrdinalIgnoreCase));
            }

            if (semanticPass)
            {
                score++;
            }

            var isValid = missingFields.Count == 0 && semanticPass;
            return new LlmBenchmarkValidationResult
            {
                IsValid = isValid,
                Score = score,
                MaxScore = maxScore,
                Notes = missingFields.Count == 0 ? null : $"Missing fields: {string.Join(", ", missingFields)}"
            };
        }
        catch (Exception ex)
        {
            return new LlmBenchmarkValidationResult
            {
                IsValid = false,
                Score = 0,
                MaxScore = 1,
                Notes = ex.Message
            };
        }
    }

    private static bool TryGetCaseInsensitive(JsonElement root, string propertyName, out JsonElement value)
    {
        foreach (var p in root.EnumerateObject())
        {
            if (p.NameEquals(propertyName) || p.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            return text[start..(end + 1)];
        }

        return text;
    }
}
