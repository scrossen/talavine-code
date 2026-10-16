using SwarmCore.Agent.ToolUseEngine;

namespace SwarmCore.Agent.Runtime;

/// <summary>
/// Pure translator: engine <see cref="AgentChatEvent"/> → UI-facing
/// <see cref="TurnEvent"/>. Stateless, trivially unit-testable.
/// </summary>
public sealed class TurnEventMapper : ITurnEventMapper
{
    public IEnumerable<TurnEvent> Map(AgentChatEvent engineEvent, TurnMappingContext context)
    {
        switch (engineEvent)
        {
            case UsageReportedEvent usage:
                yield return new TurnUsageReported(context.TurnId, usage.Provider, usage.Model, usage.InputTokens,
                    usage.OutputTokens, usage.CachedInputTokens);
                break;
            case UserMessageAppendedEvent:
                // The UI already knows what the user typed; no event.
                yield break;

            case AssistantTextEvent text:
                yield return new TextChunk(context.TurnId, text.Text);
                break;

            case AgentStatusEvent status:
                yield return new Thinking(context.TurnId, status.Text.Trim());
                break;

            case AssistantReasoningEvent reasoning:
                // Routed to its own UI event so the chat surface can collect
                // chain of thought into a collapsible "Thinking" panel
                // separate from the visible answer.
                yield return new ReasoningChunk(context.TurnId, reasoning.Text);
                break;

            case ToolCallStartedEvent start:
                yield return new ToolCallStarted(
                    TurnId: context.TurnId,
                    ToolCallId: start.Id,
                    ToolName: start.Name,
                    // A provider can hand back a call with no input object at
                    // all; the event must still map rather than fault the turn.
                    Input: start.Input?.DeepClone() as System.Text.Json.Nodes.JsonObject
                           ?? new System.Text.Json.Nodes.JsonObject());
                break;

            case ToolCallCompletedEvent completed:
                yield return new ToolCallCompleted(
                    TurnId: context.TurnId,
                    ToolCallId: completed.Id,
                    ToolName: completed.Name,
                    Result: completed.Result,
                    IsError: completed.IsError,
                    // New engines carry the exact per-call duration. Retain
                    // the turn-elapsed fallback for third-party/older event
                    // producers that have not populated it yet.
                    Elapsed: completed.Elapsed ?? DateTime.UtcNow - context.TurnStartUtc)
                { WasDispatched = completed.WasDispatched, LocalProcessCompleted = completed.LocalProcessCompleted };
                break;

            case SteeringDeliveryChangedEvent changed:
                yield return new TurnSteeringDeliveryChanged(context.TurnId, changed.Receipt.Id,
                    changed.Receipt.Delivery.ToString(), changed.Receipt.ProviderSteeringId,
                    changed.Receipt.ResponseId, changed.Receipt.Reason);
                break;

            case SteeringAppliedEvent steering:
                yield return new TurnSteeringApplied(
                    context.TurnId,
                    steering.SteeringId);
                break;

            case AdaptiveReasoningChangedEvent adaptive:
                yield return new AdaptiveReasoningChanged(context.TurnId, adaptive.DecisionId, adaptive.Action, adaptive.Outcome, adaptive.Reason,
                    adaptive.PreviousProvider, adaptive.PreviousModel, adaptive.Provider, adaptive.Model, adaptive.Effort);
                yield break;
            case WorkingContextContinuationEvent continuation:
                yield return new WorkingContextContinuation(context.TurnId, continuation.Generation, continuation.Mode, continuation.Reason);
                yield break;
            case WorkingContextCompactionStartedEvent compaction:
                yield return new WorkingContextCompactionStarted(
                    context.TurnId,
                    compaction.Reason,
                    compaction.OriginalCharacters);
                break;

            case WorkingContextCompactionCompletedEvent compaction:
                yield return new WorkingContextCompactionCompleted(
                    context.TurnId,
                    compaction.Reason,
                    compaction.OriginalCharacters,
                    compaction.CompactedCharacters,
                    compaction.CompactedMessageCount,
                    compaction.UsedDeterministicFallback,
                    compaction.Applied);
                break;

            case FinishedEvent finished:
                yield return new TurnCompleted(
                    TurnId: context.TurnId,
                    FinalText: finished.FinalText,
                    Duration: DateTime.UtcNow - context.TurnStartUtc);
                break;

            case ErroredEvent errored:
                yield return new TurnErrored(context.TurnId, errored.Message) { FailureCategory = errored.FailureCategory };
                break;

            default:
                yield break;
        }
    }
}
