using SwarmCore.Agent.Runtime;
namespace SwarmCore.Coding.Routing;

/// <summary>Execution request construction and scope predicate; evidence uses the same narrowed inputs.</summary>
public static class CodingLaneRequestFactory
{
    public static bool AllowsNativeScope(CodingTurnRequest request)
        => (request.CommunicationScope ?? AgentCommunicationScope.None).UserInitiated
           && !request.UnattendedAgentTurn && request.AllowNativeLanes;
    public static AgentTurnRequest CreateEngine(CodingTurnRequest request, CodingLane lane,
        string root, AgentCommunicationScope scopeAuthority, Guid profileId, string message,
        bool continuation, CodingExecutionLimits limits, long? reportedTokens,
        IReadOnlyList<TurnImageAttachment>? images = null, IReadOnlyList<TurnFileAttachment>? files = null,
        IReadOnlyDictionary<string,string>? metadata = null)
        => new AgentTurnRequest
                    {
                        ExecutionLimits = new AgentTurnExecutionLimits(limits.MaxIterations, limits.Timeout,
                            (int)Math.Clamp(limits.MaxCumulativeTokens > 0 ? limits.MaxCumulativeTokens - (reportedTokens ?? 0) : 0, 0, int.MaxValue)),
                        IsContinuation = continuation,
                        ConversationId = request.ConversationId,
                        UserMessage = request.Attachments.Count == 0 ? message : message + "\n\n" + Files.CodingAttachments.EvidenceBoundary,
                        AuthenticatedOriginalUserMessage = request.AuthenticatedOriginalUserMessage
                            ?? (scopeAuthority.UserInitiated ? request.UserMessage : null),
                        Images = images,
                        Files = files,
                        CommunicationScope = scopeAuthority,
                        ProfileIdOverride = profileId,
                        ProviderOverride = lane.Provider,
                        ModelOverride = SwarmCore.Abstractions.LlmRouting.ModelReasoningSelection.Split(lane.Model).Model,
                        ReasoningEffort = SwarmCore.Abstractions.LlmRouting.ModelReasoningSelection.Split(lane.Model).Effort,
                        // SwarmCode owns visible retries and terminal refusals. An inner router
                        // must not switch models behind those host-level rules.
                        DisableRouteFallback = request.SelectionPinned || request.StopOnRefusal || request.DisableRouteFallback,
                        CompactWorkingContextFirst = request.CompactWorkingContextFirst,
                        // Tools resolve and fence paths to this conversation's repository for this turn only.
                        WorkspaceRoot = root,
                        ValidateWorkingContextSourceCallsAsync = request.ValidateWorkingContextSourceCallsAsync,
                        Metadata = metadata,
                        UnattendedAgentTurn = request.UnattendedAgentTurn,
                        CurrentView = request.CurrentView,
                        RoutingDemand = request.RoutingDemand,
                    };
}
