using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.Agent.Continual;

/// <summary>Trusted host proof authority, never an agent tool or caller-supplied route hint.</summary>
public interface IContinualAcceptanceRouteAuthority
{
    /// <summary>Explicit trusted host audit lane; default preserves ordinary legacy routing.</summary>
    string IndependentAuditFunctionType => FunctionTypes.AgentChat;
    Task<LlmRouterOptions> AuthorizeAsync(ContinualAcceptanceRequest request, string functionType,
        string prompt, LlmRouterOptions options, CancellationToken ct);
}

public static class ContinualAcceptanceRoutePolicy
{
    public static void RequireExactPin(ContinualAcceptanceRequest request, LlmRouterOptions options)
    {
        if (request.RequireExactRoute && (options.OverrideProvider != request.Provider || options.OverrideModel != request.Model
            || !options.DisableFallback || options.RequiresToolUse))
            throw new UnauthorizedAccessException("Independent auditing must retain the exact host pin without fallback or tool authority.");
    }
}
