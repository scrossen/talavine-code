namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// How demanding one call is, as the calling host knows it. Hosts declare it
/// from their own metadata (the surface the user is working in, who started
/// the work, the size of what they are asking for); it is never inferred from
/// provider or model names, and never from untrusted message content.
/// </summary>
public enum LlmTaskComplexity
{
    Unspecified = 0,
    Routine = 1,
    Complex = 2,
    HighStakes = 3,
}

/// <summary>
/// The kind of surface a call serves. Hosts map their own views onto these
/// generic kinds; SwarmCore never names a host page.
/// </summary>
public enum LlmWorkSurface
{
    Unspecified = 0,
    General = 1,

    /// <summary>
    /// A source-grounded workspace where the user reasons over evidence:
    /// disputes, deadlines, records, and the documents behind them.
    /// </summary>
    EvidenceWorkspace = 2,
}

/// <summary>
/// Host-declared demand for one call. Every field is optional; the router
/// combines it with the call's interactivity, prompt size, and output budget
/// through <see cref="LlmTaskDemandPolicy"/>. Demand shapes ranking only: it
/// never relaxes privacy, pins, tool/vision capability, or spend limits.
/// </summary>
public sealed record LlmTaskDemand
{
    public LlmTaskComplexity Complexity { get; init; }
    public LlmWorkSurface Surface { get; init; }

    /// <summary>
    /// Output the caller expects to author. Overrides the output-budget
    /// heuristic for ranking when larger (a long-form rewrite, a brief).
    /// </summary>
    public int? ExpectedOutputTokens { get; init; }

    /// <summary>
    /// Tool results an agent loop is expected to add to its context before it
    /// answers. Counted toward the window the route has to hold.
    /// </summary>
    public int? ExpectedToolResultTokens { get; init; }

    /// <summary>
    /// The part of the prompt compaction cannot remove: system prompt, the
    /// current request, and evidence the answer must quote. A route whose
    /// window cannot hold this plus the output is excluded.
    /// </summary>
    public int? IrreducibleInputTokens { get; init; }

    /// <summary>Bounded privacy-safe reason codes, for audit only.</summary>
    public IReadOnlyList<string> Signals { get; init; } = [];

    public static LlmTaskDemand? Merge(LlmTaskDemand? left, LlmTaskDemand? right)
    {
        if (left is null) return right;
        if (right is null) return left;
        return new LlmTaskDemand
        {
            Complexity = (LlmTaskComplexity)Math.Max((int)left.Complexity, (int)right.Complexity),
            Surface = left.Surface == LlmWorkSurface.EvidenceWorkspace || right.Surface == LlmWorkSurface.EvidenceWorkspace
                ? LlmWorkSurface.EvidenceWorkspace
                : (LlmWorkSurface)Math.Max((int)left.Surface, (int)right.Surface),
            ExpectedOutputTokens = Max(left.ExpectedOutputTokens, right.ExpectedOutputTokens),
            ExpectedToolResultTokens = Max(left.ExpectedToolResultTokens, right.ExpectedToolResultTokens),
            IrreducibleInputTokens = Max(left.IrreducibleInputTokens, right.IrreducibleInputTokens),
            Signals = left.Signals.Concat(right.Signals)
                .Where(signal => !string.IsNullOrWhiteSpace(signal))
                .Distinct(StringComparer.Ordinal)
                .Take(LlmTaskDemandPolicy.MaximumSignals)
                .ToList(),
        };

        static int? Max(int? a, int? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
    }
}

/// <summary>What the demand asks Model Scout to prefer.</summary>
public enum LlmDemandClass
{
    /// <summary>Nobody is waiting: background, bulk, and maintenance work.</summary>
    Bulk = 0,
    InteractiveRoutine = 1,
    InteractiveComplex = 2,
    InteractiveHighStakes = 3,
}

/// <summary>A call's demand after host metadata, interactivity, and size are combined.</summary>
public sealed record LlmResolvedTaskDemand(
    LlmDemandClass Class,
    bool ConversationalAssistant,
    int EstimatedInputTokens,
    int ExpectedToolResultTokens,
    int EstimatedOutputTokens,
    int IrreducibleInputTokens,
    IReadOnlyList<string> Signals)
{
    public bool IsUserWaiting => Class != LlmDemandClass.Bulk;

    public bool IsComplexOrHighStakes => Class is LlmDemandClass.InteractiveComplex
        or LlmDemandClass.InteractiveHighStakes;

    /// <summary>The window the whole request needs without any compaction.</summary>
    public int RequiredContextTokens => SaturatingAdd(
        SaturatingAdd(EstimatedInputTokens, ExpectedToolResultTokens), EstimatedOutputTokens);

    /// <summary>The window the request needs after every compactable part is removed.</summary>
    public int IrreducibleContextTokens => SaturatingAdd(IrreducibleInputTokens, EstimatedOutputTokens);

    private static int SaturatingAdd(int left, int right)
        => (int)Math.Min(int.MaxValue, (long)Math.Max(0, left) + Math.Max(0, right));
}

/// <summary>
/// Shared demand policy for every host. User-waiting work that is complex or
/// high-stakes prefers frontier cloud/subscription routes when privacy allows;
/// background and bulk work prefers local routes. The thresholds describe the
/// request, never a provider.
/// </summary>
public static class LlmTaskDemandPolicy
{
    public const string Version = "demand-v1";
    public const int MaximumSignals = 8;

    /// <summary>A prompt this large is complex synthesis, not a quick exchange.</summary>
    public const int ComplexPromptTokens = 12_000;

    /// <summary>Authored output this long is long-form work.</summary>
    public const int LongFormOutputTokens = 1_200;

    public static LlmResolvedTaskDemand Resolve(
        string? functionType,
        LlmInteractivity interactivity,
        LlmWorkloadKind workload,
        LlmTaskDemand? declared,
        int estimatedInputTokens,
        int estimatedOutputTokens)
    {
        var resolvedWorkload = LlmWorkloadPolicy.Resolve(workload, interactivity, functionType);
        var assistant = !string.IsNullOrWhiteSpace(functionType)
                        && FunctionTypeProfiles.Resolve(functionType).IsConversationalAssistant;
        var input = Math.Max(0, estimatedInputTokens);
        var output = Math.Max(Math.Max(0, estimatedOutputTokens), declared?.ExpectedOutputTokens ?? 0);
        var toolResults = Math.Max(0, declared?.ExpectedToolResultTokens ?? 0);
        var irreducible = Math.Clamp(declared?.IrreducibleInputTokens ?? 0, 0, input == 0 ? int.MaxValue : input);
        var signals = new List<string>();

        LlmDemandClass demandClass;
        if (!LlmWorkloadPolicy.IsLatencySensitive(resolvedWorkload))
        {
            demandClass = LlmDemandClass.Bulk;
            signals.Add("nobody-waiting");
        }
        else
        {
            demandClass = LlmDemandClass.InteractiveRoutine;
            if (declared?.Complexity == LlmTaskComplexity.HighStakes)
            {
                demandClass = LlmDemandClass.InteractiveHighStakes;
                signals.Add("declared-high-stakes");
            }
            else if (declared?.Complexity == LlmTaskComplexity.Complex)
            {
                demandClass = LlmDemandClass.InteractiveComplex;
                signals.Add("declared-complex");
            }

            if (declared?.Surface == LlmWorkSurface.EvidenceWorkspace)
            {
                demandClass = Stronger(demandClass, LlmDemandClass.InteractiveComplex);
                signals.Add("evidence-workspace");
            }

            if (output >= LongFormOutputTokens)
            {
                demandClass = Stronger(demandClass, LlmDemandClass.InteractiveComplex);
                signals.Add("long-form-output");
            }

            if (input + toolResults >= ComplexPromptTokens)
            {
                demandClass = Stronger(demandClass, LlmDemandClass.InteractiveComplex);
                signals.Add("large-context");
            }

            if (assistant) signals.Add("assistant-conversation");
        }

        if (declared is not null)
            signals.AddRange(declared.Signals);

        return new LlmResolvedTaskDemand(
            demandClass,
            assistant,
            input,
            toolResults,
            output,
            irreducible,
            signals
                .Where(IsSafeSignal)
                .Distinct(StringComparer.Ordinal)
                .Take(MaximumSignals)
                .ToList());
    }

    private static LlmDemandClass Stronger(LlmDemandClass current, LlmDemandClass candidate)
        => (LlmDemandClass)Math.Max((int)current, (int)candidate);

    private static bool IsSafeSignal(string? signal)
        => !string.IsNullOrWhiteSpace(signal)
           && signal.Length <= 48
           && signal.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}
