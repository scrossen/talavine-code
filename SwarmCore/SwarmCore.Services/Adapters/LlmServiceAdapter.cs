using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Services.Abstractions;

namespace SwarmCore.Services.Adapters;

/// <summary>
/// Invokes an LLM service by delegating to the host's <see cref="ILlmRouter"/> —
/// so an LLM is "just a service" in the catalog, but the smart routing (privacy,
/// fallback, cost, tool protocols) stays where it belongs. Config keys:
/// functionType, provider, model, system (optional system-prompt prefix),
/// localOnly (true/false). The call arg "prompt" (or "input") is the user text.
/// </summary>
public sealed class LlmServiceAdapter : IServiceKindAdapter
{
    private readonly ILlmRouter _router;

    public LlmServiceAdapter(ILlmRouter router) => _router = router;

    public ServiceKind Kind => ServiceKind.Llm;

    public async Task<ServiceCallResult> InvokeAsync(ServiceDefinition s, IReadOnlyDictionary<string, object?> args, CancellationToken ct = default)
    {
        var prompt = (args.TryGetValue("prompt", out var p) ? p?.ToString() : null)
            ?? (args.TryGetValue("input", out var i) ? i?.ToString() : null)
            ?? string.Join("\n", args.Select(kv => $"{kv.Key}: {kv.Value}"));
        if (string.IsNullOrWhiteSpace(prompt)) return ServiceCallResult.Fail("No prompt/input given.");

        var system = s.Config.GetValueOrDefault("system");
        if (!string.IsNullOrWhiteSpace(system)) prompt = system + "\n\n" + prompt;

        var functionType = s.Config.GetValueOrDefault("functionType", "service-llm")!;
        var options = new LlmRouterOptions
        {
            Interactivity = LlmInteractivity.UserWaiting,
            OverrideProvider = s.Config.GetValueOrDefault("provider"),
            OverrideModel = s.Config.GetValueOrDefault("model"),
            PrivacyClass = string.Equals(s.Config.GetValueOrDefault("localOnly"), "true", StringComparison.OrdinalIgnoreCase)
                ? LlmPrivacyClass.LocalOnly : LlmPrivacyClass.Unspecified,
        };

        try
        {
            var text = await _router.GenerateResponseAsync(functionType, prompt, options).ConfigureAwait(false);
            return ServiceCallResult.Success(text);
        }
        catch (Exception ex)
        {
            return ServiceCallResult.Fail("LLM call failed: " + ex.Message);
        }
    }
}
