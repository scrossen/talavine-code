namespace SwarmCore.Agent.Interactions;

/// <summary>Partition-bound facade suitable for desktop, mobile, LAN, and worker consumers.</summary>
public interface IInteractionRequestService
{
    string PartitionKey { get; }
    Task<InteractionRequestSnapshot> CreateAsync(InteractionRequestCreate request, CancellationToken ct = default);
    Task<InteractionRequestSnapshot?> GetAsync(string interactionId, CancellationToken ct = default);
    Task<InteractionRequestSnapshot?> GetByCallerIdAsync(string callerRequestId, CancellationToken ct = default);
    Task<IReadOnlyList<InteractionRequestSnapshot>> ListPendingAsync(InteractionRequestQuery? query = null, CancellationToken ct = default);
    Task<InteractionRequestPage> ReadChangesAsync(InteractionRequestCursor after, int take = 200, CancellationToken ct = default);
    Task<InteractionRequestSnapshot> CompleteAsync(string interactionId, InteractionRequestCompletion completion, CancellationToken ct = default);
}

public sealed class InteractionRequestService(
    IInteractionRequestStore store,
    string partitionKey = "default") : IInteractionRequestService
{
    public string PartitionKey { get; } = string.IsNullOrWhiteSpace(partitionKey)
        ? throw new ArgumentException("Partition key is required.", nameof(partitionKey))
        : partitionKey.Trim();

    public async Task<InteractionRequestSnapshot> CreateAsync(InteractionRequestCreate request, CancellationToken ct = default)
    {
        QueuedChatCredentialPolicy.Validate(request);
        if (request.IsBlocking && request.Kind != InteractionRequestKind.QueuedChatUpdate)
            await Runtime.MissionInteractionBoundary.CheckAsync(request.CallerRequestId, "interaction:" + request.OwnerKind, request.Prompt, ct);
        return await store.CreateAsync(request with { PartitionKey = PartitionKey }, ct);
    }
    public Task<InteractionRequestSnapshot?> GetAsync(string interactionId, CancellationToken ct = default)
        => store.GetAsync(PartitionKey, interactionId, ct);
    public Task<InteractionRequestSnapshot?> GetByCallerIdAsync(string callerRequestId, CancellationToken ct = default)
        => store.GetByCallerIdAsync(PartitionKey, callerRequestId, ct);
    public Task<IReadOnlyList<InteractionRequestSnapshot>> ListPendingAsync(InteractionRequestQuery? query = null, CancellationToken ct = default)
        => store.ListPendingAsync(PartitionKey, query, ct);
    public Task<InteractionRequestPage> ReadChangesAsync(InteractionRequestCursor after, int take = 200, CancellationToken ct = default)
        => store.ReadChangesAsync(PartitionKey, after, take, ct);
    public Task<InteractionRequestSnapshot> CompleteAsync(string interactionId, InteractionRequestCompletion completion, CancellationToken ct = default)
        => store.CompleteAsync(PartitionKey, interactionId, completion, ct);
}
