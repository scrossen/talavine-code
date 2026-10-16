using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting.Services;

/// <summary>
/// Mandatory final Scout boundary for physical inference, including router
/// fallbacks and repairs. A recommendation for a different route cannot be
/// laundered into execution by changing the selection's provider/model fields.
/// </summary>
public static class ModelScoutExecutionAdmission
{
    /// <summary>Scout-owned capability research cannot recursively require the
    /// capability it is measuring. Exact, bounded local probes share the same
    /// physical execution ledger without creating production clearance.</summary>
    public static async Task<T> ExecuteLocalResearchAsync<T>(
        ILlmRoutingAuditStore? audit, string functionType, string provider, string model,
        TimeSpan timeout, CancellationToken cancellationToken, Func<CancellationToken, Task<T>> execute)
    {
        if (audit is null || !LlmProviderLocalityPolicy.IsLocalProviderName(provider)
            || string.IsNullOrWhiteSpace(functionType) || string.IsNullOrWhiteSpace(model)
            || timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(15))
            throw new ModelScoutAdmissionException("Scout local research requires exact identity, a deadline and a durable audit store.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(timeout);
        var correlation = Guid.NewGuid().ToString("N");
        LlmRoutingAuditEntry Entry(string kind, bool success) => new()
        {
            EventKind = kind, Actor = "model-scout", Trigger = "research",
            FunctionType = functionType, AfterProvider = provider, AfterModel = model,
            CorrelationId = correlation, Succeeded = success,
            Reason = "Exact local capability research; no production clearance granted",
            EvidenceJson = "{\"admissionMode\":\"exact-research\"}",
        };
        await audit.AppendAsync(Entry(LlmRoutingAuditEventKinds.ExecutionStarted, true), lifetime.Token).ConfigureAwait(false);
        T result;
        try { result = await execute(lifetime.Token).ConfigureAwait(false); }
        catch
        {
            await audit.AppendAsync(Entry(LlmRoutingAuditEventKinds.Execution, false), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        await audit.AppendAsync(Entry(LlmRoutingAuditEventKinds.Execution, true), CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    public static void AdmitExactConstraint(string functionType, LlmModelSelection selection, string kind)
    {
        if (string.IsNullOrWhiteSpace(functionType) || string.IsNullOrWhiteSpace(selection.Provider)
            || string.IsNullOrWhiteSpace(selection.Model)
            || kind is not ("exact-user" or "manual" or "embedding-generation" or "exact-research"))
            throw new ModelScoutAdmissionException("An exact Scout constraint is incomplete.");
        selection.ModelScoutConstraintAdmission = new(functionType, selection.Provider, selection.Model,
            kind, DateTime.UtcNow.AddMinutes(5));
    }

    public static void ValidateDecision(string functionType, LlmModelSelection selection)
    {
        if (selection.ModelScoutConstraintAdmission is { } constraint)
        {
            if (!Same(constraint.FunctionType, functionType) || !Same(constraint.Provider, selection.Provider)
                || !Same(constraint.Model, selection.Model) || constraint.ExpiresAtUtc <= DateTime.UtcNow)
                throw new ModelScoutAdmissionException("The exact Scout constraint changed or expired.");
            return;
        }
        var receipt = selection.ModelScoutDecisionReceipt;
        if (receipt is null
            || receipt.ExpiresAtUtc <= DateTime.UtcNow
            || !receipt.Selected.Eligible
            || !Same(receipt.Selected.Provider, selection.Provider)
            || !Same(receipt.Selected.Model, selection.Model)
            || !Same(receipt.FunctionType, functionType)
            || string.IsNullOrWhiteSpace(selection.AuditCorrelationId))
        {
            throw new ModelScoutAdmissionException(
                $"Model Scout has not admitted {selection.Provider}/{selection.Model} for {functionType}.");
        }
        if ((selection.RequireCurrentFunctionEvidence || receipt.FunctionEvidence is not null)
            && !ModelScoutFunctionEvidence.IsCoherent(receipt.FunctionEvidence,
                functionType, selection.Provider, selection.Model))
        {
            throw new ModelScoutAdmissionException("Model Scout execution evidence is missing, stale or belongs to another route/function.");
        }
    }

    private static bool Same(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left) && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}

public sealed class ModelScoutAdmissionException(string message) : InvalidOperationException(message);
