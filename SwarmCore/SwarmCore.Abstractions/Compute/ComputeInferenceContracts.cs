namespace SwarmCore.Abstractions.Compute;

/// <summary>One bounded model call, never a remote tool, shell, or agent-execution grant.</summary>
public sealed record ComputeInferenceRequest
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string JobId { get; init; }
    /// <summary>Both null delegates exact route selection to the receiver's Model Scout.</summary>
    public string? ProviderName { get; init; }
    public string? Model { get; init; }
    public required string FunctionType { get; init; }
    public required string Prompt { get; init; }
    public string? SystemPrompt { get; init; }
    public int MaxTokens { get; init; } = 4096;
    public double? Temperature { get; init; }
    public bool LocalOnly { get; init; }
    public bool AllowSubscription { get; init; }
    public decimal MaxApiCostUsd { get; init; }
    public DateTime ExpiresUtc { get; init; }
}

public sealed record ComputeInferenceLease
{
    public required ComputeInferenceRequest Request { get; init; }
    public long NodeId { get; init; }
    public int TenantId { get; init; }
    public required string LeaseToken { get; init; }
    public DateTime LeaseExpiresUtc { get; init; }
}

/// <summary>No hidden reasoning or provider credentials belong in this result.</summary>
public sealed record ComputeInferenceResult
{
    public bool Success { get; init; }
    public string Content { get; init; } = "";
    public required string ProviderName { get; init; }
    public required string Model { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public bool UsageMeasurementComplete { get; init; }
    public decimal EstimatedCostUsd { get; init; }
    public decimal? ProviderReportedCostUsd { get; init; }
    public string? ErrorCode { get; init; }
}

public sealed record CompleteComputeInferenceRequest(string LeaseToken, ComputeInferenceResult Result);
public sealed record ComputeInferencePollResponse(ComputeInferenceLease? Lease, int RetryAfterSeconds = 15);
public sealed record ComputeInferenceSnapshot(string Status, ComputeInferenceResult? Result);

/// <summary>Host-scoped transport seam. Expiring an inference attempt never completes its business objective.</summary>
public interface IComputeInferenceQueue
{
    Task<Guid> EnqueueAsync(long nodeId, ComputeInferenceRequest request, CancellationToken cancellationToken = default);
    Task<ComputeInferenceSnapshot?> GetResultAsync(Guid requestId, CancellationToken cancellationToken = default);
    Task CancelAsync(Guid requestId, CancellationToken cancellationToken = default);
}
