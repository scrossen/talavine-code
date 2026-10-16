namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// Snapshot of a previously-served live LLM call that the offline A/B replay
/// scheduler picked as a sample. The champion (frontier / configured) model
/// already produced the recorded response — the judge just needs to ask the
/// challenger the same question and compare.
/// </summary>
public sealed record AbTestChampionSample
{
    /// <summary>Primary key from <c>LlmUsageTracking</c>; used for transcript correlation.</summary>
    public required long UsageId { get; init; }

    /// <summary>Function type (e.g. "spam-detection") this sample was served under.</summary>
    public required string FunctionType { get; init; }

    /// <summary>Champion provider that actually served the live call.</summary>
    public required string ChampionProvider { get; init; }

    /// <summary>Champion model that actually served the live call.</summary>
    public required string ChampionModel { get; init; }

    /// <summary>Original prompt text — required (the row would have been skipped if null).</summary>
    public required string Prompt { get; init; }

    /// <summary>Champion's recorded response text — required (skipped if null).</summary>
    public required string ChampionResponse { get; init; }
}

/// <summary>
/// Replay-style A/B test judge. Called by the offline replay scheduler — never on
/// the live request path. Implementations:
///   1. Call the challenger model with <see cref="AbTestChampionSample.Prompt"/>.
///   2. Compare its response to <see cref="AbTestChampionSample.ChampionResponse"/>
///      (treated as ground truth).
///   3. Persist the verdict to <c>AbTestJudgement</c>.
///
/// Live routing never consults this interface — that was the legacy split-traffic
/// design, removed when we switched to offline replay so a single user turn could
/// never cross provider boundaries mid-loop.
/// </summary>
public interface ILlmAbTestJudge
{
    /// <param name="testId">The active <c>AbTest</c> row this sample belongs to.</param>
    /// <param name="sample">Captured live champion call — prompt + response + provenance.</param>
    /// <param name="challengerProvider">Provider for variant B (the candidate to evaluate).</param>
    /// <param name="challengerModel">Model name for variant B.</param>
    /// <param name="judgeStrategyOverride">Optional strategy override (label/json/llm); null = auto.</param>
    /// <returns>True if a verdict was persisted; false if the call short-circuited
    /// (e.g. challenger errored, judgement cap reached, test inactive).</returns>
    Task<bool> JudgeAsync(
        Guid testId,
        AbTestChampionSample sample,
        string challengerProvider,
        string challengerModel,
        string? judgeStrategyOverride,
        CancellationToken ct = default);
}
