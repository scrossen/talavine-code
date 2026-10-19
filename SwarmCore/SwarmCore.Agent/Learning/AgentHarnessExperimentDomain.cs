using SwarmCore.Agent.Runtime;
using SwarmCore.HarnessLearning;

namespace SwarmCore.Agent.Learning;

/// <summary>
/// Adapter for Talavine/CRM assistant, preparation, extraction or other agent workloads. The host supplies
/// an isolated profile/session, the candidate's context configuration, current authority and a verifier.
/// Existing procedure learning and durable AgentTurnRunner execution remain authoritative.
/// </summary>
public interface IAgentHarnessExperimentTrial : IAsyncDisposable
{
    IAgentTurnRunner Runner { get; }
    AgentTurnRequest Request { get; }
    Task<HarnessTrialObservation> VerifyAsync(CancellationToken ct);
}

public sealed class AgentHarnessExperimentDomain(
    Func<HarnessTrialRequest, CancellationToken, Task<IAgentHarnessExperimentTrial>> openTrial) : IHarnessExperimentDomain
{
    public async Task<HarnessTrialObservation> RunAsync(HarnessTrialRequest request, CancellationToken ct)
    {
        await using var trial = await openTrial(request, ct).ConfigureAwait(false);
        var turn = trial.Request;
        if (string.IsNullOrWhiteSpace(turn.ProviderOverride) || string.IsNullOrWhiteSpace(turn.ModelOverride)
            || !turn.DisableRouteFallback || turn.ExecutionLimits is not { MaxCumulativeTokens: > 0 } limits
            || limits.MaxCumulativeTokens > request.TokenLimit)
            throw new InvalidOperationException("Agent experiments require an exact route and bounded execution.");
        long? tokens = null;
        var tools = 0;
        var complete = false;
        var failed = false;
        var route = true;
        await foreach (var evt in trial.Runner.RunAsync(turn, ct).ConfigureAwait(false))
        {
            switch (evt)
            {
                case TurnUsageReported usage:
                    route &= usage.Provider == turn.ProviderOverride && usage.Model == turn.ModelOverride;
                    if (usage.InputTokens < 0 || usage.OutputTokens < 0) throw new InvalidDataException("Negative usage receipt.");
                    tokens = checked((tokens ?? 0) + usage.InputTokens + usage.OutputTokens);
                    break;
                case ToolCallCompleted: tools++; break;
                case TurnCompleted: complete = true; break;
                case TurnErrored or TurnAbandoned: failed = true; break;
            }
        }
        var verified = await trial.VerifyAsync(ct).ConfigureAwait(false);
        return verified with { Score = complete && !failed ? verified.Score : 0,
            InvariantsPassed = route && verified.InvariantsPassed, Tokens = tokens, ToolCalls = tools };
    }
}
