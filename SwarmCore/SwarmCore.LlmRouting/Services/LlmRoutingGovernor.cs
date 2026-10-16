using Microsoft.Extensions.Logging;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting.Services;

/// <summary>
/// Default route-advisor coordinator. It makes advisor precedence explicit while
/// leaving the router responsible for hard validation such as privacy and
/// provider availability.
/// </summary>
public sealed class LlmRoutingGovernor : ILlmRoutingGovernor
{
    private readonly IReadOnlyList<AdvisorEntry> _advisors;
    private readonly ILogger<LlmRoutingGovernor> _logger;

    public LlmRoutingGovernor(
        IEnumerable<ILlmRouteAdvisor> advisors,
        ILogger<LlmRoutingGovernor> logger)
    {
        _advisors = advisors
            .Select((advisor, index) => new AdvisorEntry(advisor, index))
            .OrderBy(entry => entry.Priority)
            .ThenBy(entry => entry.RegistrationIndex)
            .ToList();
        _logger = logger;
    }

    public async Task<LlmRoutingGovernorDecision> GetRecommendationAsync(
        LlmRouteAdvisorContext context,
        CancellationToken cancellationToken = default)
    {
        if (_advisors.Count == 0)
        {
            return LlmRoutingGovernorDecision.None();
        }

        var evaluations = new List<LlmRouteAdvisorEvaluation>(_advisors.Count);
        var candidates = new List<LlmRoutingGovernorCandidate>();

        foreach (var entry in _advisors)
        {
            cancellationToken.ThrowIfCancellationRequested();

            LlmRouteAdvisorRecommendation? recommendation;
            try
            {
                recommendation = await LlmRouteAdvisorPolicy.EvaluateAsync(entry.Advisor, context, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (LlmRouteSelectionBlockedException)
            {
                // A safety-floor policy has positively matched this call and
                // rejected every route. Falling through to a lower-priority
                // advisor would erase the very constraint it exists to enforce.
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (entry.Advisor is ILlmHardSafetyRouteAdvisor)
            {
                // A hard-safety advisor is a non-negotiable admission gate. If
                // its dependencies fail, routing cannot safely assume that the
                // call would have passed and continue to a weaker policy lane.
                throw new LlmRouteSelectionBlockedException(
                    $"Hard safety policy {entry.Name} could not validate {context.FunctionType}; weaker routing was blocked.",
                    ex);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "Route advisor {Advisor} failed for {FunctionType}; continuing normal routing",
                    entry.Name,
                    context.FunctionType);

                evaluations.Add(new LlmRouteAdvisorEvaluation
                {
                    AdvisorName = entry.Name,
                    PolicyKind = entry.PolicyKind,
                    Priority = entry.Priority,
                    Recommended = false,
                    ErrorMessage = ex.Message
                });
                continue;
            }

            if (recommendation is null)
            {
                evaluations.Add(new LlmRouteAdvisorEvaluation
                {
                    AdvisorName = entry.Name,
                    PolicyKind = entry.PolicyKind,
                    Priority = entry.Priority,
                    Recommended = false
                });
                continue;
            }

            evaluations.Add(new LlmRouteAdvisorEvaluation
            {
                AdvisorName = entry.Name,
                PolicyKind = entry.PolicyKind,
                Priority = entry.Priority,
                Recommended = true,
                Provider = recommendation.Provider,
                Model = recommendation.Model,
                PersistPreference = recommendation.PersistPreference,
                Reason = recommendation.Reason
            });

            candidates.Add(new LlmRoutingGovernorCandidate
            {
                Recommendation = recommendation,
                AdvisorName = entry.Name,
                PolicyKind = entry.PolicyKind,
                Priority = entry.Priority,
                IsHardSafetyPolicy = entry.Advisor is ILlmHardSafetyRouteAdvisor,
            });
        }

        if (candidates.Count == 0)
        {
            return LlmRoutingGovernorDecision.None(evaluations);
        }

        var selected = candidates[0];
        return new LlmRoutingGovernorDecision
        {
            Recommendation = selected.Recommendation,
            AdvisorName = selected.AdvisorName,
            PolicyKind = selected.PolicyKind,
            Priority = selected.Priority,
            Candidates = candidates,
            Evaluations = evaluations
        };
    }

    private sealed class AdvisorEntry
    {
        public AdvisorEntry(ILlmRouteAdvisor advisor, int registrationIndex)
        {
            Advisor = advisor;
            RegistrationIndex = registrationIndex;

            if (advisor is ILlmRoutePolicyAdvisor policyAdvisor)
            {
                Priority = policyAdvisor.Priority;
                PolicyKind = policyAdvisor.PolicyKind;
                Name = string.IsNullOrWhiteSpace(policyAdvisor.PolicyName)
                    ? advisor.GetType().Name
                    : policyAdvisor.PolicyName;
            }
            else
            {
                Priority = LlmRoutePolicyPriorities.General;
                PolicyKind = LlmRoutePolicyKind.General;
                Name = advisor.GetType().Name;
            }
        }

        public ILlmRouteAdvisor Advisor { get; }
        public int RegistrationIndex { get; }
        public int Priority { get; }
        public LlmRoutePolicyKind PolicyKind { get; }
        public string Name { get; }
    }
}
