using Microsoft.Extensions.Logging;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.Configuration;

namespace SwarmCore.LlmRouting.Services;

/// <summary>
/// Default configurator. Walks every known function type and proposes a preference
/// using this rule cascade:
///
///   1. Background + local available → local economy
///   2. User-waiting + subscription CLI healthy + cloud-ok + non-vision → frontier on the CLI
///   3. User-waiting + reasoning-heavy + cloud available → balanced cloud
///   4. Otherwise → economy cloud / current preference (don't churn unconfigured functions)
///
/// Policy suggestions are conservative:
///   - Monthly budget: leave alone if the user already set one; otherwise no proposal
///   - Quiet hours: propose a local-batch overnight window when local is available
///   - Thresholds: use the defaults from <see cref="RoutingPolicy.Default"/>
///
/// Apps can replace this with a richer implementation that consults
/// <see cref="IToolUseCalibrationService"/> for tool-use validation and the
/// message-pipeline calibration catalog for that lane.
/// </summary>
public sealed class DefaultRoutingProfileBuilder : IRoutingProfileBuilder
{
    private readonly ILlmSettingsProvider _settingsProvider;
    private readonly ILlmProviderRegistry _providerRegistry;
    private readonly IRoutingPolicyStore _policyStore;
    private readonly ILlmProviderCooldownTracker? _cooldownTracker;
    private readonly ILogger<DefaultRoutingProfileBuilder> _logger;

    public DefaultRoutingProfileBuilder(
        ILlmSettingsProvider settingsProvider,
        ILlmProviderRegistry providerRegistry,
        IRoutingPolicyStore policyStore,
        ILogger<DefaultRoutingProfileBuilder> logger,
        ILlmProviderCooldownTracker? cooldownTracker = null)
    {
        _settingsProvider = settingsProvider;
        _providerRegistry = providerRegistry;
        _policyStore = policyStore;
        _cooldownTracker = cooldownTracker;
        _logger = logger;
    }

    public async Task<RoutingProfileProposal> BuildAsync(CancellationToken ct = default)
    {
        var notes = new List<string>();
        var rows = new List<RoutingProfileFunctionRow>();

        var allSettings = await _settingsProvider.GetAllProviderSettingsAsync().ConfigureAwait(false);
        var subscription = PickFirstAvailable(allSettings, s => SubscriptionCliProviders.IsSubscriptionCliProvider(s.ProviderName));
        var cloud = PickFirstAvailable(allSettings, s => !LlmProviderLocalityPolicy.IsTrustedLocalProvider(s) && !SubscriptionCliProviders.IsSubscriptionCliProvider(s.ProviderName));
        var local = PickFirstAvailable(allSettings, LlmProviderLocalityPolicy.IsTrustedLocalProvider);

        notes.Add($"Subscription CLI: {(subscription?.ProviderName ?? "none configured")}");
        notes.Add($"Cloud API:        {(cloud?.ProviderName ?? "none configured")}");
        notes.Add($"Local provider:   {(local?.ProviderName ?? "none configured")}");

        var existingPreferences = await _settingsProvider.GetAllFunctionPreferencesAsync().ConfigureAwait(false);
        var existingByType = existingPreferences
            .GroupBy(
                preference => ModelScoutFunctionEvidence.NormalizeFunctionType(preference.FunctionType),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(preference => LlmPreferenceAuthors.IsUserPinned(preference.PinnedByUserAt))
                    .ThenByDescending(preference => preference.UpdatedAt)
                    .First(),
                StringComparer.OrdinalIgnoreCase);

        foreach (var functionType in KnownFunctionTypes())
        {
            ct.ThrowIfCancellationRequested();

            // The generic profile owns ordinary text/agent routes only. MessagePipeline
            // has a prompt/suite-bound plan and embeddings have a vector-space pin;
            // writing either as a generic preference creates a dead split-brain control.
            if (functionType.Equals(FunctionTypes.MessagePipeline, StringComparison.OrdinalIgnoreCase)
                || functionType.Equals(FunctionTypes.TextEmbedding, StringComparison.OrdinalIgnoreCase)
                || functionType.Equals(FunctionTypes.ImageEmbedding, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var profile = FunctionTypeProfiles.Resolve(functionType);
            var existing = existingByType.GetValueOrDefault(functionType);

            // A configurator proposal must not turn a durable manual pin into a
            // routine recommendation. The user can release/change the pin from the
            // model-preference control that created it.
            if (LlmPreferenceAuthors.IsUserPinned(existing?.PinnedByUserAt))
            {
                continue;
            }

            var (provider, model, reason) = ProposeFor(functionType, profile, subscription, cloud, local);
            if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model))
            {
                continue;
            }

            // Don't propose a change that matches the existing pin — it's just noise in the diff.
            if (existing is { IsEnabled: true }
                && string.Equals(existing.PreferredProvider, provider, StringComparison.OrdinalIgnoreCase)
                && string.Equals(existing.PreferredModel, model, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            rows.Add(new RoutingProfileFunctionRow(
                FunctionType: functionType,
                ProposedProvider: provider!,
                ProposedModel: model!,
                ProposedFallbackProvider: PickFallback(provider!, cloud, local)?.ProviderName,
                ProposedFallbackModel: PickFallbackModel(provider!, cloud, local),
                CurrentProvider: existing?.PreferredProvider,
                CurrentModel: existing?.PreferredModel,
                Reason: reason));
        }

        var currentPolicy = await _policyStore.GetAsync(ct).ConfigureAwait(false);
        RoutingProfilePolicyChange? policyChange = null;
        if (currentPolicy.MonthlyCloudBudgetUsd is null && cloud is not null)
        {
            notes.Add("No monthly cloud budget set. Consider setting one in routing policy.");
        }
        if (local is not null
            && (currentPolicy.QuietHoursStart is null || currentPolicy.QuietHoursEnd is null))
        {
            var proposedPolicy = currentPolicy with
            {
                QuietHoursStart = new TimeOnly(22, 0),
                QuietHoursEnd = new TimeOnly(6, 0),
                QuietHoursForceLocal = true,
            };
            policyChange = new RoutingProfilePolicyChange(
                ProposedPolicy: proposedPolicy,
                CurrentPolicy: currentPolicy,
                Reason: "Local provider is available, so background batch work can shift local overnight while interactive calls stay responsive.");
        }

        return new RoutingProfileProposal(
            FunctionRows: rows,
            PolicyChange: policyChange,
            Notes: notes,
            GeneratedAt: DateTimeOffset.UtcNow);
    }

    public async Task<int> ApplyAsync(RoutingProfileProposal proposal, CancellationToken ct = default)
    {
        if (proposal is null) return 0;

        if (proposal.FunctionRows.Count > 0 && proposal.PolicyChange is not null)
        {
            throw new InvalidOperationException(
                "Function routes and routing policy are separate durable controls and must be applied as separate audited operations.");
        }

        var existingByType = new Dictionary<string, LlmFunctionPreference>(StringComparer.OrdinalIgnoreCase);
        if (proposal.FunctionRows.Count > 0)
        {
            var existingPreferences = await _settingsProvider
                .GetAllFunctionPreferencesAsync()
                .ConfigureAwait(false) ?? [];
            existingByType = existingPreferences
                .GroupBy(
                    preference => ModelScoutFunctionEvidence.NormalizeFunctionType(preference.FunctionType),
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .OrderByDescending(preference => LlmPreferenceAuthors.IsUserPinned(preference.PinnedByUserAt))
                        .ThenByDescending(preference => preference.UpdatedAt)
                        .First(),
                    StringComparer.OrdinalIgnoreCase);
        }
        var writes = new List<FunctionPreferenceBatchWrite>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in proposal.FunctionRows)
        {
            ct.ThrowIfCancellationRequested();
            var functionType = ModelScoutFunctionEvidence.NormalizeFunctionType(row.FunctionType);
            if (string.IsNullOrWhiteSpace(functionType))
            {
                continue;
            }

            if (functionType.Equals(FunctionTypes.MessagePipeline, StringComparison.OrdinalIgnoreCase)
                || functionType.Equals(FunctionTypes.TextEmbedding, StringComparison.OrdinalIgnoreCase)
                || functionType.Equals(FunctionTypes.ImageEmbedding, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Routing profile ignored dedicated-control function {FunctionType}",
                    functionType);
                continue;
            }

            if (!seen.Add(functionType))
            {
                throw new InvalidOperationException(
                    $"The routing proposal contains more than one row for canonical function '{functionType}'.");
            }
            if (string.IsNullOrWhiteSpace(row.ProposedProvider)
                || string.IsNullOrWhiteSpace(row.ProposedModel))
            {
                throw new InvalidOperationException(
                    $"The routing proposal for '{functionType}' does not contain an exact provider/model pair.");
            }
            if (string.IsNullOrWhiteSpace(row.ProposedFallbackProvider)
                != string.IsNullOrWhiteSpace(row.ProposedFallbackModel))
            {
                throw new InvalidOperationException(
                    $"The routing proposal fallback for '{functionType}' requires both provider and model, or neither.");
            }

            existingByType.TryGetValue(functionType, out var existing);
            if (LlmPreferenceAuthors.IsUserPinned(existing?.PinnedByUserAt))
            {
                _logger.LogInformation(
                    "Routing profile left {FunctionType} unchanged because it is explicitly pinned by the user",
                    functionType);
                continue;
            }

            var preference = new LlmFunctionPreference
            {
                Id = existing?.Id ?? 0,
                FunctionType = functionType,
                PreferredProvider = row.ProposedProvider,
                PreferredModel = row.ProposedModel,
                FallbackProvider = row.ProposedFallbackProvider,
                FallbackModel = row.ProposedFallbackModel,
                IsEnabled = true,
                RequiresVision = existing?.RequiresVision ?? false,
                CreatedAt = existing?.CreatedAt ?? DateTime.UtcNow,
                CreatedBy = existing?.CreatedBy ?? nameof(DefaultRoutingProfileBuilder),
                UpdatedAt = DateTime.UtcNow,
                UpdatedBy = nameof(DefaultRoutingProfileBuilder),
                AuditCorrelationId = proposal.AuditCorrelationId,
            };

            writes.Add(new FunctionPreferenceBatchWrite(
                preference,
                FunctionPreferenceVersion.Capture(functionType, existing)));
        }

        var functionChanges = 0;
        if (writes.Count > 0)
        {
            var result = await _settingsProvider.TrySetExactFunctionPreferencesAsync(
                    writes,
                    FunctionPreferenceWriteAuthority.RespectUserPin,
                    ct)
                .ConfigureAwait(false);
            if (!result.Applied)
            {
                throw new InvalidOperationException(
                    result.Error
                    ?? $"The atomic routing profile batch was not applied ({result.Status}). Refresh the proposal and retry.");
            }
            functionChanges = result.AppliedCount;
        }

        var policyChanged = 0;
        if (proposal.PolicyChange is { ProposedPolicy: var newPolicy })
        {
            await _policyStore.SetAsync(newPolicy, ct).ConfigureAwait(false);
            policyChanged = 1;
        }

        _logger.LogInformation(
            "Routing profile applied: {FunctionChanges} function preferences updated, policy {PolicyState}",
            functionChanges, policyChanged == 0 ? "unchanged" : "updated");

        return functionChanges + policyChanged;
    }

    private (string? Provider, string? Model, string Reason) ProposeFor(
        string functionType,
        FunctionTypeProfile profile,
        LlmProviderSettings? subscription,
        LlmProviderSettings? cloud,
        LlmProviderSettings? local)
    {
        var requiresVision = ProfileSuggestsVision(functionType);
        var cloudOk = profile.PrivacyClass != LlmPrivacyClass.LocalOnly;

        // Rule 1 — background work should have a cheap stable baseline. Dynamic
        // advisors can still promote it for urgent/backlog scenarios.
        if (profile.Interactivity == LlmInteractivity.Background
            && local is not null && IsEligible(local.ProviderName))
        {
            var model = !string.IsNullOrWhiteSpace(local.DefaultModel)
                ? local.DefaultModel!
                : ModelDefaults.Economy(local.ProviderName) ?? _settingsProvider.GetDefaultModel(local.ProviderName);
            if (!string.IsNullOrWhiteSpace(model))
            {
                return (local.ProviderName, model, $"Background work — running locally on {local.ProviderName}.");
            }
        }

        // Rule 2 — subscription CLI for user-waiting cloud-ok, non-vision work
        if (profile.Interactivity == LlmInteractivity.UserWaiting
            && cloudOk && !requiresVision && subscription is not null && IsEligible(subscription.ProviderName))
        {
            var model = !string.IsNullOrWhiteSpace(subscription.DefaultModel)
                ? subscription.DefaultModel
                : ModelDefaults.Frontier(subscription.ProviderName)
                ?? ModelDefaults.Balanced(subscription.ProviderName);
            if (!string.IsNullOrWhiteSpace(model))
            {
                return (subscription.ProviderName, model, $"Frontier tier on {subscription.ProviderName} subscription.");
            }
        }

        // Rule 3 — user-waiting + reasoning-heavy → balanced cloud
        if (profile.Interactivity == LlmInteractivity.UserWaiting && profile.IsReasoningHeavy
            && cloudOk && cloud is not null && IsEligible(cloud.ProviderName))
        {
            var model = !string.IsNullOrWhiteSpace(cloud.DefaultModel)
                ? cloud.DefaultModel
                : ModelDefaults.Balanced(cloud.ProviderName);
            if (!string.IsNullOrWhiteSpace(model))
            {
                return (cloud.ProviderName, model, $"Balanced cloud tier on {cloud.ProviderName} for user-waiting work.");
            }
        }

        // Rule 4 — anything cloud, economy tier
        if (cloudOk && cloud is not null && IsEligible(cloud.ProviderName))
        {
            var model = !string.IsNullOrWhiteSpace(cloud.DefaultModel)
                ? cloud.DefaultModel
                : ModelDefaults.Economy(cloud.ProviderName) ?? ModelDefaults.Balanced(cloud.ProviderName);
            if (!string.IsNullOrWhiteSpace(model))
            {
                return (cloud.ProviderName, model, $"Economy cloud tier on {cloud.ProviderName}.");
            }
        }

        return (null, null, "No eligible provider proposal.");
    }

    private static bool ProfileSuggestsVision(string functionType) =>
        functionType.Equals(FunctionTypes.Vision, StringComparison.OrdinalIgnoreCase)
        || functionType.Equals(FunctionTypes.VisualAnalysis, StringComparison.OrdinalIgnoreCase)
        || functionType.Equals(FunctionTypes.CaptchaSolving, StringComparison.OrdinalIgnoreCase);

    private bool IsEligible(string providerName)
    {
        if (_cooldownTracker is not null && !_cooldownTracker.IsAvailable(providerName)) return false;
        return _providerRegistry.GetProvider(providerName) is not null;
    }

    private LlmProviderSettings? PickFallback(string primary, LlmProviderSettings? cloud, LlmProviderSettings? local)
    {
        if (SubscriptionCliProviders.IsSubscriptionCliProvider(primary))
        {
            if (local is not null && !string.Equals(local.ProviderName, primary, StringComparison.OrdinalIgnoreCase))
                return local;
            return null;
        }

        if (cloud is not null && !string.Equals(cloud.ProviderName, primary, StringComparison.OrdinalIgnoreCase))
            return cloud;
        if (local is not null && !string.Equals(local.ProviderName, primary, StringComparison.OrdinalIgnoreCase))
            return local;
        return null;
    }

    private string? PickFallbackModel(string primary, LlmProviderSettings? cloud, LlmProviderSettings? local)
    {
        var fb = PickFallback(primary, cloud, local);
        if (fb is null) return null;
        return !string.IsNullOrWhiteSpace(fb.DefaultModel)
            ? fb.DefaultModel
            : ModelDefaults.Balanced(fb.ProviderName) ?? _settingsProvider.GetDefaultModel(fb.ProviderName);
    }

    private LlmProviderSettings? PickFirstAvailable(
        IEnumerable<LlmProviderSettings> all,
        Func<LlmProviderSettings, bool> predicate)
    {
        return all
            .Where(s => s.IsEnabled && !string.IsNullOrWhiteSpace(s.ProviderName))
            .Where(predicate)
            .Where(s => IsEligible(s.ProviderName))
            .OrderBy(s => s.Priority)
            .FirstOrDefault();
    }

    /// <summary>
    /// Enumerate the function-type constants by reflection. New function types added to
    /// <see cref="FunctionTypes"/> are automatically picked up — no extra wiring needed.
    /// </summary>
    private static IEnumerable<string> KnownFunctionTypes()
    {
        return typeof(FunctionTypes)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => (string?)f.GetRawConstantValue() ?? string.Empty)
            .Select(ModelScoutFunctionEvidence.NormalizeFunctionType)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
