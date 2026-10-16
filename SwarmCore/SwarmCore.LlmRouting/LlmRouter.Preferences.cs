using SwarmCore.Abstractions.LlmRouting;
using Microsoft.Extensions.Logging;

namespace SwarmCore.LlmRouting;

public partial class LlmRouter
{
    // ===== Preference Management =====

    public async Task SetFunctionPreferenceAsync(string functionType, string provider, string model)
    {
        await SetFunctionPreferenceAsync(functionType, provider, model, null, null, null, null);
    }

    /// <param name="userId">
    /// Caller-minted actor id for the audit trail. Pass <see cref="LlmPreferenceAuthors.User"/>
    /// for a human-initiated change, a subsystem tag otherwise. A display name is
    /// acceptable here, but a caller that passes one must state authority through
    /// <paramref name="userPinned"/> rather than relying on this value.
    /// </param>
    /// <param name="userPinned">
    /// Whether this write establishes a user pin. Defaults to inferring from the
    /// <see cref="LlmPreferenceAuthors.User"/> actor sentinel, which is what every
    /// in-tree caller passes today.
    /// </param>
    public async Task<bool> SetFunctionPreferenceAsync(string functionType, string provider, string model,
        string? fallbackProvider = null, string? fallbackModel = null, string? userId = null, bool? requiresVision = null,
        bool? userPinned = null)
    {
        functionType = CanonicalizeRoutingFunctionType(functionType);
        var isUserPin = userPinned ?? LlmPreferenceAuthors.IsUserActor(userId);
        if (!LlmFunctionPreferenceValidation.TryValidateFallbackPair(
                fallbackProvider,
                fallbackModel,
                out var fallbackError))
        {
            throw new ArgumentException(fallbackError);
        }

        var before = await TryReadPreferenceForAuditAsync(functionType);
        var beforeExact = await ReadExactFunctionPreferenceAsync(functionType).ConfigureAwait(false);
        var auditCorrelationId = Guid.NewGuid().ToString("N");
        var preference = new LlmFunctionPreference
        {
            FunctionType = functionType,
            PreferredProvider = provider,
            PreferredModel = model,
            FallbackProvider = string.IsNullOrWhiteSpace(fallbackProvider) ? null : fallbackProvider.Trim(),
            FallbackModel = string.IsNullOrWhiteSpace(fallbackModel) ? null : fallbackModel.Trim(),
            IsEnabled = true,
            RequiresVision = requiresVision ?? false,
            UpdatedAt = DateTime.UtcNow,
            UpdatedBy = userId,
            PinnedByUserAt = isUserPin ? DateTime.UtcNow : null,
            AuditCorrelationId = auditCorrelationId,
            MutationRevision = Guid.NewGuid().ToString("N"),
        };

        // Commit intent before touching settings so a missing/unavailable audit
        // ledger cannot permit an unaudited route mutation. The terminal receipt
        // below records whether the protected write actually applied.
        await AppendPreferenceAuditAsync(
            LlmRoutingAuditEventKinds.ControlRequested,
            functionType,
            before,
            preference,
            actor: userId ?? "llm-router",
            trigger: isUserPin ? "user-request" : "router-api",
            reason: "Function route change requested",
            succeeded: true,
            correlationId: auditCorrelationId);

        var authority = isUserPin
            ? FunctionPreferenceWriteAuthority.UserAuthorizedOverride
            : FunctionPreferenceWriteAuthority.RespectUserPin;
        FunctionPreferenceConditionalWriteResult write;
        try
        {
            write = await _settingsProvider.TrySetExactFunctionPreferenceAsync(
                preference,
                authority,
                FunctionPreferenceVersion.Capture(functionType, beforeExact)).ConfigureAwait(false);
        }
        catch (Exception writeException)
        {
            var recovery = await RecoverFailedFunctionPreferenceWriteAsync(
                functionType,
                beforeExact,
                preference).ConfigureAwait(false);
            try
            {
                await AppendPreferenceAuditAsync(
                    LlmRoutingAuditEventKinds.PreferenceChanged,
                    functionType,
                    before,
                    recovery.Current,
                    actor: userId ?? "llm-router",
                    trigger: isUserPin ? "user-request" : "router-api",
                    reason: $"Function route write acknowledgement failed. {recovery.Message}",
                    succeeded: false,
                    correlationId: auditCorrelationId);
            }
            catch (Exception auditException)
            {
                throw new InvalidOperationException(
                    $"Function preference update for '{functionType}' failed during write acknowledgement and terminal audit. {recovery.Message}",
                    new AggregateException(writeException, auditException));
            }
            throw new InvalidOperationException(
                $"Function preference update for '{functionType}' failed during write acknowledgement. {recovery.Message}",
                writeException);
        }
        var success = write.Applied;
        var committed = success ? write.Current ?? preference : null;
        var ambiguousOwnedWrite = !success && OwnsMutationRevision(write.Current, preference);
        if (ambiguousOwnedWrite)
        {
            var rollbackError = await TryRollbackFunctionPreferenceWriteAsync(
                functionType,
                beforeExact,
                write.Current!).ConfigureAwait(false);
            if (rollbackError is not null)
            {
                await AppendPreferenceAuditAsync(
                    LlmRoutingAuditEventKinds.PreferenceChanged,
                    functionType,
                    before,
                    write.Current,
                    actor: userId ?? "llm-router",
                    trigger: isUserPin ? "user-request" : "router-api",
                    reason: $"Settings returned {write.Status} for the caller-owned revision; outcome is uncertain because {rollbackError}.",
                    succeeded: false,
                    correlationId: auditCorrelationId);
                throw new InvalidOperationException(
                    $"Function preference update for '{functionType}' returned {write.Status} for its caller-owned revision and may remain committed because {rollbackError}.");
            }
            committed = beforeExact ?? before;
        }
        try
        {
            await AppendPreferenceAuditAsync(
                LlmRoutingAuditEventKinds.PreferenceChanged,
                functionType,
                before,
                committed,
                actor: userId ?? "llm-router",
                trigger: isUserPin ? "user-request" : "router-api",
                reason: success
                    ? "Function route pinned"
                    : ambiguousOwnedWrite
                        ? $"Settings returned {write.Status} after committing the caller-owned revision; the ambiguous write was conditionally rolled back"
                        : "Preference write was refused or lost an exact-route race",
                succeeded: success,
                correlationId: auditCorrelationId);
        }
        catch (Exception auditException)
        {
            var rollbackError = success && committed is not null
                ? await TryRollbackFunctionPreferenceWriteAsync(
                    functionType,
                    beforeExact,
                    committed).ConfigureAwait(false)
                : null;
            var outcome = !success
                ? "No function route mutation was reported."
                : rollbackError is null
                    ? "The committed route was conditionally rolled back."
                    : $"The route may remain committed because {rollbackError}.";
            throw new InvalidOperationException(
                $"Function preference update for '{functionType}' could not commit its terminal audit receipt. {outcome}",
                auditException);
        }

        if (success)
        {
            _logger.LogInformation("Set preference for {FunctionType}: {Provider}/{Model} by {UserId}",
                functionType, provider, model, userId ?? "system");
            _knownGoodTracker?.NotifyConfigChanged(functionType, provider, model);
        }
        else
        {
            _logger.LogWarning(
                "Failed to set preference for {FunctionType}: {Status}",
                functionType,
                write.Status);
        }
        return success;
    }

    private static bool OwnsMutationRevision(
        LlmFunctionPreference? current,
        LlmFunctionPreference requested)
        => current is not null
           && !string.IsNullOrWhiteSpace(requested.MutationRevision)
           && string.Equals(
               current.MutationRevision,
               requested.MutationRevision,
               StringComparison.Ordinal);

    private async Task<LlmFunctionPreference?> ReadExactFunctionPreferenceAsync(string functionType)
        => (await _settingsProvider.GetAllFunctionPreferencesAsync().ConfigureAwait(false))
            .FirstOrDefault(item => item.FunctionType.Equals(
                functionType,
                StringComparison.OrdinalIgnoreCase));

    private sealed record FailedFunctionPreferenceWriteRecovery(
        LlmFunctionPreference? Current,
        string Message);

    private async Task<FailedFunctionPreferenceWriteRecovery> RecoverFailedFunctionPreferenceWriteAsync(
        string functionType,
        LlmFunctionPreference? beforeExact,
        LlmFunctionPreference requested)
    {
        LlmFunctionPreference? current;
        try
        {
            current = await ReadExactFunctionPreferenceAsync(functionType).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return UnverifiableFunctionPreferenceWrite(ex);
        }

        var beforeVersion = FunctionPreferenceVersion.Capture(functionType, beforeExact);
        if (FunctionPreferenceVersion.Capture(functionType, current) == beforeVersion)
        {
            return new FailedFunctionPreferenceWriteRecovery(
                current,
                "The exact route still matches its pre-write version, so no rollback was needed.");
        }

        if (!OwnsMutationRevision(current, requested))
        {
            return new FailedFunctionPreferenceWriteRecovery(
                current,
                "A concurrent exact route state was observed and left unchanged.");
        }

        var rollbackError = await TryRollbackFunctionPreferenceWriteAsync(
            functionType,
            beforeExact,
            current!).ConfigureAwait(false);

        try
        {
            current = await ReadExactFunctionPreferenceAsync(functionType).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var context = rollbackError is null
                ? "The caller-owned revision was reported conditionally rolled back, but its final exact state"
                : "The caller-owned revision was detected, but its final exact state after the rollback attempt";
            return UnverifiableFunctionPreferenceWrite(ex, context);
        }

        if (rollbackError is null)
        {
            return new FailedFunctionPreferenceWriteRecovery(
                current,
                "The exact caller-owned revision committed before the failure and was conditionally rolled back. The terminal after-state was re-read from exact storage.");
        }

        if (FunctionPreferenceVersion.Capture(functionType, current) == beforeVersion)
        {
            return new FailedFunctionPreferenceWriteRecovery(
                current,
                $"The exact route now matches its pre-write version and no caller-owned revision remains; the rollback attempt reported that {rollbackError}.");
        }

        if (!OwnsMutationRevision(current, requested))
        {
            return new FailedFunctionPreferenceWriteRecovery(
                current,
                $"A concurrent exact route state was observed after the caller-owned revision was detected and was left unchanged; the conditional rollback did not apply because {rollbackError}.");
        }

        return new FailedFunctionPreferenceWriteRecovery(
            current,
            $"The exact caller-owned revision remains observed because {rollbackError}.");
    }

    private static FailedFunctionPreferenceWriteRecovery UnverifiableFunctionPreferenceWrite(
        Exception exception,
        string context = "The exact route outcome")
        => new(
            Current: null,
            $"{context} could not be verified safely: {exception.GetType().Name}. No exact after-state is claimed.");

    private async Task<string?> TryRollbackFunctionPreferenceWriteAsync(
        string functionType,
        LlmFunctionPreference? beforeExact,
        LlmFunctionPreference committed)
    {
        try
        {
            var committedVersion = FunctionPreferenceVersion.Capture(functionType, committed);
            var rollback = beforeExact is null
                ? await _settingsProvider.TryDeleteFunctionPreferenceAsync(
                    functionType,
                    FunctionPreferenceWriteAuthority.UserAuthorizedOverride,
                    committedVersion).ConfigureAwait(false)
                : await _settingsProvider.TrySetExactFunctionPreferenceAsync(
                    LlmFunctionPreferenceValidation.CreateCompensatingWrite(beforeExact),
                    FunctionPreferenceWriteAuthority.UserAuthorizedOverride,
                    committedVersion).ConfigureAwait(false);
            return rollback.Applied
                ? null
                : "conditional rollback was skipped because a newer exact route won";
        }
        catch (Exception ex)
        {
            return $"conditional rollback failed with {ex.GetType().Name}";
        }
    }

    public async Task<LlmModelSelection?> GetFunctionPreferenceAsync(string functionType)
    {
        functionType = CanonicalizeRoutingFunctionType(functionType);
        var preference = await _settingsProvider.GetFunctionPreferenceAsync(functionType);
        if (preference == null || !preference.IsEnabled)
        {
            return null;
        }

        return new LlmModelSelection
        {
            Provider = preference.PreferredProvider,
            Model = preference.PreferredModel,
            FallbackProvider = preference.FallbackProvider,
            FallbackModel = preference.FallbackModel,
            IsDefault = false,
            Reason = $"Function preference for {functionType}"
        };
    }

    public async Task<Dictionary<string, LlmModelSelection>> GetAllFunctionPreferencesAsync()
    {
        var preferences = await _settingsProvider.GetAllFunctionPreferencesAsync();
        var result = new Dictionary<string, LlmModelSelection>();

        foreach (var pref in preferences.Where(p => p.IsEnabled))
        {
            result[pref.FunctionType] = new LlmModelSelection
            {
                Provider = pref.PreferredProvider,
                Model = pref.PreferredModel,
                FallbackProvider = pref.FallbackProvider,
                FallbackModel = pref.FallbackModel,
                IsDefault = false,
                Reason = $"Function preference for {pref.FunctionType}"
            };
        }

        return result;
    }

    public async Task<List<LlmFunctionPreference>> GetAllFunctionPreferencesDetailedAsync()
    {
        return await _settingsProvider.GetAllFunctionPreferencesAsync();
    }

    public async Task ClearFunctionPreferenceAsync(string functionType)
    {
        functionType = CanonicalizeRoutingFunctionType(functionType);
        var before = (await _settingsProvider.GetAllFunctionPreferencesAsync())
            .FirstOrDefault(item => item.FunctionType.Equals(functionType, StringComparison.OrdinalIgnoreCase));
        var auditCorrelationId = Guid.NewGuid().ToString("N");
        await AppendPreferenceAuditAsync(
            LlmRoutingAuditEventKinds.ControlRequested,
            functionType,
            before,
            after: null,
            actor: "user",
            trigger: "user-request",
            reason: "Function route clear requested",
            succeeded: true,
            correlationId: auditCorrelationId);
        FunctionPreferenceConditionalWriteResult removal;
        try
        {
            removal = before is null
                ? new FunctionPreferenceConditionalWriteResult(
                    FunctionPreferenceConditionalWriteStatus.Rejected)
                : await _settingsProvider.TryDeleteFunctionPreferenceAsync(
                    functionType,
                    FunctionPreferenceWriteAuthority.UserAuthorizedOverride,
                    FunctionPreferenceVersion.Capture(functionType, before));
        }
        catch (Exception ex)
        {
            var compensation = await TryCompensateUncertainFunctionClearAsync(
                functionType,
                before);
            LlmFunctionPreference? observed = null;
            try
            {
                observed = await ReadExactFunctionPreferenceAsync(functionType).ConfigureAwait(false);
                await AppendPreferenceAuditAsync(
                    LlmRoutingAuditEventKinds.PreferenceCleared,
                    functionType,
                    before,
                    observed,
                    actor: "user",
                    trigger: "user-request",
                    reason: $"Function route clear acknowledgement failed. {compensation}",
                    succeeded: false,
                    correlationId: auditCorrelationId);
            }
            catch (Exception auditException)
            {
                throw new InvalidOperationException(
                    $"Function preference clear for '{functionType}' failed during write acknowledgement and terminal audit. {compensation}",
                    new AggregateException(ex, auditException));
            }
            throw new InvalidOperationException(
                $"Function preference clear for '{functionType}' failed. {compensation}",
                ex);
        }

        var success = removal.Applied;
        var currentAfter = removal.Current;
        if (!success && currentAfter is null)
        {
            currentAfter = await ReadExactFunctionPreferenceAsync(functionType).ConfigureAwait(false);
        }
        var ambiguousAbsent = !success
                              && removal.Status == FunctionPreferenceConditionalWriteStatus.Conflict
                              && currentAfter is null;
        if (success)
        {
            _logger.LogInformation("Cleared preference for {FunctionType}", functionType);
        }
        else
        {
            _logger.LogWarning(
                ambiguousAbsent
                    ? "Function preference clear for {FunctionType} returned conflict while the exact row was already absent; outcome is uncertain or another clear already won"
                    : "Failed to clear preference for {FunctionType}; a current exact row was left unchanged",
                functionType);
        }
        try
        {
            await AppendPreferenceAuditAsync(
                LlmRoutingAuditEventKinds.PreferenceCleared,
                functionType,
                before,
                after: success ? null : currentAfter,
                actor: "user",
                trigger: "user-request",
                reason: success
                    ? "Function route unpinned"
                    : ambiguousAbsent
                        ? "The delete returned conflict while the exact route was already absent. The clear outcome is uncertain or another clear already won; absence was kept."
                        : "No function route was cleared because a newer exact route exists and was left unchanged",
                succeeded: success,
                correlationId: auditCorrelationId);
        }
        catch (Exception auditException)
        {
            var outcome = !success
                ? ambiguousAbsent
                    ? "The delete returned conflict while the exact route was already absent. The outcome is uncertain or another clear already won; absence was kept."
                    : "No function route mutation was reported; the current exact row was left unchanged."
                : "The clear committed before its terminal receipt failed; current storage was left untouched. Safe rollback was skipped because physical absence has no owned revision and may include a later pin-then-unpin ABA.";
            throw new InvalidOperationException(
                $"Function preference clear for '{functionType}' could not commit its terminal audit receipt. {outcome}",
                auditException);
        }
    }

    private async Task<string> TryCompensateUncertainFunctionClearAsync(
        string functionType,
        LlmFunctionPreference? before)
    {
        try
        {
            var current = (await _settingsProvider.GetAllFunctionPreferencesAsync())
                .FirstOrDefault(item => item.FunctionType.Equals(functionType, StringComparison.OrdinalIgnoreCase));
            if (FunctionPreferenceVersion.Capture(functionType, current)
                == FunctionPreferenceVersion.Capture(functionType, before))
            {
                return "The exact route still matches its pre-clear version, so no rollback was needed.";
            }

            if (before is null || current is not null)
            {
                return "A concurrent exact route edit was detected and left unchanged.";
            }

            return "The exact route is absent, so the clear may have committed. Safe rollback was skipped because physical absence has no owned revision and may include a later pin-then-unpin ABA.";
        }
        catch (Exception ex)
        {
            return $"The exact route outcome could not be verified safely: {ex.GetType().Name}.";
        }
    }

    private async Task AppendPreferenceAuditAsync(
        string eventKind,
        string functionType,
        LlmFunctionPreference? before,
        LlmFunctionPreference? after,
        string actor,
        string trigger,
        string reason,
        bool succeeded,
        string correlationId)
    {
        functionType = CanonicalizeRoutingFunctionType(functionType);
        try
        {
            await _routingAuditStore.AppendAsync(new LlmRoutingAuditEntry
            {
                EventKind = eventKind,
                Actor = actor,
                Trigger = trigger,
                FunctionType = functionType,
                BeforeProvider = before?.PreferredProvider,
                BeforeModel = before?.PreferredModel,
                AfterProvider = after?.PreferredProvider,
                AfterModel = after?.PreferredModel,
                FallbackProvider = after?.FallbackProvider,
                FallbackModel = after?.FallbackModel,
                Reason = reason,
                CorrelationId = correlationId,
                IsUserOverride = LlmPreferenceAuthors.IsUserActor(actor),
                Succeeded = succeeded,
                Error = succeeded ? null : reason
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to append preference audit for {FunctionType}", functionType);
            throw new InvalidOperationException(
                $"Function preference operation for '{functionType}' was blocked or reported uncertain because its audit receipt could not be committed.",
                ex);
        }
    }

    // ===== Cost Reporting =====

    public async Task<LlmCostSummary> GetCostSummaryAsync(DateTime startDate, DateTime endDate)
    {
        return await _usageTracker.GetCostSummaryAsync(null, startDate, endDate);
    }

    public async Task<LlmCostSummary> GetCostSummaryAsync(string? functionType = null, DateTime? startDate = null, DateTime? endDate = null)
    {
        return await _usageTracker.GetCostSummaryAsync(functionType, startDate, endDate);
    }

    public async Task<List<LlmCostBreakdown>> GetCostBreakdownAsync(DateTime startDate, DateTime endDate, string groupBy = "function")
    {
        return await _usageTracker.GetCostBreakdownAsync(startDate, endDate, groupBy);
    }

    public async Task<List<LlmCostBreakdown>> GetCostBreakdownAsync(TimeSpan period, string groupBy = "function")
    {
        return await _usageTracker.GetCostBreakdownAsync(period, groupBy);
    }

    public async Task<List<LlmUsageRecord>> GetRecentUsageAsync(int count = 100)
    {
        return await _usageTracker.GetRecentUsageAsync(null, count);
    }

    public async Task<List<LlmUsageRecord>> GetRecentUsageAsync(string? functionType = null, int limit = 100)
    {
        return await _usageTracker.GetRecentUsageAsync(functionType, limit);
    }

    // ===== Configuration Management =====

    public Task ReloadCostsAsync()
    {
        // The cost service handles reloading through its provider.
        _logger.LogInformation("Cost reload requested - delegating to cost service");
        return _costService.ReloadCostsAsync();
    }
}

