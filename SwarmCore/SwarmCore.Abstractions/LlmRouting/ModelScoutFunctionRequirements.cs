namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// Canonical hard capability requirements implied by SwarmCore function types.
/// Callers may add stricter requirements, but omitted chat-tool flags must never
/// make a tool or vision lane eligible for a model that cannot execute it.
/// </summary>
public static class ModelScoutFunctionRequirements
{
    public static bool RequiresToolUse(string? functionType)
        => string.Equals(functionType, FunctionTypes.AgentChat, StringComparison.OrdinalIgnoreCase)
           || string.Equals(functionType, FunctionTypes.AgentLoop, StringComparison.OrdinalIgnoreCase)
           || string.Equals(functionType, FunctionTypes.CodingAssistant, StringComparison.OrdinalIgnoreCase);

    public static bool RequiresVision(string? functionType)
        => string.Equals(functionType, FunctionTypes.Vision, StringComparison.OrdinalIgnoreCase)
           || string.Equals(functionType, FunctionTypes.VisualAnalysis, StringComparison.OrdinalIgnoreCase)
           || string.Equals(functionType, FunctionTypes.CaptchaSolving, StringComparison.OrdinalIgnoreCase)
           || string.Equals(functionType, FunctionTypes.ImageEmbedding, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Function types whose ordinary repeatable route is a typed-JSON contract. A
    /// successful, validated call for one of these functions may safely initialize
    /// its generic function preference; structured output on any other function is
    /// treated as a one-off constraint. MessagePipeline is deliberately excluded
    /// because its prompt/suite-bound plan, rather than FunctionPreferences, owns
    /// that route.
    /// </summary>
    public static bool UsesStableStructuredOutput(string? functionType)
        => string.Equals(functionType, FunctionTypes.AgentInstructionChat, StringComparison.OrdinalIgnoreCase)
           || string.Equals(functionType, FunctionTypes.VibeBuilder, StringComparison.OrdinalIgnoreCase)
           || string.Equals(functionType, FunctionTypes.ChatSegmentation, StringComparison.OrdinalIgnoreCase)
           || string.Equals(functionType, FunctionTypes.SpamDetection, StringComparison.OrdinalIgnoreCase)
           || string.Equals(functionType, FunctionTypes.Classification, StringComparison.OrdinalIgnoreCase)
           || string.Equals(functionType, FunctionTypes.DocumentProcessing, StringComparison.OrdinalIgnoreCase);
}
