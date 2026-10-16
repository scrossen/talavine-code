using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Runtime;
using SwarmCore.LlmRouting.Configuration;

namespace SwarmCore.Agent.Catalog;

/// <summary>
/// Host-neutral Model Scout tools. Applications can register these with
/// <c>AddModelScoutChatTools()</c>; richer hosts may replace matching tool ids with
/// domain-specific implementations (for example pipeline recovery in Talavine).
/// </summary>
public static class ModelScoutChatTools
{
    public const string OwnerId = "model-scout.tools";
    private const string RouteScopeFunction = "function";
    private const string RouteScopeCurrentProfile = "current-profile";

    public static IEnumerable<ToolDescriptor> Create(IServiceProvider services)
    {
        yield return ToolDescriptor.Create<CoreScoutStatusRequest, CoreScoutStatusResult>(
            "model-scout.status",
            OwnerId,
            "Show configured model providers, function routes, user pins, and recent auditable Model Scout decisions. Use for 'what models are you using?' or 'is this route pinned?'. When automatic management is on, proof recovery retries continue without a user model choice or recovery command.",
            ToolCostClass.Cheap,
            ToolSideEffect.ReadOnly,
            async (request, ct) =>
            {
                using var scope = services.CreateScope();
                var warnings = new List<string>();
                var settings = scope.ServiceProvider.GetService<ILlmSettingsProvider>();
                var policy = RoutingPolicy.Default;
                var policyStore = scope.ServiceProvider.GetService<IRoutingPolicyStore>();
                if (policyStore is null)
                {
                    warnings.Add("Routing policy storage is not registered; safe default weights are shown.");
                }
                else
                {
                    try
                    {
                        policy = await policyStore.GetAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"Routing policy is temporarily unavailable: {SafeExceptionMessage(ex)}");
                    }
                }
                var audit = scope.ServiceProvider.GetService<ILlmRoutingAuditStore>();
                var providers = new List<LlmProviderSettings>();
                var routes = new List<LlmFunctionPreference>();
                if (settings is null)
                {
                    warnings.Add("Model provider and function-route settings are not registered in this host.");
                }
                else
                {
                    try
                    {
                        providers = await settings.GetAllProviderSettingsAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"Provider settings are temporarily unavailable: {SafeExceptionMessage(ex)}");
                    }

                    try
                    {
                        routes = await settings.GetAllFunctionPreferencesAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"Function routes are temporarily unavailable: {SafeExceptionMessage(ex)}");
                    }
                }
                var routeStatuses = routes
                    .Select(route => new
                    {
                        CanonicalFunction = NormalizeAuditFunctionType(route.FunctionType),
                        Route = route,
                    })
                    .Where(item => !item.CanonicalFunction.Equals(
                        FunctionTypes.TextEmbedding,
                        StringComparison.OrdinalIgnoreCase))
                    .GroupBy(item => item.CanonicalFunction, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group
                        // A disabled preference is configuration history, not the
                        // route the runtime will execute. Keep it visible when it
                        // is the only row, but never let it hide an enabled alias.
                        .OrderByDescending(item => item.Route.IsEnabled)
                        .ThenByDescending(item => LlmPreferenceAuthors.IsUserPinned(item.Route.PinnedByUserAt))
                        .ThenByDescending(item => item.Route.FunctionType.Equals(
                            item.CanonicalFunction,
                            StringComparison.OrdinalIgnoreCase))
                        .ThenByDescending(item => item.Route.UpdatedAt ?? item.Route.CreatedAt)
                        .First())
                    .Select(item => new CoreScoutRoute(
                        RoutingDiagnosticSanitizer.Sanitize(item.CanonicalFunction, 160) ?? "unknown-function",
                        RoutingDiagnosticSanitizer.Sanitize(item.Route.PreferredProvider, 80) ?? "unknown-provider",
                        RoutingDiagnosticSanitizer.Sanitize(item.Route.PreferredModel, 240) ?? "unknown-model",
                        RoutingDiagnosticSanitizer.Sanitize(item.Route.FallbackProvider, 80),
                        RoutingDiagnosticSanitizer.Sanitize(item.Route.FallbackModel, 240),
                        item.Route.IsEnabled,
                        LlmPreferenceAuthors.IsUserPinned(item.Route.PinnedByUserAt),
                        FixedCompatibilityPin: false))
                    .ToList();
                var embeddingIdentity = new EmbeddingModelIdentity(
                    EmbeddingModelDefaults.Provider,
                    EmbeddingModelDefaults.Model,
                    EmbeddingModelDefaults.Dimension,
                    EmbeddingModelDefaults.ArtifactDigest);
                var embeddingPin = scope.ServiceProvider.GetService<IEmbeddingModelPin>();
                if (embeddingPin is not null)
                {
                    try
                    {
                        embeddingIdentity = await embeddingPin.GetAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        // The embedding-space contract still has a truthful fixed
                        // ship-with identity when the optional persistence bridge is
                        // absent or unreadable.
                    }
                }
                routeStatuses.Add(new CoreScoutRoute(
                    FunctionTypes.TextEmbedding,
                    RoutingDiagnosticSanitizer.Sanitize(embeddingIdentity.Provider, 80) ?? EmbeddingModelDefaults.Provider,
                    RoutingDiagnosticSanitizer.Sanitize(embeddingIdentity.Model, 240) ?? EmbeddingModelDefaults.Model,
                    null,
                    null,
                    Enabled: true,
                    UserPinned: false,
                    FixedCompatibilityPin: true));

                ModelScoutHostControlStatus hostStatus;
                var hostControl = scope.ServiceProvider.GetService<IModelScoutHostControl>();
                try
                {
                    hostStatus = hostControl is null
                        ? new ModelScoutHostControlStatus(false, UnavailableModelScoutHostControl.Message)
                        : await hostControl.GetStatusAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    hostStatus = new ModelScoutHostControlStatus(
                        false,
                        $"Host-specific Model Scout status is unavailable: {SafeExceptionMessage(ex)}");
                }
                var recent = new List<LlmRoutingAuditEntry>();
                if (audit is null)
                {
                    warnings.Add("Routing audit storage is not registered in this host.");
                }
                else
                {
                    try
                    {
                        recent = (await audit.QueryAsync(new LlmRoutingAuditQuery
                        {
                            Limit = Math.Clamp(request.RecentDecisions <= 0 ? 10 : request.RecentDecisions, 1, 30),
                        }, ct).ConfigureAwait(false)).ToList();
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"Recent routing receipts are temporarily unavailable: {SafeExceptionMessage(ex)}");
                    }
                }
                return new CoreScoutStatusResult
                {
                    Providers = providers.Select(provider => new CoreScoutProvider(
                        RoutingDiagnosticSanitizer.Sanitize(provider.ProviderName, 80) ?? "unknown-provider",
                        RoutingDiagnosticSanitizer.Sanitize(provider.DefaultModel, 240),
                        provider.IsEnabled,
                        provider.IsLocalProvider,
                        provider.DisabledByHealthCheck,
                        RoutingDiagnosticSanitizer.Sanitize(provider.LastValidationError, 300))).ToList(),
                    Routes = routeStatuses
                        .OrderBy(route => route.FunctionType, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    Recent = recent.Select(ToSummary).ToList(),
                    Policy = new CoreScoutPolicy(
                        policy.MonthlyCloudBudgetUsd,
                        policy.UseCustomModelScoutWeights,
                        policy.ModelScoutCapabilityWeight,
                        policy.ModelScoutSpeedWeight,
                        policy.ModelScoutCostWeight),
                    HostControlsAvailable = hostStatus.Available,
                    HostControlAvailability = RoutingDiagnosticSanitizer.Sanitize(
                        hostStatus.AvailabilityMessage,
                        500) ?? "Host-specific Model Scout control status is unavailable.",
                    AutoManageEnabled = hostStatus.AutomaticManagementEnabled,
                    CurrentProfileRoute = hostStatus.CurrentProfileRoute is null
                        ? null
                        : new CoreScoutCurrentProfileRoute(
                            RoutingDiagnosticSanitizer.Sanitize(hostStatus.CurrentProfileRoute.ProfileName, 160),
                            RoutingDiagnosticSanitizer.Sanitize(hostStatus.CurrentProfileRoute.Provider, 80),
                            RoutingDiagnosticSanitizer.Sanitize(hostStatus.CurrentProfileRoute.Model, 240),
                            RoutingDiagnosticSanitizer.Sanitize(hostStatus.CurrentProfileRoute.Mode, 80)
                            ?? "function-route-or-automatic"),
                    Warnings = warnings,
                };
            },
            keywords: ["model", "scout", "routing", "status", "pin", "automatic"]);

        yield return ToolDescriptor.Create<CoreScoutRecommendRequest, CoreScoutRecommendResult>(
            "model-scout.recommend",
            OwnerId,
            "Compare executable models for a canonical function using capability, speed, cost, reliability, privacy, and evidence. This recommends only and changes nothing.",
            ToolCostClass.Cheap,
            ToolSideEffect.ReadOnly,
            async (request, ct) =>
            {
                if (string.IsNullOrWhiteSpace(request.FunctionType))
                    return CoreScoutRecommendResult.Failure("functionType is required");
                using var scope = services.CreateScope();
                if (NormalizeAuditFunctionType(request.FunctionType).Equals(
                        FunctionTypes.TextEmbedding,
                        StringComparison.OrdinalIgnoreCase))
                {
                    var identity = await GetEmbeddingCompatibilityIdentityAsync(
                            scope.ServiceProvider,
                            ct)
                        .ConfigureAwait(false);
                    return new CoreScoutRecommendResult(
                        true,
                        $"Text embedding is fixed to compatibility identity {identity.Provider}/{identity.Model} ({identity.Dimension:N0} dimensions). Capability/speed/cost ranking does not apply inside an existing vector space; changing it requires an explicit semantic-index rebuild.",
                        identity.Provider,
                        identity.Model,
                        null,
                        null);
                }
                var scout = scope.ServiceProvider.GetRequiredService<IModelScout>();
                var audit = scope.ServiceProvider.GetService<ILlmRoutingAuditStore>();
                var currentTurn = scope.ServiceProvider.GetService<ICurrentTurnContextAccessor>()?.Current;
                ModelScoutRecommendationDecision decision;
                try
                {
                    decision = await scout.RecommendAsync(new ModelScoutRecommendationRequest
                    {
                        FunctionType = request.FunctionType.Trim(),
                        Provider = Clean(request.Provider),
                        Tier = ParseTier(request.Tier),
                        Interactivity = request.Interactive ? LlmInteractivity.UserWaiting : LlmInteractivity.Background,
                        PrivacyClass = request.LocalOnly ? LlmPrivacyClass.LocalOnly : LlmPrivacyClass.CloudOk,
                        RequiresVision = request.RequiresVision || ModelScoutFunctionRequirements.RequiresVision(request.FunctionType.Trim()),
                        RequiresToolUse = request.RequiresToolUse
                                          || ModelScoutFunctionRequirements.RequiresToolUse(request.FunctionType.Trim()),
                        RequiresStructuredOutput = request.RequiresStructuredOutput,
                        MinimumContextTokens = request.MinimumContextTokens,
                        MaximumEstimatedCostUsd = request.MaximumCostUsd,
                        LatencyTargetMs = request.LatencyTargetMs,
                        MinimumCapabilityScore = request.MinimumCapabilityScore,
                        RequireCurrentFunctionEvidence = request.RequireCurrentFunctionEvidence,
                        PersistAudit = true,
                        AuditSessionId = currentTurn?.ConversationId,
                        AuditTurnId = currentTurn?.TurnId?.ToString("N"),
                    }, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return CoreScoutRecommendResult.Failure(
                        $"Model Scout could not produce an auditable recommendation: {SafeExceptionMessage(ex)}");
                }

                var selected = decision.Selected?.Candidate;
                if (selected is null)
                {
                    return new CoreScoutRecommendResult(
                        false,
                        RoutingDiagnosticSanitizer.Sanitize(decision.Reason, 2_000)
                        ?? "No safe recommendation reason was returned.",
                        null,
                        null,
                        null,
                        Clean(decision.AuditCorrelationId));
                }

                var receipt = await FindMatchingRecommendationReceiptAsync(
                    audit,
                    decision,
                    request.FunctionType.Trim(),
                    ct).ConfigureAwait(false);
                if (receipt is null)
                {
                    return new CoreScoutRecommendResult(
                        false,
                        "Model Scout found a route, but no durable matching recommendation receipt could be verified. No route is being reported as an auditable recommendation.",
                        null,
                        null,
                        null,
                        Clean(decision.AuditCorrelationId));
                }

                return new CoreScoutRecommendResult(
                    true,
                    RoutingDiagnosticSanitizer.Sanitize(decision.Reason, 2_000) ?? "No safe recommendation reason was returned.",
                    RoutingDiagnosticSanitizer.Sanitize(selected.Provider, 80),
                    RoutingDiagnosticSanitizer.Sanitize(selected.Model, 240),
                    receipt.AuditId,
                    decision.AuditCorrelationId);
            },
            keywords: ["model", "scout", "recommend", "best", "speed", "cost", "capability"]);

        yield return ToolDescriptor.Create<CoreScoutExplainRequest, CoreScoutExplainResult>(
            "model-scout.explain-routing",
            OwnerId,
            "Explain an auditable routing selection or execution in plain English. Uses the current chat automatically when the user asks 'why did you use that model?'.",
            ToolCostClass.Cheap,
            ToolSideEffect.ReadOnly,
            async (request, ct) =>
            {
                using var scope = services.CreateScope();
                var audit = scope.ServiceProvider.GetService<ILlmRoutingAuditStore>();
                if (audit is null)
                    return new CoreScoutExplainResult("This host has not registered a routing audit store.", []);
                var currentTurn = scope.ServiceProvider.GetService<ICurrentTurnContextAccessor>()?.Current;
                var explicitFilter = !string.IsNullOrWhiteSpace(request.AuditId)
                                     || !string.IsNullOrWhiteSpace(request.CorrelationId)
                                     || !string.IsNullOrWhiteSpace(request.SessionId)
                                     || !string.IsNullOrWhiteSpace(request.TurnId)
                                     || !string.IsNullOrWhiteSpace(request.FunctionType);
                var limit = Math.Clamp(request.Limit <= 0 ? 10 : request.Limit, 1, 30);
                var rows = await audit.QueryAsync(new LlmRoutingAuditQuery
                {
                    AuditId = Clean(request.AuditId),
                    CorrelationId = Clean(request.CorrelationId),
                    SessionId = Clean(request.SessionId)
                                ?? (!explicitFilter ? Clean(currentTurn?.ConversationId) : null),
                    TurnId = Clean(request.TurnId),
                    FunctionTypes = string.IsNullOrWhiteSpace(request.FunctionType)
                        ? null
                        : ExpandFunctionAuditKeys(request.FunctionType),
                    Limit = limit,
                }, ct).ConfigureAwait(false);

                // Recommendation, selection, execution, and control receipts are
                // separate append-only facts. Pull their correlation siblings so a
                // user can ask one plain-English "why?" question and see the scorecard
                // plus what actually ran or changed.
                var expanded = rows.ToDictionary(row => row.AuditId, StringComparer.Ordinal);
                foreach (var correlationId in rows
                             .Select(row => Clean(row.CorrelationId))
                             .Where(value => value is not null)
                             .Distinct(StringComparer.Ordinal)
                             .Take(10))
                {
                    var siblings = await audit.QueryAsync(new LlmRoutingAuditQuery
                    {
                        CorrelationId = correlationId,
                        Limit = 20,
                    }, ct).ConfigureAwait(false);
                    foreach (var sibling in siblings)
                        expanded.TryAdd(sibling.AuditId, sibling);
                }
                rows = expanded.Values
                    .OrderByDescending(row => row.OccurredAtUtc)
                    .ThenByDescending(row => row.AuditId, StringComparer.Ordinal)
                    .Take(limit)
                    .ToList();
                var summaries = rows.Select(ToSummary).ToList();
                var primaryCorrelation = Clean(rows.FirstOrDefault()?.CorrelationId);
                var comparison = summaries
                    .Where(summary => primaryCorrelation is null
                                      || string.Equals(summary.CorrelationId, primaryCorrelation, StringComparison.Ordinal))
                    .Select(summary => summary.Evidence)
                    .FirstOrDefault(evidence => evidence?.Candidates.Count > 0);
                var explanation = summaries.Count == 0
                    ? "No matching routing receipt was found."
                    : BuildPlainEnglishExplanation(summaries[0], comparison);
                return new CoreScoutExplainResult(explanation, summaries);
            },
            keywords: ["model", "scout", "why", "explain", "audit", "receipt", "routing"]);

        yield return ToolDescriptor.Create<CoreScoutPinRequest, CoreScoutCommandResult>(
            "model-scout.pin-route",
            OwnerId,
            "Pin a canonical function route, or the current agent profile for agent-chat, to an exact provider/model after validating live provider reachability and the advertised model. Scope defaults to function; current-profile requires a connected host adapter. A direct chat request authorizes this explicit user override.",
            ToolCostClass.External,
            ToolSideEffect.ExternalSend,
            async (request, ct) =>
            {
                using var scope = services.CreateScope();
                var audit = scope.ServiceProvider.GetRequiredService<ILlmRoutingAuditStore>();
                var turn = scope.ServiceProvider.GetService<ICurrentTurnContextAccessor>()?.Current;
                // Use one canonical identity for validation, optimistic versioning,
                // persistence, compensation, host-scope checks, and receipts. An
                // alias row (for example agent-loop) would otherwise be invisible
                // to the runtime's canonical agent-chat lookup.
                var functionType = NormalizeAuditFunctionType(
                    Clean(request.FunctionType) ?? "model-scout");
                var controlScope = ParseRouteScope(request.Scope);
                var requiresVision = request.RequiresVision || ModelScoutFunctionRequirements.RequiresVision(functionType);
                var requiresToolUse = ModelScoutFunctionRequirements.RequiresToolUse(functionType);
                const string reason = "User approved an exact Model Scout route pin.";
                var intent = await RecordIntentAsync(
                    audit,
                    functionType,
                    reason,
                    new
                    {
                        command = "pin",
                        scope = controlScope ?? Clean(request.Scope),
                        provider = Clean(request.Provider),
                        model = Clean(request.Model),
                        fallbackProvider = Clean(request.FallbackProvider),
                        fallbackModel = Clean(request.FallbackModel),
                        requiresVision,
                    },
                    turn,
                    ct).ConfigureAwait(false);
                if (functionType.Equals(
                        FunctionTypes.TextEmbedding,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return await RecordCommandAsync(
                        audit,
                        false,
                        LlmRoutingAuditEventKinds.PreferenceChanged,
                        null,
                        EmbeddingCompatibilityMutationGuidance("pin-route"),
                        CancellationToken.None,
                        functionType,
                        intent).ConfigureAwait(false);
                }

                if (controlScope is null)
                    return await RecordCommandAsync(
                        audit, false, LlmRoutingAuditEventKinds.PreferenceChanged, null,
                        "scope must be either 'function' or 'current-profile'", CancellationToken.None, functionType, intent).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(request.FunctionType)
                    || string.IsNullOrWhiteSpace(request.Provider)
                    || string.IsNullOrWhiteSpace(request.Model))
                    return await RecordCommandAsync(
                        audit, false, LlmRoutingAuditEventKinds.PreferenceChanged, null,
                        "functionType, provider, and model are required", CancellationToken.None, functionType, intent).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(request.FallbackProvider) != string.IsNullOrWhiteSpace(request.FallbackModel))
                    return await RecordCommandAsync(
                        audit, false, LlmRoutingAuditEventKinds.PreferenceChanged, null,
                        "fallbackProvider and fallbackModel must be supplied together", CancellationToken.None, functionType, intent).ConfigureAwait(false);
                if (controlScope == RouteScopeCurrentProfile
                    && !functionType.Equals(FunctionTypes.AgentChat, StringComparison.OrdinalIgnoreCase))
                    return await RecordCommandAsync(
                        audit, false, LlmRoutingAuditEventKinds.PreferenceChanged, null,
                        "current-profile scope is valid only for agent-chat", CancellationToken.None, functionType, intent).ConfigureAwait(false);
                if (controlScope == RouteScopeCurrentProfile
                    && (!string.IsNullOrWhiteSpace(request.FallbackProvider)
                        || !string.IsNullOrWhiteSpace(request.FallbackModel)))
                    return await RecordCommandAsync(
                        audit, false, LlmRoutingAuditEventKinds.PreferenceChanged, null,
                        "current-profile routes do not have a fallback", CancellationToken.None, functionType, intent).ConfigureAwait(false);

                var settings = scope.ServiceProvider.GetRequiredService<ILlmSettingsProvider>();
                var registry = scope.ServiceProvider.GetRequiredService<ILlmProviderRegistry>();
                var primaryError = await ValidateTargetAsync(
                    scope.ServiceProvider,
                    settings,
                    registry,
                    request.Provider,
                    request.Model,
                    requiresVision,
                    requiresToolUse,
                    CancellationToken.None).ConfigureAwait(false);
                if (primaryError is not null)
                    return await RecordCommandAsync(
                        audit, false, LlmRoutingAuditEventKinds.PreferenceChanged, null,
                        primaryError, CancellationToken.None, functionType, intent).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(request.FallbackProvider))
                {
                    var fallbackError = await ValidateTargetAsync(
                        scope.ServiceProvider,
                        settings,
                        registry,
                        request.FallbackProvider!,
                        request.FallbackModel!,
                        requiresVision,
                        requiresToolUse,
                        CancellationToken.None).ConfigureAwait(false);
                    if (fallbackError is not null)
                        return await RecordCommandAsync(
                            audit, false, LlmRoutingAuditEventKinds.PreferenceChanged, null,
                            fallbackError, CancellationToken.None, functionType, intent).ConfigureAwait(false);
                }

                if (controlScope == RouteScopeCurrentProfile)
                {
                    return await ExecuteHostControlAsync(
                        scope.ServiceProvider,
                        audit,
                        intent,
                        "pin-current-profile",
                        RouteScopeCurrentProfile,
                        LlmRoutingAuditEventKinds.PreferenceChanged,
                        FunctionTypes.AgentChat,
                        (host, context, token) => host.PinCurrentProfileAsync(
                            request.Provider.Trim(),
                            request.Model.Trim(),
                            context,
                            token)).ConfigureAwait(false);
                }

                LlmFunctionPreference? before;
                try
                {
                    before = (await settings.GetAllFunctionPreferencesAsync().ConfigureAwait(false))
                        .FirstOrDefault(item => item.FunctionType.Equals(functionType, StringComparison.OrdinalIgnoreCase));
                }
                catch (Exception ex)
                {
                    return await RecordCommandAsync(
                        audit, false, LlmRoutingAuditEventKinds.PreferenceChanged, null,
                        $"Could not inspect the current route pin: {SafeExceptionMessage(ex)}",
                        CancellationToken.None, functionType, intent).ConfigureAwait(false);
                }

                var preference = new LlmFunctionPreference
                {
                    FunctionType = functionType,
                    PreferredProvider = request.Provider.Trim(),
                    PreferredModel = request.Model.Trim(),
                    FallbackProvider = Clean(request.FallbackProvider),
                    FallbackModel = Clean(request.FallbackModel),
                    IsEnabled = true,
                    RequiresVision = requiresVision,
                    UpdatedAt = DateTime.UtcNow,
                    UpdatedBy = LlmPreferenceAuthors.User,

                    PinnedByUserAt = DateTime.UtcNow,
                    AuditCorrelationId = intent.CorrelationId,
                    MutationRevision = Guid.NewGuid().ToString("N"),
                };
                bool success;
                LlmFunctionPreference? written = null;
                try
                {
                    var write = await settings.TrySetExactFunctionPreferenceAsync(
                        preference,
                        FunctionPreferenceWriteAuthority.UserAuthorizedOverride,
                        FunctionPreferenceVersion.Capture(functionType, before)).ConfigureAwait(false);
                    written = write.Current;
                    success = write.Applied
                              && written is not null
                              && string.Equals(
                                  written.MutationRevision,
                                  preference.MutationRevision,
                                  StringComparison.Ordinal);
                }
                catch (Exception ex)
                {
                    var compensation = await TryCompensateFailedPinMutationAsync(
                        settings,
                        functionType,
                        before,
                        preference).ConfigureAwait(false);
                    return await RecordCommandAsync(
                        audit, false, LlmRoutingAuditEventKinds.PreferenceChanged, compensation.Current,
                        $"Could not save the route pin: {SafeExceptionMessage(ex)} {compensation.Message}",
                        CancellationToken.None, preference.FunctionType, intent, before).ConfigureAwait(false);
                }
                var failureAfter = before;
                var failureReason = "The route pin was refused by the settings provider.";
                var ambiguousCommitRemains = false;
                if (!success && written is not null
                    && string.Equals(
                        written.MutationRevision,
                        preference.MutationRevision,
                        StringComparison.Ordinal))
                {
                    var rollbackError = await TryRestorePinnedPreferenceAsync(
                        settings,
                        functionType,
                        before,
                        written).ConfigureAwait(false);
                    if (rollbackError is null)
                    {
                        failureReason = "The settings provider returned a non-applied result for the caller-owned route revision; the ambiguous committed write was conditionally rolled back.";
                    }
                    else
                    {
                        ambiguousCommitRemains = true;
                        failureAfter = await GetExactPreferenceAsync(settings, functionType).ConfigureAwait(false);
                        failureReason = $"The settings provider returned a non-applied result for the caller-owned route revision. The outcome is uncertain because {rollbackError}; current storage was left untouched.";
                    }
                }
                try
                {
                    return await RecordCommandAsync(
                        audit,
                        success,
                        LlmRoutingAuditEventKinds.PreferenceChanged,
                        success ? preference : failureAfter,
                        success ? reason : failureReason,
                        CancellationToken.None,
                        preference.FunctionType,
                        intent,
                        before).ConfigureAwait(false);
                }
                catch (Exception auditException)
                {
                    var rollbackError = success
                        ? await TryRestorePinnedPreferenceAsync(
                            settings,
                            functionType,
                            before,
                            written!).ConfigureAwait(false)
                        : null;
                    var message = !success
                        ? ambiguousCommitRemains
                            ? "The settings provider returned a non-applied result for the caller-owned route revision and its terminal receipt also failed. The route may remain committed; current storage was left untouched."
                            : "The settings provider refused the route pin, or its ambiguous owned write was restored, and its terminal receipt also failed."
                        : rollbackError is null
                            ? "The route pin was rolled back because its completion receipt could not be committed."
                            : $"The route pin may have changed because its receipt failed and rollback also failed: {rollbackError}";
                    return AuditOutageResult(intent, message, auditException);
                }
            },
            keywords: ["model", "scout", "pin", "always", "route", "override"]);

        yield return ToolDescriptor.Create<CoreScoutUnpinRequest, CoreScoutCommandResult>(
            "model-scout.unpin-route",
            OwnerId,
            "Remove an exact function pin, or clear the current agent-profile route for agent-chat, and return that scope to automatic routing. Scope defaults to function; current-profile requires a connected host adapter. Requires approval.",
            ToolCostClass.Cheap,
            ToolSideEffect.WritesLocal,
            async (request, ct) =>
            {
                using var scope = services.CreateScope();
                var audit = scope.ServiceProvider.GetRequiredService<ILlmRoutingAuditStore>();
                var turn = scope.ServiceProvider.GetService<ICurrentTurnContextAccessor>()?.Current;
                // Canonicalize before any lookup/version/delete operation so a
                // user can remove the same route through any supported alias.
                var functionType = NormalizeAuditFunctionType(
                    Clean(request.FunctionType) ?? "model-scout");
                var controlScope = ParseRouteScope(request.Scope);
                const string reason = "User approved removing the exact Model Scout route pin.";
                var intent = await RecordIntentAsync(
                    audit,
                    functionType,
                    reason,
                    new { command = "unpin", scope = controlScope ?? Clean(request.Scope) },
                    turn,
                    ct).ConfigureAwait(false);
                if (functionType.Equals(
                        FunctionTypes.TextEmbedding,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return await RecordCommandAsync(
                        audit,
                        false,
                        LlmRoutingAuditEventKinds.PreferenceCleared,
                        null,
                        EmbeddingCompatibilityMutationGuidance("unpin-route"),
                        CancellationToken.None,
                        functionType,
                        intent).ConfigureAwait(false);
                }
                if (controlScope is null)
                    return await RecordCommandAsync(
                        audit, false, LlmRoutingAuditEventKinds.PreferenceCleared, null,
                        "scope must be either 'function' or 'current-profile'", CancellationToken.None, functionType, intent).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(request.FunctionType))
                    return await RecordCommandAsync(
                        audit, false, LlmRoutingAuditEventKinds.PreferenceCleared, null,
                        "functionType is required", CancellationToken.None, functionType, intent).ConfigureAwait(false);
                if (controlScope == RouteScopeCurrentProfile
                    && !functionType.Equals(FunctionTypes.AgentChat, StringComparison.OrdinalIgnoreCase))
                    return await RecordCommandAsync(
                        audit, false, LlmRoutingAuditEventKinds.PreferenceCleared, null,
                        "current-profile scope is valid only for agent-chat", CancellationToken.None, functionType, intent).ConfigureAwait(false);
                if (controlScope == RouteScopeCurrentProfile)
                {
                    return await ExecuteHostControlAsync(
                        scope.ServiceProvider,
                        audit,
                        intent,
                        "unpin-current-profile",
                        RouteScopeCurrentProfile,
                        LlmRoutingAuditEventKinds.PreferenceCleared,
                        FunctionTypes.AgentChat,
                        (host, context, token) => host.UnpinCurrentProfileAsync(context, token)).ConfigureAwait(false);
                }

                var settings = scope.ServiceProvider.GetRequiredService<ILlmSettingsProvider>();

                LlmFunctionPreference? exact;
                try
                {
                    exact = (await settings.GetAllFunctionPreferencesAsync().ConfigureAwait(false))
                        .FirstOrDefault(item => item.FunctionType.Equals(functionType, StringComparison.OrdinalIgnoreCase));
                }
                catch (Exception ex)
                {
                    return await RecordCommandAsync(
                        audit, false, LlmRoutingAuditEventKinds.PreferenceCleared, null,
                        $"Could not inspect the exact route pin: {SafeExceptionMessage(ex)}", CancellationToken.None, functionType, intent).ConfigureAwait(false);
                }

                if (exact is null)
                    return await RecordCommandAsync(
                        audit, false, LlmRoutingAuditEventKinds.PreferenceCleared, null,
                        $"There is no exact route pin for '{functionType}' to remove.", CancellationToken.None, functionType, intent).ConfigureAwait(false);
                if (!LlmPreferenceAuthors.IsUserPinned(exact.PinnedByUserAt))
                    return await RecordCommandAsync(
                        audit, false, LlmRoutingAuditEventKinds.PreferenceCleared, exact,
                        $"The exact route for '{functionType}' is automatic, not a user pin; it was left unchanged.",
                        CancellationToken.None, functionType, intent, exact).ConfigureAwait(false);

                bool success;
                FunctionPreferenceConditionalWriteResult removal;
                try
                {
                    removal = await settings.TryDeleteFunctionPreferenceAsync(
                        functionType,
                        FunctionPreferenceWriteAuthority.UserAuthorizedOverride,
                        FunctionPreferenceVersion.Capture(functionType, exact)).ConfigureAwait(false);
                    success = removal.Applied;
                }
                catch (Exception ex)
                {
                    var compensation = await TryCompensateFailedUnpinMutationAsync(
                        settings,
                        functionType,
                        exact).ConfigureAwait(false);
                    return await RecordCommandAsync(
                        audit, false, LlmRoutingAuditEventKinds.PreferenceCleared, compensation.Current,
                        $"Could not remove the route pin: {SafeExceptionMessage(ex)} {compensation.Message}",
                        CancellationToken.None, functionType, intent, exact).ConfigureAwait(false);
                }
                var currentAfter = removal.Current;
                if (!success && currentAfter is null)
                {
                    currentAfter = await GetExactPreferenceAsync(settings, functionType).ConfigureAwait(false);
                }
                var ambiguousAbsent = !success
                                      && removal.Status == FunctionPreferenceConditionalWriteStatus.Conflict
                                      && currentAfter is null;
                try
                {
                    return await RecordCommandAsync(
                        audit,
                        success,
                        LlmRoutingAuditEventKinds.PreferenceCleared,
                        success ? null : currentAfter,
                        success
                            ? reason
                            : ambiguousAbsent
                                ? "The delete returned a conflict while the exact route was already absent. The unpin outcome is uncertain or another unpin already won; absence was kept and no pin was restored."
                                : "The exact user route pin was not removed because a current exact route still exists; that newer row was left unchanged.",
                        CancellationToken.None,
                        functionType,
                        intent,
                        exact).ConfigureAwait(false);
                }
                catch (Exception auditException)
                {
                    var message = !success
                        ? ambiguousAbsent
                            ? "The delete returned a conflict while the exact route was already absent, and its terminal receipt also failed. The outcome is uncertain or another unpin already won; absence was kept."
                            : "The settings provider refused the route unpin and its terminal receipt also failed; the current exact row was left unchanged."
                        : "The route unpin committed before its completion receipt failed; current storage was left untouched. Safe rollback was skipped because physical absence has no owned revision and may include a later pin-then-unpin ABA.";
                    return AuditOutageResult(intent, message, auditException);
                }
            },
            keywords: ["model", "scout", "unpin", "automatic", "route"]);

        yield return ToolDescriptor.Create<CoreScoutPolicyRequest, CoreScoutCommandResult>(
            "model-scout.set-policy",
            OwnerId,
            "Persist Model Scout's soft monthly metered-cloud budget target and capability/speed/cost priorities. The target increases cost pressure as spend approaches it; it is not a billing hard stop. Requires approval. Use for 'favor speed with a $20 monthly target' or 'reset Scout weights to automatic'.",
            ToolCostClass.Cheap,
            ToolSideEffect.WritesLocal,
            async (request, ct) =>
            {
                using var scope = services.CreateScope();
                var audit = scope.ServiceProvider.GetRequiredService<ILlmRoutingAuditStore>();
                var turn = scope.ServiceProvider.GetService<ICurrentTurnContextAccessor>()?.Current;
                const string reason = "User approved a persistent Model Scout cost/speed/capability policy.";
                var intent = await RecordIntentAsync(
                    audit,
                    "model-scout",
                    reason,
                    new
                    {
                        command = "set-policy",
                        request.MonthlyCloudBudgetUsd,
                        request.ClearMonthlyCloudBudget,
                        request.CapabilityWeight,
                        request.SpeedWeight,
                        request.CostWeight,
                        request.ResetWeights,
                    },
                    turn,
                    ct).ConfigureAwait(false);
                if (request.ClearMonthlyCloudBudget && request.MonthlyCloudBudgetUsd.HasValue)
                    return await RecordCommandAsync(
                        audit, false, LlmRoutingAuditEventKinds.PolicyChanged, null,
                        "Choose either monthlyCloudBudgetUsd or clearMonthlyCloudBudget, not both",
                        CancellationToken.None, "model-scout", intent).ConfigureAwait(false);
                if (request.MonthlyCloudBudgetUsd is <= 0 or > 1_000_000m)
                    return await RecordCommandAsync(
                        audit, false, LlmRoutingAuditEventKinds.PolicyChanged, null,
                        "monthlyCloudBudgetUsd must be positive and no more than $1,000,000, or use clearMonthlyCloudBudget",
                        CancellationToken.None, "model-scout", intent).ConfigureAwait(false);
                var anyWeight = request.CapabilityWeight.HasValue
                                || request.SpeedWeight.HasValue
                                || request.CostWeight.HasValue;
                if (anyWeight && new[]
                    {
                        request.CapabilityWeight ?? 0m,
                        request.SpeedWeight ?? 0m,
                        request.CostWeight ?? 0m,
                    }.Any(weight => weight is < 0 or > 100))
                    return await RecordCommandAsync(
                        audit, false, LlmRoutingAuditEventKinds.PolicyChanged, null,
                        "Scout weights must each be between 0 and 100", CancellationToken.None, "model-scout", intent).ConfigureAwait(false);
                if (request.ResetWeights && anyWeight)
                    return await RecordCommandAsync(
                        audit, false, LlmRoutingAuditEventKinds.PolicyChanged, null,
                        "resetWeights cannot be combined with explicit weights", CancellationToken.None, "model-scout", intent).ConfigureAwait(false);

                var store = scope.ServiceProvider.GetRequiredService<IRoutingPolicyStore>();
                RoutingPolicySnapshot beforeSnapshot;
                RoutingPolicy before;
                try
                {
                    beforeSnapshot = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
                    before = beforeSnapshot.Policy;
                }
                catch (Exception ex)
                {
                    return await RecordCommandAsync(
                        audit, false, LlmRoutingAuditEventKinds.PolicyChanged, null,
                        $"Could not read the current Model Scout policy: {SafeExceptionMessage(ex)}",
                        CancellationToken.None, "model-scout", intent).ConfigureAwait(false);
                }
                var after = before with
                {
                    MonthlyCloudBudgetUsd = request.ClearMonthlyCloudBudget
                        ? null
                        : request.MonthlyCloudBudgetUsd ?? before.MonthlyCloudBudgetUsd,
                    ModelScoutCapabilityWeight = request.CapabilityWeight ?? before.ModelScoutCapabilityWeight,
                    ModelScoutSpeedWeight = request.SpeedWeight ?? before.ModelScoutSpeedWeight,
                    ModelScoutCostWeight = request.CostWeight ?? before.ModelScoutCostWeight,
                    UseCustomModelScoutWeights = request.ResetWeights
                        ? false
                        : anyWeight || before.UseCustomModelScoutWeights,
                };
                if (after.UseCustomModelScoutWeights
                    && (after.ModelScoutCapabilityWeight is < 0 or > 100
                        || after.ModelScoutSpeedWeight is < 0 or > 100
                        || after.ModelScoutCostWeight is < 0 or > 100))
                    return await RecordCommandAsync(
                        audit, false, LlmRoutingAuditEventKinds.PolicyChanged, null,
                        "Resolved Scout weights must each be between 0 and 100", CancellationToken.None, "model-scout", intent).ConfigureAwait(false);
                if (after.UseCustomModelScoutWeights
                    && after.ModelScoutCapabilityWeight
                       + after.ModelScoutSpeedWeight
                       + after.ModelScoutCostWeight <= 0)
                    return await RecordCommandAsync(
                        audit, false, LlmRoutingAuditEventKinds.PolicyChanged, null,
                        "At least one resolved Scout weight must be positive", CancellationToken.None, "model-scout", intent).ConfigureAwait(false);
                var mutationRevision = Guid.NewGuid().ToString("N");
                var writtenSnapshot = new RoutingPolicySnapshot(after, mutationRevision);
                try
                {
                    var write = await store.TrySetAsync(
                        after,
                        beforeSnapshot,
                        mutationRevision,
                        CancellationToken.None).ConfigureAwait(false);
                    if (!write.Applied
                        || write.Current != after
                        || !string.Equals(write.Revision, mutationRevision, StringComparison.Ordinal))
                    {
                        return await RecordCommandAsync(
                            audit, false, LlmRoutingAuditEventKinds.PolicyChanged, null,
                            "The policy changed concurrently; the newer policy was left unchanged.",
                            CancellationToken.None, "model-scout", intent).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    var compensation = await TryCompensateFailedPolicyMutationAsync(
                        store,
                        beforeSnapshot,
                        writtenSnapshot).ConfigureAwait(false);
                    return await RecordCommandAsync(
                        audit, false, LlmRoutingAuditEventKinds.PolicyChanged, null,
                        $"Could not save the Model Scout policy: {SafeExceptionMessage(ex)} {compensation}",
                        CancellationToken.None, "model-scout", intent).ConfigureAwait(false);
                }
                var receipt = new LlmRoutingAuditEntry
                {
                    EventKind = LlmRoutingAuditEventKinds.PolicyChanged,
                    Actor = LlmPreferenceAuthors.User,
                    Trigger = "approved-chat-command",
                    FunctionType = "model-scout",
                    Reason = reason,
                    EvidenceJson = RoutingDiagnosticSanitizer.SanitizeEvidenceJson(
                        System.Text.Json.JsonSerializer.Serialize(new
                        {
                            beforeMonthlyCloudBudgetUsd = before.MonthlyCloudBudgetUsd,
                            afterMonthlyCloudBudgetUsd = after.MonthlyCloudBudgetUsd,
                            beforeUsesCustomWeights = before.UseCustomModelScoutWeights,
                            afterUsesCustomWeights = after.UseCustomModelScoutWeights,
                            weights = new
                            {
                                after.ModelScoutCapabilityWeight,
                                after.ModelScoutSpeedWeight,
                                after.ModelScoutCostWeight,
                            },
                        })),
                    IsUserOverride = true,
                    Succeeded = true,
                    CorrelationId = intent.CorrelationId,
                    SessionId = intent.SessionId,
                    TurnId = intent.TurnId,
                };
                try
                {
                    await audit.AppendAsync(receipt, CancellationToken.None).ConfigureAwait(false);
                    return new CoreScoutCommandResult(true, receipt.Reason, receipt.AuditId);
                }
                catch (Exception auditException)
                {
                    var rollbackError = await TryRestorePolicyAsync(
                        store,
                        beforeSnapshot,
                        writtenSnapshot).ConfigureAwait(false);
                    var message = rollbackError is null
                        ? "The policy change was rolled back because its completion receipt could not be committed."
                        : $"The policy may have changed because its receipt failed and rollback also failed: {rollbackError}";
                    return AuditOutageResult(intent, message, auditException);
                }
            },
            keywords: ["model", "scout", "policy", "budget", "speed", "cost", "capability", "monthly"]);

        yield return ToolDescriptor.Create<CoreScoutAutoManageRequest, CoreScoutCommandResult>(
            "model-scout.set-auto-manage",
            OwnerId,
            "Turn this app's automatic Model Scout routing management on or off. This is profile-wide and requires the app's host-control adapter; a direct chat request authorizes the change.",
            ToolCostClass.Cheap,
            ToolSideEffect.WritesLocal,
            async (request, ct) =>
            {
                using var scope = services.CreateScope();
                var audit = scope.ServiceProvider.GetRequiredService<ILlmRoutingAuditStore>();
                var turn = scope.ServiceProvider.GetService<ICurrentTurnContextAccessor>()?.Current;
                var intent = await RecordIntentAsync(
                    audit,
                    "model-scout",
                    "User approved changing automatic Model Scout routing management.",
                    new { command = "set-auto-manage", enabled = request.Enabled },
                    turn,
                    ct).ConfigureAwait(false);
                if (!request.Enabled.HasValue)
                    return await RecordCommandAsync(
                        audit, false, LlmRoutingAuditEventKinds.AutomationChanged, null,
                        "enabled is required and must be true or false", CancellationToken.None, "model-scout", intent).ConfigureAwait(false);

                return await ExecuteHostControlAsync(
                    scope.ServiceProvider,
                    audit,
                    intent,
                    "set-auto-manage",
                    "profile",
                    LlmRoutingAuditEventKinds.AutomationChanged,
                    "model-scout",
                    (host, context, token) => host.SetAutomaticManagementAsync(
                        request.Enabled.Value,
                        context,
                        token)).ConfigureAwait(false);
            },
            keywords: ["model", "scout", "automatic", "auto-manage", "enable", "disable"]);

        yield return ToolDescriptor.Create<CoreScoutOptimizeRequest, CoreScoutCommandResult>(
            "model-scout.optimize-now",
            OwnerId,
            "Run the app's Model Scout optimization now, optionally for one canonical function. The host may probe or benchmark routes; a direct chat request authorizes the operation and it requires a host-control adapter.",
            ToolCostClass.Expensive,
            ToolSideEffect.ExternalSend,
            async (request, ct) =>
            {
                using var scope = services.CreateScope();
                var audit = scope.ServiceProvider.GetRequiredService<ILlmRoutingAuditStore>();
                var turn = scope.ServiceProvider.GetService<ICurrentTurnContextAccessor>()?.Current;
                var functionType = string.IsNullOrWhiteSpace(request.FunctionType)
                    ? null
                    : NormalizeAuditFunctionType(request.FunctionType);
                var intent = await RecordIntentAsync(
                    audit,
                    functionType ?? "model-scout",
                    "User approved an immediate Model Scout optimization run.",
                    new { command = "optimize-now", functionType },
                    turn,
                    ct).ConfigureAwait(false);
                return await ExecuteHostControlAsync(
                    scope.ServiceProvider,
                    audit,
                    intent,
                    "optimize-now",
                    "profile",
                    LlmRoutingAuditEventKinds.OptimizationRun,
                    functionType ?? "model-scout",
                    (host, context, token) => host.OptimizeAsync(
                        new ModelScoutHostOptimizeRequest(functionType),
                        context,
                        token)).ConfigureAwait(false);
            },
            keywords: ["model", "scout", "optimize", "run", "rebalance"]);

        yield return ToolDescriptor.Create<CoreScoutBenchmarkRequest, CoreScoutCommandResult>(
            "model-scout.benchmark-route",
            OwnerId,
            "Benchmark one exact provider/model for a canonical function. This may invoke a local, subscription, or metered provider; a direct chat request authorizes the operation and it requires a host-control adapter. Set allowMeteredProvider when a metered provider is acceptable.",
            ToolCostClass.Expensive,
            ToolSideEffect.ExternalSend,
            async (request, ct) =>
            {
                using var scope = services.CreateScope();
                var audit = scope.ServiceProvider.GetRequiredService<ILlmRoutingAuditStore>();
                var turn = scope.ServiceProvider.GetService<ICurrentTurnContextAccessor>()?.Current;
                var functionType = string.IsNullOrWhiteSpace(request.FunctionType)
                    ? "model-scout"
                    : NormalizeAuditFunctionType(request.FunctionType);
                var intent = await RecordIntentAsync(
                    audit,
                    functionType,
                    "User approved benchmarking one exact Model Scout route.",
                    new
                    {
                        command = "benchmark-route",
                        functionType = string.IsNullOrWhiteSpace(request.FunctionType)
                            ? null
                            : NormalizeAuditFunctionType(request.FunctionType),
                        provider = Clean(request.Provider),
                        model = Clean(request.Model),
                        request.AllowMeteredProvider,
                    },
                    turn,
                    ct).ConfigureAwait(false);
                if (NormalizeAuditFunctionType(functionType).Equals(
                        FunctionTypes.TextEmbedding,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return await RecordCommandAsync(
                        audit,
                        false,
                        LlmRoutingAuditEventKinds.BenchmarkRun,
                        null,
                        EmbeddingCompatibilityMutationGuidance("benchmark-route"),
                        CancellationToken.None,
                        functionType,
                        intent).ConfigureAwait(false);
                }
                if (string.IsNullOrWhiteSpace(request.FunctionType)
                    || string.IsNullOrWhiteSpace(request.Provider)
                    || string.IsNullOrWhiteSpace(request.Model))
                    return await RecordCommandAsync(
                        audit, false, LlmRoutingAuditEventKinds.BenchmarkRun, null,
                        "functionType, provider, and model are required", CancellationToken.None, functionType, intent).ConfigureAwait(false);

                return await ExecuteHostControlAsync(
                    scope.ServiceProvider,
                    audit,
                    intent,
                    "benchmark-route",
                    "function",
                    LlmRoutingAuditEventKinds.BenchmarkRun,
                    functionType,
                    (host, context, token) => host.BenchmarkAsync(
                        new ModelScoutHostBenchmarkRequest(
                            functionType,
                            request.Provider.Trim(),
                            request.Model.Trim(),
                            request.AllowMeteredProvider),
                        context,
                        token)).ConfigureAwait(false);
            },
            keywords: ["model", "scout", "benchmark", "test", "proof", "route"]);

        yield return ToolDescriptor.Create<CoreScoutRecoveryRequest, CoreScoutCommandResult>(
            "model-scout.recover-route",
            OwnerId,
            "Optionally run an immediate aggressive search for current function proof. When automatic management is on, bounded recovery retries already continue without any user choice or command. A direct chat request authorizes this immediate check, which may refresh public benchmarks, probe providers, or run bounded benchmarks. Requires a host-control adapter.",
            ToolCostClass.Expensive,
            ToolSideEffect.ExternalSend,
            async (request, ct) =>
            {
                using var scope = services.CreateScope();
                var audit = scope.ServiceProvider.GetRequiredService<ILlmRoutingAuditStore>();
                var turn = scope.ServiceProvider.GetService<ICurrentTurnContextAccessor>()?.Current;
                var functionType = string.IsNullOrWhiteSpace(request.FunctionType)
                    ? "model-scout"
                    : NormalizeAuditFunctionType(request.FunctionType);
                var intent = await RecordIntentAsync(
                    audit,
                    functionType,
                    "User approved an immediate Model Scout route-proof recovery run.",
                    new
                    {
                        command = "recover-route",
                        functionType = string.IsNullOrWhiteSpace(request.FunctionType)
                            ? null
                            : NormalizeAuditFunctionType(request.FunctionType)
                    },
                    turn,
                    ct).ConfigureAwait(false);
                if (NormalizeAuditFunctionType(functionType).Equals(
                        FunctionTypes.TextEmbedding,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return await RecordCommandAsync(
                        audit,
                        false,
                        LlmRoutingAuditEventKinds.RecoveryRun,
                        null,
                        EmbeddingCompatibilityMutationGuidance("recover-route"),
                        CancellationToken.None,
                        functionType,
                        intent).ConfigureAwait(false);
                }
                if (string.IsNullOrWhiteSpace(request.FunctionType))
                    return await RecordCommandAsync(
                        audit, false, LlmRoutingAuditEventKinds.RecoveryRun, null,
                        "functionType is required", CancellationToken.None, functionType, intent).ConfigureAwait(false);

                return await ExecuteHostControlAsync(
                    scope.ServiceProvider,
                    audit,
                    intent,
                    "recover-route",
                    "function",
                    LlmRoutingAuditEventKinds.RecoveryRun,
                    functionType,
                    (host, context, token) => host.RecoverAsync(
                        new ModelScoutHostRecoveryRequest(functionType),
                        context,
                        token)).ConfigureAwait(false);
            },
            keywords: ["model", "scout", "recover", "proof", "search", "route"]);
    }

    private static async Task<LlmRoutingAuditEntry?> FindMatchingRecommendationReceiptAsync(
        ILlmRoutingAuditStore? audit,
        ModelScoutRecommendationDecision decision,
        string requestedFunctionType,
        CancellationToken ct)
    {
        var selected = decision.Selected?.Candidate;
        if (audit is null
            || selected is null
            || string.IsNullOrWhiteSpace(decision.AuditCorrelationId)
            || string.IsNullOrWhiteSpace(decision.Request.FunctionType)
            || string.IsNullOrWhiteSpace(selected.Provider)
            || string.IsNullOrWhiteSpace(selected.Model)
            || !string.Equals(
                NormalizeAuditFunctionType(decision.Request.FunctionType),
                NormalizeAuditFunctionType(requestedFunctionType),
                StringComparison.Ordinal))
        {
            return null;
        }

        IReadOnlyList<LlmRoutingAuditEntry> rows;
        try
        {
            rows = await audit.QueryAsync(new LlmRoutingAuditQuery
            {
                CorrelationId = decision.AuditCorrelationId,
                FunctionType = NormalizeAuditFunctionType(requestedFunctionType),
                EventKind = LlmRoutingAuditEventKinds.Recommendation,
                Limit = 20,
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }

        return rows.FirstOrDefault(row =>
            !string.IsNullOrWhiteSpace(row.AuditId)
            && row.Succeeded
            && string.Equals(row.EventKind, LlmRoutingAuditEventKinds.Recommendation, StringComparison.Ordinal)
            && string.Equals(row.Actor, "model-scout", StringComparison.OrdinalIgnoreCase)
            && string.Equals(row.CorrelationId, decision.AuditCorrelationId, StringComparison.Ordinal)
            && string.Equals(
                NormalizeAuditFunctionType(row.FunctionType),
                NormalizeAuditFunctionType(requestedFunctionType),
                StringComparison.Ordinal)
            && string.Equals(row.AfterProvider?.Trim(), selected.Provider.Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(row.AfterModel?.Trim(), selected.Model.Trim(), StringComparison.OrdinalIgnoreCase)
            && HasStructuredRecommendationEvidence(row.EvidenceJson));
    }

    private static bool HasStructuredRecommendationEvidence(string? evidenceJson)
    {
        if (string.IsNullOrWhiteSpace(evidenceJson))
            return false;

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(evidenceJson);
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static string NormalizeAuditFunctionType(string? functionType)
        => ModelScoutFunctionEvidence.NormalizeFunctionType(functionType);

    private static IReadOnlyCollection<string> ExpandFunctionAuditKeys(string? functionType)
    {
        var canonical = NormalizeAuditFunctionType(functionType);
        if (canonical.Equals(FunctionTypes.AgentChat, StringComparison.OrdinalIgnoreCase))
        {
            return [FunctionTypes.AgentChat, "agent-loop", "assistant-chat", "chatbot", "chat"];
        }

        if (canonical.Equals(FunctionTypes.TextEmbedding, StringComparison.OrdinalIgnoreCase))
        {
            return [FunctionTypes.TextEmbedding, "embedding", "embeddings"];
        }

        return [canonical];
    }

    private static async Task<EmbeddingModelIdentity> GetEmbeddingCompatibilityIdentityAsync(
        IServiceProvider services,
        CancellationToken ct)
    {
        var identity = new EmbeddingModelIdentity(
            EmbeddingModelDefaults.Provider,
            EmbeddingModelDefaults.Model,
            EmbeddingModelDefaults.Dimension,
            EmbeddingModelDefaults.ArtifactDigest);
        var pin = services.GetService<IEmbeddingModelPin>();
        if (pin is null)
        {
            return identity;
        }

        try
        {
            return await pin.GetAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return identity;
        }
    }

    private static string EmbeddingCompatibilityMutationGuidance(string command)
        => $"The text-embedding route is an immutable compatibility pin: {EmbeddingModelDefaults.Provider}/{EmbeddingModelDefaults.Model} ({EmbeddingModelDefaults.Dimension} dimensions). model-scout.{command} cannot change or dynamically prove it because existing vectors must remain in one embedding space. Use an explicit semantic-index rebuild workflow to change embedding models.";

    private static async Task<CoreScoutCommandResult> ExecuteHostControlAsync(
        IServiceProvider services,
        ILlmRoutingAuditStore audit,
        CommandAuditContext intent,
        string command,
        string controlScope,
        string eventKind,
        string functionType,
        Func<IModelScoutHostControl, ModelScoutHostControlContext, CancellationToken,
            Task<ModelScoutHostControlResult>> operation)
    {
        var host = services.GetService<IModelScoutHostControl>()
                   ?? new UnavailableModelScoutHostControl();
        var context = new ModelScoutHostControlContext(
            intent.CorrelationId,
            intent.IntentAuditId
            ?? throw new InvalidOperationException("The Model Scout control intent has no audit id."),
            intent.SessionId,
            intent.TurnId);
        ModelScoutHostControlResult result;
        try
        {
            // Once the approval intent is durable, finish the host operation and
            // terminal receipt even if the originating chat turn is cancelled.
            result = await operation(host, context, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = new ModelScoutHostControlResult(
                false,
                $"The host-specific Model Scout control failed: {SafeExceptionMessage(ex)}");
        }

        var outcome = result.Success
                      && Enum.IsDefined(result.Outcome)
                      && result.Outcome != ModelScoutHostControlOutcome.NotApplied
            ? result.Outcome
            : ModelScoutHostControlOutcome.NotApplied;
        var succeeded = result.Success && outcome != ModelScoutHostControlOutcome.NotApplied;
        var auditStatus = HostControlAuditStatus(outcome);
        var safeMessage = RoutingDiagnosticSanitizer.Sanitize(result.Message, 1_000)
                          ?? outcome switch
                          {
                              ModelScoutHostControlOutcome.Queued => "The Model Scout host control was queued.",
                              ModelScoutHostControlOutcome.Accepted => "The Model Scout host control was accepted.",
                              ModelScoutHostControlOutcome.Completed => "The Model Scout host control completed.",
                              _ => "The Model Scout host control was not applied.",
                          };
        var receiptReason = outcome switch
        {
            ModelScoutHostControlOutcome.Queued => "Approved Model Scout host control was durably queued.",
            ModelScoutHostControlOutcome.Accepted => "Approved Model Scout host control was accepted for execution.",
            ModelScoutHostControlOutcome.Completed => "Approved Model Scout host control completed.",
            _ => "Approved Model Scout host control was not applied.",
        };
        var receipt = new LlmRoutingAuditEntry
        {
            EventKind = eventKind,
            Actor = LlmPreferenceAuthors.User,
            Trigger = "approved-chat-command",
            FunctionType = functionType,
            BeforeProvider = RoutingDiagnosticSanitizer.Sanitize(result.BeforeProvider, 80),
            BeforeModel = RoutingDiagnosticSanitizer.Sanitize(result.BeforeModel, 240),
            AfterProvider = RoutingDiagnosticSanitizer.Sanitize(result.AfterProvider, 80),
            AfterModel = RoutingDiagnosticSanitizer.Sanitize(result.AfterModel, 240),
            FallbackProvider = RoutingDiagnosticSanitizer.Sanitize(result.FallbackProvider, 80),
            FallbackModel = RoutingDiagnosticSanitizer.Sanitize(result.FallbackModel, 240),
            Reason = receiptReason,
            EvidenceJson = RoutingDiagnosticSanitizer.SanitizeEvidenceJson(
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    command,
                    scope = controlScope,
                    status = auditStatus,
                })),
            IsUserOverride = true,
            Succeeded = succeeded,
            Error = succeeded ? null : receiptReason,
            CorrelationId = intent.CorrelationId,
            SessionId = intent.SessionId,
            TurnId = intent.TurnId,
        };
        try
        {
            await audit.AppendAsync(receipt, CancellationToken.None).ConfigureAwait(false);
            return new CoreScoutCommandResult(
                succeeded,
                safeMessage,
                receipt.AuditId,
                auditStatus);
        }
        catch (Exception auditException)
        {
            var message = succeeded
                ? "The host control may have completed, but its terminal audit receipt failed; inspect the intent receipt before retrying."
                : "The host control reported no change, but its terminal audit receipt failed."
                    + " The durable intent receipt is still available.";
            return AuditOutageResult(intent, message, auditException);
        }
    }

    private static string HostControlAuditStatus(ModelScoutHostControlOutcome outcome) => outcome switch
    {
        ModelScoutHostControlOutcome.Completed => "completed",
        ModelScoutHostControlOutcome.Accepted => "accepted",
        ModelScoutHostControlOutcome.Queued => "queued",
        _ => "not-applied",
    };

    private static string? ParseRouteScope(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "function" => RouteScopeFunction,
        "current-profile" or "profile" or "this-profile" => RouteScopeCurrentProfile,
        _ => null,
    };

    private static async Task<string?> ValidateTargetAsync(
        IServiceProvider services,
        ILlmSettingsProvider settings,
        ILlmProviderRegistry registry,
        string providerName,
        string model,
        bool requiresVision,
        bool requiresToolUse,
        CancellationToken ct)
    {
        try
        {
            var configured = await settings.GetProviderSettingsAsync(providerName.Trim()).ConfigureAwait(false);
            if (configured is null || !configured.IsEnabled || configured.DisabledByHealthCheck)
                return $"Provider '{providerName}' is not configured and healthy.";
            var provider = registry.GetProvider(configured.ProviderName) ?? registry.GetProvider(providerName.Trim());
            if (provider is null) return $"Provider runtime '{providerName}' is not registered.";
            var probe = await ModelScoutProviderProbeCoordinator.ProbeAsync(
                configured,
                provider,
                TimeSpan.FromSeconds(10),
                ct).ConfigureAwait(false);
            if (!probe.IsHealthy)
                return $"Provider '{providerName}' is unreachable: "
                       + (RoutingDiagnosticSanitizer.Sanitize(probe.Message, 300) ?? "no safe diagnostic was returned");
            if (!probe.Models.Contains(model.Trim(), StringComparer.OrdinalIgnoreCase))
                return $"Model '{model}' is not advertised by '{providerName}'.";
            if (requiresVision && !provider.SupportsVision(model.Trim()))
                return $"Model '{model}' has not proven vision support on '{providerName}'.";
            if (requiresToolUse)
            {
                var translators = services.GetService<IToolUseRouteSupport>();
                if (translators?.Supports(configured.ProviderName) != true
                    && translators?.Supports(providerName.Trim()) != true)
                    return $"Model '{model}' cannot be pinned for agent-chat: provider '{providerName}' has no native tool-use translator.";
                var capability = services.GetService<ILlmCapabilitiesService>()
                    ?.GetModelCapability(configured.ProviderName, model.Trim())
                    ?? services.GetService<ILlmCapabilitiesService>()
                        ?.GetModelCapability(providerName.Trim(), model.Trim());
                if (capability?.SupportsFunctionCalling == false)
                    return $"Model '{model}' cannot be pinned for agent-chat because the capability catalog says tool use is unsupported.";
                if (capability?.SupportsFunctionCalling != true
                    && !SubscriptionCliProviders.IsSubscriptionCliProvider(configured.ProviderName)
                    && !SubscriptionCliProviders.IsSubscriptionCliProvider(providerName.Trim()))
                {
                    return $"Model '{model}' cannot be pinned for agent-chat because affirmative model-level tool-use capability is not proven.";
                }
            }
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Could not validate {providerName}/{model}: {SafeExceptionMessage(ex)}";
        }
    }

    private static async Task<LlmFunctionPreference?> GetExactPreferenceAsync(
        ILlmSettingsProvider settings,
        string functionType)
        => (await settings.GetAllFunctionPreferencesAsync().ConfigureAwait(false))
            .FirstOrDefault(item => item.FunctionType.Equals(functionType, StringComparison.OrdinalIgnoreCase));

    private sealed record FailedPreferenceMutationCompensation(
        LlmFunctionPreference? Current,
        string Message);

    private static async Task<FailedPreferenceMutationCompensation> TryCompensateFailedPinMutationAsync(
        ILlmSettingsProvider settings,
        string functionType,
        LlmFunctionPreference? before,
        LlmFunctionPreference requested)
    {
        try
        {
            var rollbackError = await TryRestorePinnedPreferenceAsync(
                settings,
                functionType,
                before,
                requested).ConfigureAwait(false);
            if (rollbackError is null)
            {
                return new FailedPreferenceMutationCompensation(
                    before,
                    "The route write committed before throwing and its exact owned revision was conditionally rolled back.");
            }

            var current = await GetExactPreferenceAsync(settings, functionType).ConfigureAwait(false);
            if (FunctionPreferenceVersion.Capture(functionType, current)
                == FunctionPreferenceVersion.Capture(functionType, before))
            {
                return new FailedPreferenceMutationCompensation(
                    current,
                    "The exact pre-command route is still present; no compensation was needed.");
            }

            return new FailedPreferenceMutationCompensation(
                current,
                "The command does not own the current exact route revision; a concurrent edit was left unchanged.");
        }
        catch (Exception ex)
        {
            return new FailedPreferenceMutationCompensation(
                null,
                $"The route outcome could not be verified safely: {SafeExceptionMessage(ex)}");
        }
    }

    private static async Task<FailedPreferenceMutationCompensation> TryCompensateFailedUnpinMutationAsync(
        ILlmSettingsProvider settings,
        string functionType,
        LlmFunctionPreference before)
    {
        try
        {
            var current = await GetExactPreferenceAsync(settings, functionType).ConfigureAwait(false);
            if (FunctionPreferenceVersion.Capture(functionType, current)
                == FunctionPreferenceVersion.Capture(functionType, before))
            {
                return new FailedPreferenceMutationCompensation(
                    current,
                    "The exact pre-command pin is still present; no compensation was needed.");
            }

            if (current is not null)
            {
                return new FailedPreferenceMutationCompensation(
                    current,
                    "A concurrent exact route edit was detected and left unchanged.");
            }

            return new FailedPreferenceMutationCompensation(
                null,
                "The exact route is absent, so the deletion may have committed. Safe rollback was skipped because physical absence has no owned revision and may include a later pin-then-unpin ABA.");
        }
        catch (Exception ex)
        {
            return new FailedPreferenceMutationCompensation(
                null,
                $"The route outcome could not be verified safely: {SafeExceptionMessage(ex)}");
        }
    }

    private static async Task<string> TryCompensateFailedPolicyMutationAsync(
        IRoutingPolicyStore store,
        RoutingPolicySnapshot before,
        RoutingPolicySnapshot requested)
    {
        try
        {
            var rollbackError = await TryRestorePolicyAsync(store, before, requested).ConfigureAwait(false);
            return rollbackError is null
                ? "The policy write committed before throwing and was conditionally rolled back."
                : $"The policy write was not rolled back because it was not owned by this command: {rollbackError}.";
        }
        catch (Exception ex)
        {
            return $"The policy outcome could not be verified safely: {SafeExceptionMessage(ex)}";
        }
    }

    private static async Task<string?> TryRestorePinnedPreferenceAsync(
        ILlmSettingsProvider settings,
        string functionType,
        LlmFunctionPreference? before,
        LlmFunctionPreference written)
    {
        try
        {
            var expected = FunctionPreferenceVersion.Capture(functionType, written);
            if (before is null)
            {
                var removal = await settings.TryDeleteFunctionPreferenceAsync(
                    functionType,
                    FunctionPreferenceWriteAuthority.UserAuthorizedOverride,
                    expected).ConfigureAwait(false);
                return removal.Applied
                    ? null
                    : "rollback was skipped because the route changed concurrently";
            }

            var restore = await settings.TrySetExactFunctionPreferenceAsync(
                LlmFunctionPreferenceValidation.CreateCompensatingWrite(before),
                FunctionPreferenceWriteAuthority.UserAuthorizedOverride,
                expected).ConfigureAwait(false);
            return restore.Applied
                ? null
                : "rollback was skipped because the route changed concurrently";
        }
        catch (Exception ex)
        {
            return SafeExceptionMessage(ex);
        }
    }

    private static async Task<string?> TryRestorePolicyAsync(
        IRoutingPolicyStore store,
        RoutingPolicySnapshot before,
        RoutingPolicySnapshot written)
    {
        try
        {
            var restore = await store.TrySetAsync(
                before.Policy,
                written,
                Guid.NewGuid().ToString("N"),
                CancellationToken.None).ConfigureAwait(false);
            return restore.Applied
                ? null
                : "rollback was skipped because the policy changed concurrently";
        }
        catch (Exception ex)
        {
            return SafeExceptionMessage(ex);
        }
    }

    private static CoreScoutCommandResult AuditOutageResult(
        CommandAuditContext intent,
        string message,
        Exception auditException)
        => new(
            false,
            $"{message} Audit error: {SafeExceptionMessage(auditException)}",
            intent.IntentAuditId);

    private static async Task<CoreScoutCommandResult> RecordCommandAsync(
        ILlmRoutingAuditStore? audit,
        bool success,
        string eventKind,
        LlmFunctionPreference? route,
        string reason,
        CancellationToken ct,
        string? functionType,
        CommandAuditContext context,
        LlmFunctionPreference? before = null)
    {
        var safeReason = RoutingDiagnosticSanitizer.Sanitize(reason, 2_000)
                         ?? (success ? "Model Scout command completed." : "Model Scout command failed.");
        if (audit is null)
            return new CoreScoutCommandResult(success, safeReason, null);
        var entry = new LlmRoutingAuditEntry
        {
            EventKind = eventKind,
            Actor = LlmPreferenceAuthors.User,
            Trigger = "approved-chat-command",
            FunctionType = NormalizeAuditFunctionType(route?.FunctionType ?? functionType),
            BeforeProvider = before?.PreferredProvider,
            BeforeModel = before?.PreferredModel,
            AfterProvider = route?.PreferredProvider,
            AfterModel = route?.PreferredModel,
            FallbackProvider = route?.FallbackProvider,
            FallbackModel = route?.FallbackModel,
            Reason = safeReason,
            EvidenceJson = RoutingDiagnosticSanitizer.SanitizeEvidenceJson(
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    beforeFallbackProvider = before?.FallbackProvider,
                    beforeFallbackModel = before?.FallbackModel,
                    afterFallbackProvider = route?.FallbackProvider,
                    afterFallbackModel = route?.FallbackModel,
                })),
            IsUserOverride = true,
            Succeeded = success,
            Error = success ? null : safeReason,
            CorrelationId = context.CorrelationId,
            SessionId = context.SessionId,
            TurnId = context.TurnId,
        };
        await audit.AppendAsync(entry, ct).ConfigureAwait(false);
        return new CoreScoutCommandResult(success, safeReason, entry.AuditId);
    }

    private static async Task<CommandAuditContext> RecordIntentAsync(
        ILlmRoutingAuditStore audit,
        string functionType,
        string reason,
        object requestedChange,
        CurrentTurnContext? turn,
        CancellationToken ct)
    {
        var context = new CommandAuditContext(
            Guid.NewGuid().ToString("N"),
            Clean(turn?.ConversationId),
            turn?.TurnId?.ToString("N"));
        var safeReason = RoutingDiagnosticSanitizer.Sanitize(reason, 2_000)
                         ?? "User approved a Model Scout control change.";
        var intent = new LlmRoutingAuditEntry
        {
            EventKind = LlmRoutingAuditEventKinds.ControlRequested,
            Actor = LlmPreferenceAuthors.User,
            Trigger = "approved-chat-command",
            FunctionType = NormalizeAuditFunctionType(functionType),
            Reason = safeReason,
            EvidenceJson = RoutingDiagnosticSanitizer.SanitizeEvidenceJson(
                System.Text.Json.JsonSerializer.Serialize(requestedChange)),
            IsUserOverride = true,
            Succeeded = true,
            CorrelationId = context.CorrelationId,
            SessionId = context.SessionId,
            TurnId = context.TurnId,
        };
        await audit.AppendAsync(intent, ct).ConfigureAwait(false);
        return context with { IntentAuditId = intent.AuditId };
    }

    private sealed record CommandAuditContext(
        string CorrelationId,
        string? SessionId,
        string? TurnId,
        string? IntentAuditId = null);

    private static CoreScoutAuditSummary ToSummary(LlmRoutingAuditEntry entry) => new(
        entry.AuditId,
        entry.OccurredAtUtc,
        entry.EventKind,
        entry.Actor,
        RoutingDiagnosticSanitizer.Sanitize(entry.FunctionType, 160),
        string.IsNullOrWhiteSpace(entry.AfterProvider)
            ? null
            : $"{RoutingDiagnosticSanitizer.Sanitize(entry.AfterProvider, 80)}/{RoutingDiagnosticSanitizer.Sanitize(entry.AfterModel, 240)}",
        RoutingDiagnosticSanitizer.Sanitize(entry.Reason, 2_000),
        entry.Succeeded,
        entry.SessionId,
        entry.TurnId,
        entry.CorrelationId,
        BuildEvidenceSummary(entry.EvidenceJson));

    private static string BuildPlainEnglishExplanation(
        CoreScoutAuditSummary row,
        CoreScoutEvidenceSummary? correlatedEvidence)
    {
        var routeEvidence = row.Evidence;
        var evidence = routeEvidence?.Candidates.Count > 0 ? routeEvidence : correlatedEvidence;
        var isPendingExecution = row.EventKind == LlmRoutingAuditEventKinds.ExecutionStarted;
        var outcome = isPendingExecution
            ? "is pending a terminal receipt; if none follows, the stream was interrupted"
            : row.Succeeded ? "succeeded" : "failed";
        var weights = evidence is null
                      || evidence.CapabilityWeight is null
                      || evidence.SpeedWeight is null
                      || evidence.CostWeight is null
            ? string.Empty
            : $" The capability/speed/cost weights were {evidence.CapabilityWeight:0.##}/{evidence.SpeedWeight:0.##}/{evidence.CostWeight:0.##}.";
        var alternatives = evidence?.Candidates.Count > 0
            ? " Compared routes: " + string.Join("; ", evidence.Candidates.Take(4).Select(DescribeEvidenceCandidate)) + "."
            : string.Empty;
        var fallbackTransition = routeEvidence is not null
                                 && !string.Equals(
                                     routeEvidence.BeforeFallbackRoute,
                                     routeEvidence.AfterFallbackRoute,
                                     StringComparison.OrdinalIgnoreCase)
                                 && (routeEvidence.BeforeFallbackRoute is not null
                                     || routeEvidence.AfterFallbackRoute is not null)
            ? $" The fallback changed from {routeEvidence.BeforeFallbackRoute ?? "none"} to {routeEvidence.AfterFallbackRoute ?? "none"}."
            : string.Empty;
        var action = row.EventKind switch
        {
            LlmRoutingAuditEventKinds.Execution => "used",
            LlmRoutingAuditEventKinds.ExecutionStarted => "started streaming from",
            _ => "selected or changed",
        };
        return $"{row.Actor} {action} {row.Route ?? "no executable route"} for "
               + $"{row.FunctionType ?? "this workload"} because {row.Reason ?? "no reason was recorded"}."
               + weights + alternatives + fallbackTransition + $" The action {outcome}. Receipt {row.AuditId}.";
    }

    private static CoreScoutEvidenceSummary? BuildEvidenceSummary(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var root = document.RootElement;
            var summary = new CoreScoutEvidenceSummary();
            summary.BeforeFallbackRoute = EvidenceRoute(
                root,
                "beforeFallbackProvider",
                "beforeFallbackModel");
            summary.AfterFallbackRoute = EvidenceRoute(
                root,
                "afterFallbackProvider",
                "afterFallbackModel");
            if (TryJsonProperty(root, "request", out var request)
                && TryJsonProperty(request, "weights", out var weights))
            {
                summary.CapabilityWeight = JsonDecimal(weights, "capability");
                summary.SpeedWeight = JsonDecimal(weights, "speed");
                summary.CostWeight = JsonDecimal(weights, "cost");
            }

            if (TryJsonProperty(root, "candidates", out var candidates)
                && candidates.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var candidate in candidates.EnumerateArray().Take(8))
                {
                    var provider = RoutingDiagnosticSanitizer.Sanitize(JsonString(candidate, "provider"), 80);
                    var model = RoutingDiagnosticSanitizer.Sanitize(JsonString(candidate, "model"), 240);
                    if (provider is null || model is null) continue;
                    _ = TryJsonProperty(candidate, "score", out var score);
                    summary.Candidates.Add(new CoreScoutEvidenceCandidate(
                        $"{provider}/{model}",
                        JsonDecimal(score, "total"),
                        JsonDecimal(score, "capability"),
                        JsonDecimal(score, "speed"),
                        JsonDecimal(score, "cost"),
                        RoutingDiagnosticSanitizer.Sanitize(JsonString(candidate, "rejectionReason"), 500),
                        JsonDecimal(candidate, "capabilityScore"),
                        JsonDecimal(candidate, "estimatedLatencyMs"),
                        JsonDecimal(candidate, "estimatedCostUsd"),
                        JsonDecimal(candidate, "reliabilityScore")));
                }
            }

            // Runtime Selection/Execution receipts deliberately retain a compact
            // bounded decision instead of the full Recommendation payload. Parse
            // that schema directly so "why did you use that model?" remains useful
            // even when the recommendation audit was suppressed.
            if (summary.Candidates.Count == 0
                && TryJsonProperty(root, "modelScoutDecision", out var runtimeDecision)
                && runtimeDecision.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (TryJsonProperty(runtimeDecision, "appliedWeights", out var appliedWeights))
                {
                    summary.CapabilityWeight = JsonDecimal(appliedWeights, "capability");
                    summary.SpeedWeight = JsonDecimal(appliedWeights, "speed");
                    summary.CostWeight = JsonDecimal(appliedWeights, "cost");
                }

                if (TryJsonProperty(runtimeDecision, "selected", out var selected))
                    AddRuntimeDecisionCandidate(summary, selected, isSelected: true);
                if (TryJsonProperty(runtimeDecision, "alternatives", out var alternatives)
                    && alternatives.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var alternative in alternatives.EnumerateArray().Take(7))
                        AddRuntimeDecisionCandidate(summary, alternative, isSelected: false);
                }
            }
            return summary;
        }
        catch (System.Text.Json.JsonException)
        {
            return new CoreScoutEvidenceSummary { EvidenceReadable = false };
        }
    }

    private static void AddRuntimeDecisionCandidate(
        CoreScoutEvidenceSummary summary,
        System.Text.Json.JsonElement candidate,
        bool isSelected)
    {
        var provider = RoutingDiagnosticSanitizer.Sanitize(JsonString(candidate, "provider"), 80);
        var model = RoutingDiagnosticSanitizer.Sanitize(JsonString(candidate, "model"), 240);
        if (provider is null || model is null) return;
        var eligible = JsonBoolean(candidate, "eligible");
        var reason = RoutingDiagnosticSanitizer.Sanitize(JsonString(candidate, "reason"), 500);
        summary.Candidates.Add(new CoreScoutEvidenceCandidate(
            $"{provider}/{model}",
            JsonDecimal(candidate, "total"),
            JsonDecimal(candidate, "capability"),
            JsonDecimal(candidate, "speed"),
            JsonDecimal(candidate, "cost"),
            !isSelected && eligible == false ? reason ?? "failed an eligibility gate" : null,
            JsonDecimal(candidate, "capabilityScore"),
            JsonDecimal(candidate, "estimatedLatencyMs"),
            JsonDecimal(candidate, "estimatedCostUsd"),
            JsonDecimal(candidate, "reliabilityScore")));
    }

    private static string DescribeEvidenceCandidate(CoreScoutEvidenceCandidate candidate)
    {
        if (candidate.RejectionReason is not null)
            return $"{candidate.Route} rejected because {candidate.RejectionReason}";

        var normalized = $"scored {candidate.TotalScore:0.###} "
                         + $"(capability {candidate.CapabilityScore:0.###}, speed {candidate.SpeedScore:0.###}, cost {candidate.CostScore:0.###})";
        var raw = new List<string>();
        if (candidate.RawCapabilityScore is { } capability) raw.Add($"measured capability {capability:0.###}");
        if (candidate.EstimatedLatencyMs is { } latency) raw.Add($"latency {latency:0.##} ms");
        if (candidate.EstimatedCostUsd is { } cost) raw.Add($"estimated cost ${cost:0.######}");
        if (candidate.MeasuredReliability is { } reliability) raw.Add($"measured reliability {reliability:0.###}");
        return raw.Count == 0
            ? $"{candidate.Route} {normalized}"
            : $"{candidate.Route} {normalized}; {string.Join(", ", raw)}";
    }

    private static bool TryJsonProperty(
        System.Text.Json.JsonElement element,
        string name,
        out System.Text.Json.JsonElement value)
    {
        if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static string? JsonString(System.Text.Json.JsonElement element, string name)
        => TryJsonProperty(element, name, out var value)
           && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString()
            : null;

    private static decimal? JsonDecimal(System.Text.Json.JsonElement element, string name)
        => TryJsonProperty(element, name, out var value)
           && value.ValueKind == System.Text.Json.JsonValueKind.Number
           && value.TryGetDecimal(out var number)
            ? number
            : null;

    private static bool? JsonBoolean(System.Text.Json.JsonElement element, string name)
        => TryJsonProperty(element, name, out var value)
           && value.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static string? EvidenceRoute(
        System.Text.Json.JsonElement element,
        string providerProperty,
        string modelProperty)
    {
        var provider = RoutingDiagnosticSanitizer.Sanitize(JsonString(element, providerProperty), 80);
        var model = RoutingDiagnosticSanitizer.Sanitize(JsonString(element, modelProperty), 240);
        return provider is null || model is null ? null : $"{provider}/{model}";
    }

    private static ModelScoutTier ParseTier(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "economy" => ModelScoutTier.Economy,
        "frontier" => ModelScoutTier.Frontier,
        _ => ModelScoutTier.Balanced,
    };

    private static string SafeExceptionMessage(Exception ex)
        => RoutingDiagnosticSanitizer.Sanitize(ex.Message, 300) ?? "provider validation failed";

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Registers the host-neutral Scout tools after the capability catalog exists.</summary>
internal sealed class ModelScoutChatToolRegistrationService(
    ICapabilityCatalog catalog,
    IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Mutating Scout tools must never be exposed in a host that cannot issue
        // durable receipts. Fail composition at startup instead of silently
        // changing routing without an audit trail. Both collaborators may be
        // profile-scoped, so validate them inside a temporary scope rather than
        // resolving them from this hosted singleton's root provider.
        using (var scope = services.CreateScope())
        {
            _ = scope.ServiceProvider.GetRequiredService<ILlmRoutingAuditStore>();
            _ = scope.ServiceProvider.GetRequiredService<IRoutingPolicyStore>();
            _ = scope.ServiceProvider.GetRequiredService<ILlmSettingsProvider>();
            _ = scope.ServiceProvider.GetRequiredService<IModelScout>();
            _ = scope.ServiceProvider.GetRequiredService<ILlmProviderRegistry>();
            _ = scope.ServiceProvider.GetRequiredService<IModelScoutHostControl>();
        }
        foreach (var tool in ModelScoutChatTools.Create(services)) catalog.Register(tool);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class CoreScoutStatusRequest { public int RecentDecisions { get; set; } = 10; }
public sealed class CoreScoutStatusResult
{
    public List<CoreScoutProvider> Providers { get; init; } = [];
    public List<CoreScoutRoute> Routes { get; init; } = [];
    public List<CoreScoutAuditSummary> Recent { get; init; } = [];
    public CoreScoutPolicy? Policy { get; init; }
    public bool HostControlsAvailable { get; init; }
    public string HostControlAvailability { get; init; } = "Host-specific controls are unavailable.";
    public bool? AutoManageEnabled { get; init; }
    public CoreScoutCurrentProfileRoute? CurrentProfileRoute { get; init; }
    public List<string> Warnings { get; init; } = [];
}
public sealed record CoreScoutProvider(string Provider, string? DefaultModel, bool Enabled, bool Local, bool HealthDisabled, string? Error);
public sealed record CoreScoutRoute(
    string FunctionType,
    string Provider,
    string Model,
    string? FallbackProvider,
    string? FallbackModel,
    bool Enabled,
    bool UserPinned,
    bool FixedCompatibilityPin);
public sealed record CoreScoutCurrentProfileRoute(
    string? ProfileName,
    string? Provider,
    string? Model,
    string Mode);
public sealed record CoreScoutAuditSummary(
    string AuditId,
    DateTime OccurredAtUtc,
    string EventKind,
    string Actor,
    string? FunctionType,
    string? Route,
    string? Reason,
    bool Succeeded,
    string? SessionId,
    string? TurnId,
    string? CorrelationId,
    CoreScoutEvidenceSummary? Evidence);
public sealed class CoreScoutEvidenceSummary
{
    public bool EvidenceReadable { get; init; } = true;
    public string? BeforeFallbackRoute { get; set; }
    public string? AfterFallbackRoute { get; set; }
    public decimal? CapabilityWeight { get; set; }
    public decimal? SpeedWeight { get; set; }
    public decimal? CostWeight { get; set; }
    public List<CoreScoutEvidenceCandidate> Candidates { get; } = [];
}
public sealed record CoreScoutEvidenceCandidate(
    string Route,
    decimal? TotalScore,
    decimal? CapabilityScore,
    decimal? SpeedScore,
    decimal? CostScore,
    string? RejectionReason,
    decimal? RawCapabilityScore,
    decimal? EstimatedLatencyMs,
    decimal? EstimatedCostUsd,
    decimal? MeasuredReliability);
public sealed record CoreScoutPolicy(decimal? MonthlyCloudBudgetUsd, bool UsesCustomWeights, decimal CapabilityWeight, decimal SpeedWeight, decimal CostWeight);
public sealed class CoreScoutRecommendRequest
{
    public string FunctionType { get; set; } = "";
    public string? Provider { get; set; }
    public string? Tier { get; set; }
    public bool Interactive { get; set; } = true;
    public bool LocalOnly { get; set; }
    public bool RequiresVision { get; set; }
    public bool RequiresToolUse { get; set; }
    public bool RequiresStructuredOutput { get; set; }
    public int? MinimumContextTokens { get; set; }
    public decimal? MaximumCostUsd { get; set; }
    public int? LatencyTargetMs { get; set; }
    public decimal? MinimumCapabilityScore { get; set; }
    public bool RequireCurrentFunctionEvidence { get; set; }
}
public sealed record CoreScoutRecommendResult(bool Success, string Reason, string? Provider, string? Model, string? AuditReceiptId, string? CorrelationId)
{
    public static CoreScoutRecommendResult Failure(string reason) => new(false, reason, null, null, null, null);
}
public sealed class CoreScoutExplainRequest
{
    public string? AuditId { get; set; }
    public string? CorrelationId { get; set; }
    public string? SessionId { get; set; }
    public string? TurnId { get; set; }
    public string? FunctionType { get; set; }
    public int Limit { get; set; } = 10;
}
public sealed record CoreScoutExplainResult(string Explanation, List<CoreScoutAuditSummary> Entries);
public sealed class CoreScoutPinRequest
{
    public string FunctionType { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public string? Scope { get; set; }
    public string? FallbackProvider { get; set; }
    public string? FallbackModel { get; set; }
    public string? Reason { get; set; }
    public bool RequiresVision { get; set; }
}
public sealed class CoreScoutUnpinRequest
{
    public string FunctionType { get; set; } = "";
    public string? Scope { get; set; }
    public string? Reason { get; set; }
}
public sealed record CoreScoutCommandResult(
    bool Success,
    string Message,
    string? AuditReceiptId,
    string? Outcome = null)
{
    public static CoreScoutCommandResult Failure(string message) => new(false, message, null);
}
public sealed class CoreScoutPolicyRequest
{
    public decimal? MonthlyCloudBudgetUsd { get; set; }
    public bool ClearMonthlyCloudBudget { get; set; }
    public decimal? CapabilityWeight { get; set; }
    public decimal? SpeedWeight { get; set; }
    public decimal? CostWeight { get; set; }
    public bool ResetWeights { get; set; }
    public string? Reason { get; set; }
}
public sealed class CoreScoutAutoManageRequest { public bool? Enabled { get; set; } }
public sealed class CoreScoutOptimizeRequest { public string? FunctionType { get; set; } }
public sealed class CoreScoutBenchmarkRequest
{
    public string FunctionType { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public bool AllowMeteredProvider { get; set; }
}
public sealed class CoreScoutRecoveryRequest { public string FunctionType { get; set; } = ""; }
