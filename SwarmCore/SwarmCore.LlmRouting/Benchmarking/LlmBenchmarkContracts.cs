using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting.Benchmarking;

public sealed record LlmBenchmarkCandidate(string Provider, string Model);

public sealed class LlmBenchmarkValidationResult
{
    public required bool IsValid { get; init; }
    public required int Score { get; init; }
    public required int MaxScore { get; init; }
    public string? Notes { get; init; }

    public static LlmBenchmarkValidationResult PassFail(bool valid, string? notes = null) =>
        new()
        {
            IsValid = valid,
            Score = valid ? 1 : 0,
            MaxScore = 1,
            Notes = notes
        };
}

public sealed class LlmBenchmarkCase
{
    public required string Name { get; init; }
    public required string Prompt { get; init; }
    public required Func<string, LlmBenchmarkValidationResult> Validator { get; init; }
    public LlmRouterOptions? Options { get; init; }
    public double Weight { get; init; } = 1.0d;
}

public sealed class LlmBenchmarkPass
{
    public required string Name { get; init; }
    public required IReadOnlyList<LlmBenchmarkCase> Cases { get; init; }
    public int? ShortlistCount { get; init; }
}

public sealed class LlmBenchmarkFunctionPlan
{
    public required string FunctionType { get; init; }
    public required IReadOnlyList<LlmBenchmarkPass> Passes { get; init; }
}

public sealed class LlmBenchmarkCandidateDiscoveryOptions
{
    public bool IncludeProviderDefaultModels { get; init; } = true;
    public bool DiscoverProviderModels { get; init; } = true;
    public int MaxModelsPerProvider { get; init; } = 2;
    public bool IncludeOnlyHealthyProviders { get; init; } = true;
    public bool ValidateModelUsability { get; init; } = true;
}

public sealed class LlmBenchmarkSuite
{
    public required IReadOnlyList<LlmBenchmarkFunctionPlan> Functions { get; init; }
    public LlmBenchmarkCandidateDiscoveryOptions CandidateDiscovery { get; init; } = new();
    public IReadOnlyList<LlmBenchmarkCandidate>? ExplicitCandidates { get; init; }
    public bool ApplyRecommendations { get; init; } = false;
    public string? ApplyUserId { get; init; }
}

public sealed class LlmBenchmarkCaseResult
{
    public required string CaseName { get; init; }
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public required bool IsValid { get; init; }
    public required int Score { get; init; }
    public required int MaxScore { get; init; }
    public required long LatencyMs { get; init; }
    public required decimal EstimatedCost { get; init; }
    public required int InputTokens { get; init; }
    public required int OutputTokens { get; init; }
    public string? Error { get; init; }
    public string? Notes { get; init; }
}

public sealed class LlmBenchmarkCandidateAggregate
{
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public required double WeightedValidity { get; init; }
    public required double WeightedAccuracy { get; init; }
    public required long AverageLatencyMs { get; init; }
    public required decimal AverageCost { get; init; }
    public required int ErrorCount { get; init; }
}

public sealed class LlmBenchmarkPassResult
{
    public required string PassName { get; init; }
    public required IReadOnlyList<LlmBenchmarkCaseResult> CaseResults { get; init; }
    public required IReadOnlyList<LlmBenchmarkCandidateAggregate> CandidateRankings { get; init; }
}

public sealed class LlmBenchmarkFunctionResult
{
    public required string FunctionType { get; init; }
    public required IReadOnlyList<LlmBenchmarkPassResult> PassResults { get; init; }
    public required LlmBenchmarkCandidate? Recommended { get; init; }
    public required LlmBenchmarkCandidate? Fallback { get; init; }
}

public sealed class LlmBenchmarkSuiteResult
{
    public required IReadOnlyList<LlmBenchmarkCandidate> Candidates { get; init; }
    public required IReadOnlyList<LlmBenchmarkFunctionResult> Functions { get; init; }
}
