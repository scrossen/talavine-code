using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.ProviderPacks;
using SwarmCore.LlmRouting.Providers;

namespace SwarmCore.LlmRouting.Services;

/// <summary>Scout-owned model identity and preference matching. Exact connection pins remain separate.</summary>
public static class ModelScoutModelRequests
{
    public static string ModelIdentity(string provider, string model)
    {
        var identity = model.Trim();
        if (!ProviderPackCatalog.TryGet(provider, out var pack)) return identity;
        if (pack!.SubscriptionCli is { } cli)
        {
            var prefix = pack.ModelPrefix + ":";
            if (identity.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) identity = identity[prefix.Length..];
            var at = identity.LastIndexOf('@');
            if (at > 0 && cli.ReasoningEfforts.Contains(identity[(at + 1)..], StringComparer.OrdinalIgnoreCase))
                identity = identity[..at];
            // An unresolved CLI default must never masquerade as a requested model.
            if (identity.Equals("default", StringComparison.OrdinalIgnoreCase)) return model;
        }
        else if (pack.OpenRouter is not null)
        {
            var slash = identity.IndexOf('/');
            if (slash > 0 && ProviderPackCatalog.TryGet(identity[..slash], out var foundation)
                && foundation!.DirectApi is not null)
                identity = identity[(slash + 1)..];
        }
        return identity;
    }

    public static int PreferenceIndex(LlmModelRequest? request, string provider, string model)
    {
        if (request is null) return int.MaxValue;
        var identity = ModelIdentity(provider, model);
        for (var i = 0; i < request.PreferredModels.Count; i++)
            if (request.PreferredModels[i].Trim().Equals(identity, StringComparison.OrdinalIgnoreCase)
                || request.PreferredModels[i].Trim().Equals(model, StringComparison.OrdinalIgnoreCase)) return i;
        return int.MaxValue;
    }

    public static string? Rejection(LlmModelRequest? request, string provider, string model,
        bool? tools = null, bool? vision = null, bool? structured = null)
    {
        if (request is null) return null;
        if (request.RequirePreferredModel && PreferenceIndex(request, provider, model) == int.MaxValue)
            return "outside the required model list";
        foreach (var capability in request.RequiredCapabilities)
        {
            var supported = capability switch
            {
                LlmRequiredCapability.ToolCalling => tools == true,
                LlmRequiredCapability.Vision => vision == true,
                LlmRequiredCapability.StructuredOutput => structured == true,
                _ => ModelRouteCapabilities.Supports(provider, model, capability)
            };
            if (!supported) return $"required capability {capability} is not supported by this connection";
        }
        return null;
    }

    public static int ConnectionOrder(ModelScoutRecommendationRequest request, ModelScoutCandidate candidate)
    {
        if (PreferenceIndex(request.ModelRequest, candidate.Provider, candidate.Model) == int.MaxValue) return 0;
        return request.PreferredModelConnection switch
        {
            ModelScoutConnectionPreference.SubscriptionFirst => candidate.IsSubscription ? 0 : 1,
            ModelScoutConnectionPreference.ApiFirst => !candidate.IsSubscription && !candidate.IsLocal ? 0 : 1,
            _ => 0
        };
    }

    public static LlmModelRequestUnavailableException Unavailable(string functionType, LlmModelRequest request,
        ModelScoutRecommendationDecision? decision, string reason = "no available, admitted connection",
        IReadOnlyList<ModelRouteRejection>? executionRejections = null)
    {
        var reasons = decision?.Candidates
            .Where(c => request.PreferredModels.Count == 0 || PreferenceIndex(request, c.Candidate.Provider, c.Candidate.Model) != int.MaxValue)
            .Select(c => new ModelRouteRejection(c.Candidate.Provider, c.Candidate.Model, c.RejectionReason
                ?? executionRejections?.LastOrDefault(r => r.Provider == c.Candidate.Provider && r.Model == c.Candidate.Model)?.Reason
                ?? reason)).ToList() ?? [];
        foreach (var model in request.PreferredModels)
            if (decision?.Candidates.Any(c => PreferenceIndex(request with { PreferredModels = [model] }, c.Candidate.Provider, c.Candidate.Model) == 0) != true)
                reasons.Add(new(null, model, "not found in the available connection inventory; check the provider connection and model access"));
        if (reasons.Count == 0) reasons.Add(new(null, "requested capabilities", reason));
        return new(functionType, request, reasons);
    }
}
