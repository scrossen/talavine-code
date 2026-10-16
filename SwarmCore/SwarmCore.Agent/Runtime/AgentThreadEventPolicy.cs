using System.Text.Json;
using System.Text.Json.Nodes;

namespace SwarmCore.Agent.Runtime;

/// <summary>One shared durable replay contract. Hosts own transactions, scope, sequence and deletion evidence.</summary>
public static class AgentThreadEventPolicy
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static AgentThreadItemKind GetKind(TurnEvent value) => value switch
    {
        TurnStarted => AgentThreadItemKind.TurnStarted, TextChunk => AgentThreadItemKind.TextChunk,
        ReasoningChunk => AgentThreadItemKind.ReasoningActivity, ToolCallStarted => AgentThreadItemKind.ToolCallStarted,
        ToolCallInputDelta => AgentThreadItemKind.ToolCallInputDelta, ToolCallCompleted => AgentThreadItemKind.ToolCallCompleted,
        Thinking => AgentThreadItemKind.Thinking, PermissionRequested => AgentThreadItemKind.PermissionRequested,
        TurnSteeringAccepted => AgentThreadItemKind.SteeringAccepted, TurnSteeringDeliveryChanged => AgentThreadItemKind.SteeringDeliveryChanged,
        WorkingContextContinuation => AgentThreadItemKind.WorkingContextContinuation, AdaptiveReasoningChanged => AgentThreadItemKind.AdaptiveReasoningChanged,
        TurnUsageReported => AgentThreadItemKind.UsageReported, TurnSteeringApplied => AgentThreadItemKind.SteeringApplied,
        TurnSteeringDeferred => AgentThreadItemKind.SteeringDeferred, WorkingContextCompactionStarted => AgentThreadItemKind.WorkingContextCompactionStarted,
        WorkingContextCompactionCompleted => AgentThreadItemKind.WorkingContextCompactionCompleted, TurnCompleted => AgentThreadItemKind.TurnCompleted,
        TurnErrored => AgentThreadItemKind.TurnErrored, TurnAbandoned => AgentThreadItemKind.TurnAbandoned,
        _ => throw new NotSupportedException($"Turn event type '{value.GetType().FullName}' has no durable item mapping."),
    };

    public static TurnEvent Normalize(TurnEvent value) => value switch
    {
        ReasoningChunk reasoning => new Thinking(reasoning.TurnId),
        ToolCallStarted started => started with { Input = new JsonObject() },
        ToolCallInputDelta delta => delta with { JsonFragment = string.Empty },
        ToolCallCompleted completed => completed with { Result = string.Empty },
        TurnErrored errored => errored with { UnverifiedFinalText = null },
        PermissionRequested permission => permission with { FunctionName = string.Empty,
            Permissions = permission.Permissions.ToArray(), PermissionLevels = permission.PermissionLevels.ToArray() },
        _ => value,
    };

    public static string Serialize(TurnEvent value) => JsonSerializer.Serialize(value, value.GetType(), Json);
    /// <summary>Freeze all caller-owned collections before the first persistence await.</summary>
    public static TurnEvent Freeze(TurnEvent value) => Deserialize(GetKind(value), Serialize(Normalize(value)));
    public static bool Equivalent(TurnEvent left, TurnEvent right) => left.GetType() == right.GetType()
        && string.Equals(Serialize(left), Serialize(right), StringComparison.Ordinal);

    public static TurnEvent Deserialize(AgentThreadItemKind kind, string json) => kind switch
    {
        AgentThreadItemKind.TurnStarted => Read<TurnStarted>(json), AgentThreadItemKind.TextChunk => Read<TextChunk>(json),
        AgentThreadItemKind.ReasoningActivity or AgentThreadItemKind.Thinking => Read<Thinking>(json),
        AgentThreadItemKind.ToolCallStarted => Read<ToolCallStarted>(json), AgentThreadItemKind.ToolCallInputDelta => Read<ToolCallInputDelta>(json),
        AgentThreadItemKind.ToolCallCompleted => Read<ToolCallCompleted>(json), AgentThreadItemKind.PermissionRequested => Read<PermissionRequested>(json),
        AgentThreadItemKind.SteeringAccepted => Read<TurnSteeringAccepted>(json), AgentThreadItemKind.SteeringDeliveryChanged => Read<TurnSteeringDeliveryChanged>(json),
        AgentThreadItemKind.WorkingContextContinuation => Read<WorkingContextContinuation>(json), AgentThreadItemKind.AdaptiveReasoningChanged => Read<AdaptiveReasoningChanged>(json),
        AgentThreadItemKind.UsageReported => Read<TurnUsageReported>(json), AgentThreadItemKind.SteeringApplied => Read<TurnSteeringApplied>(json),
        AgentThreadItemKind.SteeringDeferred => Read<TurnSteeringDeferred>(json), AgentThreadItemKind.WorkingContextCompactionStarted => Read<WorkingContextCompactionStarted>(json),
        AgentThreadItemKind.WorkingContextCompactionCompleted => Read<WorkingContextCompactionCompleted>(json), AgentThreadItemKind.TurnCompleted => Read<TurnCompleted>(json),
        AgentThreadItemKind.TurnErrored => Read<TurnErrored>(json), AgentThreadItemKind.TurnAbandoned => Read<TurnAbandoned>(json),
        _ => throw new InvalidOperationException($"Unknown durable agent-thread item kind '{kind}'."),
    };
    private static T Read<T>(string json) where T : TurnEvent => JsonSerializer.Deserialize<T>(json, Json)
        ?? throw new InvalidOperationException($"Stored agent-thread item could not be read as {typeof(T).Name}.");

    public static void ValidateAppend(AgentThreadTurnSnapshot? existing, Guid turnId, TurnEvent value)
    {
        if (existing is null)
        {
            if (value is not TurnStarted) throw new InvalidOperationException($"Durable turn '{turnId}' must begin with a TurnStarted event.");
            return;
        }
        if (!string.Equals(existing.StreamTurnId, value.TurnId, StringComparison.Ordinal))
            throw new InvalidOperationException($"Durable turn '{turnId}' is already bound to stream turn '{existing.StreamTurnId}'.");
        if (value is TurnStarted && existing.FirstSequence != 0)
            throw new InvalidOperationException($"Durable turn '{turnId}' already has a start event.");
        if (existing.Status != AgentThreadTurnStatus.Running)
            throw new InvalidOperationException($"Cannot append '{GetKind(value)}' after durable turn '{turnId}' reached '{existing.Status}'.");
    }
    public static AgentThreadTurnStatus StatusAfter(TurnEvent value) => value switch
    {
        TurnCompleted => AgentThreadTurnStatus.Completed, TurnErrored => AgentThreadTurnStatus.Failed,
        TurnAbandoned => AgentThreadTurnStatus.Abandoned, _ => AgentThreadTurnStatus.Running,
    };
}
