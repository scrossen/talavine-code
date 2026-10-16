using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.Configuration;

namespace SwarmCore.Chat;

/// <summary>Default <see cref="IChatModelPickerService"/> over <see cref="ILlmRouter"/>.</summary>
public sealed class ChatModelPickerService : IChatModelPickerService
{
    private readonly ILlmRouter _router;
    private readonly ILlmSettingsProvider _settings;
    private readonly ILlmCapabilitiesService _capabilities;
    private readonly ILlmProviderRegistry? _providers;
    private readonly ILlmApiKeyProvider? _apiKeys;

    public ChatModelPickerService(ILlmRouter router, ILlmSettingsProvider settings, ILlmCapabilitiesService capabilities,
        ILlmProviderRegistry? providers = null, ILlmApiKeyProvider? apiKeys = null)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _providers = providers;
        _apiKeys = apiKeys;
    }

    public async Task<ChatModelChoice> ResolveAsync(
        string functionType,
        string seedPrompt,
        bool preferVision,
        bool requiresStructuredOutput = false,
        bool allowTextJsonContractRepair = false,
        CancellationToken ct = default)
    {
        var existing = await _router.GetFunctionPreferenceAsync(functionType).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        // A constraint-free picker can display the cached route directly. A typed
        // or vision lane must ask the router to re-apply the SAME hard envelope the
        // real call will use; otherwise the picker can persist "recommended" state
        // that execution rejects one click later.
        if (existing is not null && !preferVision && !requiresStructuredOutput)
            return new ChatModelChoice(existing.Provider, existing.Model, _capabilities.SupportsVision(existing.Provider, existing.Model));

        var selection = await _router.SelectOptimalModelAsync(
            functionType,
            seedPrompt,
            SelectionOptions(
                preferVision,
                requiresStructuredOutput,
                allowTextJsonContractRepair,
                ct)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        // Seed it as the new "last choice" so the next open of this surface is instant
        // (and so the user sees what got picked, in Settings, like any other function preference).
        // Caching the automatically selected route is an optimization, not a prerequisite for
        // answering. A transient settings acknowledgement failure must not discard a valid model
        // selection or fail an autonomous cycle; the next resolve can select again.
        try
        {
            await _router.SetFunctionPreferenceAsync(
                functionType, selection.Provider, selection.Model,
                selection.FallbackProvider, selection.FallbackModel, userId: null, requiresVision: preferVision).ConfigureAwait(false);
        }
        catch
        {
            // Deliberately continue with the already selected route.
        }

        return new ChatModelChoice(selection.Provider, selection.Model, _capabilities.SupportsVision(selection.Provider, selection.Model));
    }

    public async Task SetChoiceAsync(
        string functionType,
        string provider,
        string model,
        bool requiresVision = false,
        bool requiresStructuredOutput = false,
        bool allowTextJsonContractRepair = false,
        CancellationToken ct = default)
    {
        if (requiresVision || requiresStructuredOutput)
        {
            // Validate the exact candidate before it becomes a durable pin. This is
            // selection-only: no inference/provider request is made. Exact route-kind
            // identity is preserved, so an API route can never stand in for its CLI
            // sibling (or vice versa).
            var validated = await _router.SelectOptimalModelAsync(
                functionType,
                "Validate the selected model for this surface.",
                SelectionOptions(
                    requiresVision,
                    requiresStructuredOutput,
                    allowTextJsonContractRepair,
                    ct,
                    provider,
                    model)).ConfigureAwait(false);
            if (!string.Equals(validated.Provider, provider, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(validated.Model, model, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The selected model route for '{functionType}' did not satisfy this surface's execution requirements.");
            }
        }

        var persisted = await _router.SetFunctionPreferenceAsync(
            functionType,
            provider,
            model,
            userId: LlmPreferenceAuthors.User,
            requiresVision: _capabilities.SupportsVision(provider, model)).ConfigureAwait(false);
        if (!persisted)
            throw new InvalidOperationException(
                $"The settings provider refused to save the user-selected model route for '{functionType}'.");
    }

    private static LlmRouterOptions SelectionOptions(
        bool requiresVision,
        bool requiresStructuredOutput,
        bool allowTextJsonContractRepair,
        CancellationToken ct,
        string? provider = null,
        string? model = null) => new()
    {
        RequiresVision = requiresVision,
        RequiresStructuredOutput = requiresStructuredOutput,
        AllowTextJsonContractRepair = allowTextJsonContractRepair,
        RequestCancellationToken = ct,
        OverrideProvider = provider,
        OverrideModel = model,
        OverrideProvenance = provider is null || model is null
            ? LlmOverrideProvenance.Unspecified
            : LlmOverrideProvenance.User,
        DisableFallback = provider is not null && model is not null,
    };

    public async Task<IReadOnlyList<ChatModelCandidate>> ListCandidatesAsync(string functionType, CancellationToken ct = default)
    {
        var current = await _router.GetFunctionPreferenceAsync(functionType).ConfigureAwait(false);
        var providers = await _settings.GetAllProviderSettingsAsync().ConfigureAwait(false);
        var candidates = new List<ChatModelCandidate>();

        foreach (var p in providers.Where(p => p.IsEnabled))
        {
            ct.ThrowIfCancellationRequested();
            IEnumerable<string> models = string.IsNullOrWhiteSpace(p.DefaultModel) ? [] : [p.DefaultModel!];
            var registered = _providers?.GetProvider(p.ProviderName);
            if (_providers is not null && registered is null) continue; // this host cannot run it
            if (registered is ICredentialFreeLlmProvider)
            {
                // Compute nodes and an operator's model server: offer what they serve right now.
                models = await ListServedModelsAsync(registered, ct).ConfigureAwait(false);
            }
            else if (_apiKeys is not null && !p.IsLocalProvider
                     && !SubscriptionCliProviderNames.Contains(p.ProviderName)
                     && !await _apiKeys.HasApiKeyAsync(p.ProviderName).ConfigureAwait(false))
            {
                continue; // a key this workspace does not have; choosing it could only fail
            }

            foreach (var model in models.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var isCurrent = current is not null
                    && string.Equals(current.Provider, p.ProviderName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(current.Model, model, StringComparison.OrdinalIgnoreCase);
                candidates.Add(new ChatModelCandidate(p.ProviderName, model, _capabilities.SupportsVision(p.ProviderName, model), isCurrent));
            }
        }

        return candidates;
    }

    // Signed-in subscription CLIs authenticate themselves; they have no API key to look for.
    private static readonly HashSet<string> SubscriptionCliProviderNames = new(StringComparer.OrdinalIgnoreCase) { "ClaudeCode", "CodexCli", "GeminiCli" };

    private static async Task<IReadOnlyList<string>> ListServedModelsAsync(ILlmProvider provider, CancellationToken ct)
    {
        try
        {
            var listing = provider.GetAvailableModelsAsync();
            return await Task.WhenAny(listing, Task.Delay(TimeSpan.FromSeconds(10), ct)).ConfigureAwait(false) == listing
                ? await listing.ConfigureAwait(false)
                : [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return []; }
    }
}
