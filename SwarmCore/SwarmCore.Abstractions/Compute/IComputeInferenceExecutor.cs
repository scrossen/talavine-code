namespace SwarmCore.Abstractions.Compute;

/// <summary>Receiver-owned, supplied-context inference. Never grants local tools or filesystem access.</summary>
public interface IComputeInferenceExecutor
{
    Task<ComputeInferenceResult> ExecuteAsync(ComputeInferenceRequest request,
        bool subscriptionSharingEnabled, CancellationToken cancellationToken = default);
}
