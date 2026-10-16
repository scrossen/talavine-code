namespace SwarmCore.Skills.Models;

/// <summary>
/// Result of a skill function execution.
/// </summary>
public record SkillResult
{
    /// <summary>
    /// Whether execution was successful.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// The result value (if any).
    /// </summary>
    public object? Result { get; init; }

    /// <summary>
    /// Error message if execution failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Exception details if an exception occurred.
    /// </summary>
    public string? ExceptionDetails { get; init; }

    /// <summary>
    /// Time taken to execute.
    /// </summary>
    public TimeSpan ExecutionTime { get; init; }

    /// <summary>
    /// Whether execution was sandboxed.
    /// </summary>
    public bool WasSandboxed { get; init; }

    /// <summary>
    /// True when the skill that produced this result is below Verified trust
    /// (e.g. a file-based skill). Orchestrators that reflect skill output back
    /// into an LLM context MUST treat the Result as untrusted data — wrap it
    /// in a delimiter and never execute embedded instructions verbatim.
    /// Set by SkillExecutor; mitigates indirect prompt injection via skill output.
    /// </summary>
    public bool IsUntrusted { get; init; }

    /// <summary>
    /// Skill that produced this result.
    /// </summary>
    public string? SkillId { get; init; }

    /// <summary>
    /// Function that produced this result.
    /// </summary>
    public string? FunctionName { get; init; }

    /// <summary>
    /// Additional metadata about the execution.
    /// </summary>
    public Dictionary<string, object> Metadata { get; init; } = [];

    /// <summary>
    /// Create a successful result.
    /// </summary>
    public static SkillResult Ok(object? result = null, TimeSpan? executionTime = null) => new()
    {
        Success = true,
        Result = result,
        ExecutionTime = executionTime ?? TimeSpan.Zero
    };

    /// <summary>
    /// Create a failed result.
    /// </summary>
    public static SkillResult Fail(string errorMessage, string? exceptionDetails = null) => new()
    {
        Success = false,
        ErrorMessage = errorMessage,
        ExceptionDetails = exceptionDetails
    };

    /// <summary>
    /// Get result as typed value.
    /// </summary>
    public T? GetResult<T>()
    {
        if (Result is null)
            return default;

        if (Result is T typedResult)
            return typedResult;

        try
        {
            return (T)Convert.ChangeType(Result, typeof(T));
        }
        catch
        {
            return default;
        }
    }
}

/// <summary>
/// Result of a skill search operation.
/// </summary>
public class SkillSearchResult
{
    /// <summary>
    /// The matching skill.
    /// </summary>
    public required string SkillId { get; init; }

    /// <summary>
    /// Skill name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Skill description.
    /// </summary>
    public string Description { get; init; } = "";

    /// <summary>
    /// Relevance score (0-1).
    /// </summary>
    public double Relevance { get; init; }

    /// <summary>
    /// Matching keywords.
    /// </summary>
    public List<string> MatchingKeywords { get; init; } = [];

    /// <summary>
    /// Category of the skill.
    /// </summary>
    public SkillCategory? Category { get; init; }

    /// <summary>
    /// Trust level of the skill.
    /// </summary>
    public SkillTrustLevel TrustLevel { get; init; }
}

/// <summary>
/// Options for skill search.
/// </summary>
public class SkillSearchOptions
{
    /// <summary>
    /// Maximum results to return.
    /// </summary>
    public int Limit { get; init; } = 10;

    /// <summary>
    /// Minimum relevance score (0-1).
    /// </summary>
    public double MinRelevance { get; init; } = 0.3;

    /// <summary>
    /// Filter by category.
    /// </summary>
    public SkillCategory? Category { get; init; }

    /// <summary>
    /// Filter by trust level.
    /// </summary>
    public SkillTrustLevel? MinTrustLevel { get; init; }

    /// <summary>
    /// Only include enabled skills.
    /// </summary>
    public bool OnlyEnabled { get; init; } = true;
}
