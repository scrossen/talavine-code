using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Routing;
using SwarmCore.HarnessLearning;

namespace SwarmCore.Coding.Experiments;

/// <summary>Host owns the isolated workspace, current authority, exact route, and independent verifier.</summary>
public interface ICodingHarnessExperimentTrial : IAsyncDisposable
{
    ICodingTurnOrchestrator Orchestrator { get; }
    CodingTurnRequest Request { get; }
    string Provider { get; }
    string Model { get; }
    Task<HarnessTrialObservation> VerifyAsync(CancellationToken ct);
}

/// <summary>
/// Uses the actual shared coding orchestrator, including its guarded tools and durable turn boundary.
/// Hosts may reuse this adapter for code or file-based preparation tasks. No model response is a verifier.
/// </summary>
public sealed class CodingHarnessExperimentDomain(
    Func<HarnessTrialRequest, CancellationToken, Task<ICodingHarnessExperimentTrial>> openTrial) : IHarnessExperimentDomain
{
    public async Task<HarnessTrialObservation> RunAsync(HarnessTrialRequest request, CancellationToken ct)
    {
        await using var trial = await openTrial(request, ct).ConfigureAwait(false);
        if (trial.Request.RememberModelResponses || !trial.Request.SelectionPinned
            || trial.Request.AllowHandOff != false || !trial.Request.DisableRouteFallback
            || trial.Request.ExecutionLimits is not { MaxCumulativeTokens: > 0 } limits
            || limits.MaxCumulativeTokens > request.TokenLimit)
            throw new InvalidOperationException("Experiments require private fresh turns, a pinned route and a physical token ceiling.");
        long? tokens = null;
        var tools = 0;
        var complete = false;
        var failed = false;
        var exactRoute = true;
        await foreach (var evt in trial.Orchestrator.RunAsync(trial.Request, ct).ConfigureAwait(false))
        {
            switch (evt)
            {
                case TurnUsageReported usage:
                    exactRoute &= usage.Provider == trial.Provider && usage.Model == trial.Model;
                    if (usage.InputTokens < 0 || usage.OutputTokens < 0) throw new InvalidDataException("Negative usage receipt.");
                    tokens = checked((tokens ?? 0) + usage.InputTokens + usage.OutputTokens);
                    break;
                case CodingLaneSelected selected:
                    exactRoute &= selected.Lane.Provider == trial.Provider && selected.Lane.Model == trial.Model;
                    break;
                case ToolCallCompleted: tools++; break;
                case TurnCompleted: complete = true; break;
                case TurnErrored or CodingLanesExhausted or CodingTurnLimitReached or CodingLaneHandOff: failed = true; break;
            }
        }
        var verified = await trial.VerifyAsync(ct).ConfigureAwait(false);
        return verified with
        {
            Score = complete && !failed ? verified.Score : 0,
            InvariantsPassed = exactRoute && verified.InvariantsPassed,
            Tokens = tokens,
            ToolCalls = tools
        };
    }
}
