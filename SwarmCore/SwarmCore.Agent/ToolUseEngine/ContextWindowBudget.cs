namespace SwarmCore.Agent.ToolUseEngine;

/// <summary>
/// Token budgets the engine derives from a route's context window. Requests
/// are sized with the preflight's character proxy (three characters per
/// token), so both limits are compared against that estimate.
/// </summary>
/// <param name="WindowTokens">The route's context window (prompt plus generated output).</param>
/// <param name="TriggerTokens">Estimated prompt size above which older context is compacted.</param>
/// <param name="HardLimitTokens">Estimated prompt size the window can physically hold while
/// leaving room to generate the reply.</param>
internal readonly record struct ContextWindowBudget(int WindowTokens, double TriggerTokens, int HardLimitTokens)
{
    // JSON, code, and non-English text tokenize denser than three characters
    // per token; keep the physical limit clear of the estimate's error.
    private const double EstimateHeadroomFraction = 0.05;

    public static ContextWindowBudget For(int windowTokens, WorkingContextCompactionOptions options)
    {
        var reserveCap = (int)Math.Floor(windowTokens * Math.Clamp(options.MaximumReserveFraction, 0, 0.9));
        var generation = Math.Max(0, options.OutputReserveTokens) + Math.Max(0, options.ReasoningReserveTokens);
        var reserves = generation + Math.Max(0, options.ToolResultReserveTokens);
        return new(
            windowTokens,
            windowTokens * Math.Clamp(options.TriggerFraction, 0.1, 0.99) - Math.Min(reserves, reserveCap),
            windowTokens - Math.Min(generation, reserveCap) - (int)Math.Ceiling(windowTokens * EstimateHeadroomFraction));
    }

    public static int EstimateTokens(int characters) => (int)Math.Ceiling(characters / 3d);

    public bool UnderPressure(int requestCharacters)
        => EstimateTokens(requestCharacters) > TriggerTokens || ExceedsHardLimit(requestCharacters);

    public bool ExceedsHardLimit(int requestCharacters) => EstimateTokens(requestCharacters) > HardLimitTokens;

    public int CharactersOverHardLimit(int requestCharacters)
        => Math.Max(0, requestCharacters - HardLimitTokens * 3);
}
