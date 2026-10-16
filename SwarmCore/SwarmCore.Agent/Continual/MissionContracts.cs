namespace SwarmCore.Agent.Continual;

/// <summary>Trusted caller's outcome oracle for one exact external operation. Inspection is
/// read-only and must return the assigned content digest; it never authorizes action replay.</summary>
public sealed record MissionOutcomeInspection(string ToolName, string DispatchInputJson, string InspectUrl, string ExpectedBodySha256);

/// <summary>A mission is a continual objective with a versioned delivery contract, not another scheduler.</summary>
public sealed record MissionDefinition
{
    public int SchemaVersion { get; init; } = 1;
    public SwarmCore.Abstractions.CodingTasks.CodingRunState? CodingRun { get; init; }
    public SwarmCore.Abstractions.Security.SecurityRunState? SecurityRun { get; init; }
    public required string OwnerId { get; init; }
    public int ConstraintVersion { get; init; } = 1;
    public IReadOnlyList<string> Constraints { get; init; } = [];
    public required IReadOnlyList<string> SuccessCriteria { get; init; }
    public required IReadOnlyList<MissionEvidenceRequirement> EvidenceRequirements { get; init; }
    public IReadOnlyList<string> ResourceReferences { get; init; } = [];
    public DateTimeOffset? DeadlineUtc { get; init; }
    public IReadOnlyList<MissionOutcomeInspection> OutcomeInspections { get; init; } = [];
    public string? BudgetReference { get; init; }
    public SwarmCore.Abstractions.Budgets.MissionBudgetLimits? BudgetLimits { get; init; }
    public Guid? ParentId { get; init; }
    public MissionDelegation? Delegation { get; init; }
    public MissionDelegationLimits? DelegationLimits { get; init; }
    public bool PlanIsModelAuthored { get; init; }
    public IReadOnlyList<MissionAcceptanceCriterion> AcceptanceCriteria { get; init; } = [];
    /// <summary>Only an explicitly assigned limit stops repair attempts; null preserves continual work.</summary>
    public int? MaximumAttempts { get; init; }
    /// <summary>Trusted host temporal assignment. Null means current evidence; model plans cannot grant historical exceptions.</summary>
    public DateTimeOffset? EvidenceEffectiveAtUtc { get; init; }
    public DateTimeOffset? EvidenceKnownAtUtc { get; init; }
    /// <summary>Task scheduling intent; these fields do not impose a mission execution deadline.</summary>
    public DateTimeOffset? ReminderDueAtUtc { get; init; }
    public DateOnly? ReminderDueDate { get; init; }
    public string? ReminderTimeZoneId { get; init; }
    /// <summary>Trusted host bindings for schema artifacts. Model-authored plans cannot populate this list.</summary>
    public IReadOnlyList<MissionResource> SchemaResources { get; init; } = [];

    public void Validate()
    {
        foreach (var inspection in OutcomeInspections)
        {
            if (inspection.ToolName is not ("http.request" or "missions.browser" or "computer.act")
                || inspection.ExpectedBodySha256.Length != 64 || !inspection.ExpectedBodySha256.All(Uri.IsHexDigit)
                || !Uri.TryCreate(inspection.InspectUrl, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https")
                || url.UserInfo.Length != 0 || url.Fragment.Length != 0
                || System.Text.Json.Nodes.JsonNode.Parse(inspection.DispatchInputJson) is not System.Text.Json.Nodes.JsonObject)
                throw new ArgumentException("Outcome inspection requires an exact external tool input, read endpoint and expected content digest.");
        }
        BudgetLimits?.Validate();
        if (SchemaVersion != 1 || ConstraintVersion < 1)
            throw new ArgumentException("Unsupported mission contract version.");
        ArgumentException.ThrowIfNullOrWhiteSpace(OwnerId);
        if (SchemaResources.Any(r => r.Kind is not (MissionResourceKind.File or MissionResourceKind.Directory)
            || !Path.IsPathFullyQualified(r.Identity)))
            throw new ArgumentException("Schema bindings must identify absolute host file or directory resources.");
        if (EvidenceKnownAtUtc is not null && EvidenceEffectiveAtUtc is null)
            throw new ArgumentException("An as-known evidence scope requires an effective time.");
        if (MaximumAttempts is <= 0 || AcceptanceCriteria.Any(c => string.IsNullOrWhiteSpace(c.Id) || string.IsNullOrWhiteSpace(c.Description))
            || AcceptanceCriteria.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != AcceptanceCriteria.Count)
            throw new ArgumentException("Acceptance IDs must be unique and an assigned attempt limit must be positive.");
        if (AcceptanceCriteria.Any(c => !Enum.IsDefined(c.Method) || c.RequiredEvidenceKinds.Any(k => !Enum.IsDefined(k))
            || c.Method != CriterionValidationMethod.Semantic && c.Receipt is null))
            throw new ArgumentException("Acceptance methods and evidence kinds must be supported, with a selector for deterministic checks.");
        if (SuccessCriteria.Count == 0 || SuccessCriteria.Any(string.IsNullOrWhiteSpace)
            || EvidenceRequirements.Any(e => string.IsNullOrWhiteSpace(e.ToolName)))
            throw new ArgumentException("A mission requires success criteria and named tool evidence requirements.");
        foreach (var evidence in EvidenceRequirements.Where(e => e.InputJson is not null))
        {
            try
            {
                if (System.Text.Json.Nodes.JsonNode.Parse(evidence.InputJson!) is not System.Text.Json.Nodes.JsonObject)
                    throw new ArgumentException("Evidence input selectors must be JSON objects.");
            }
            catch (System.Text.Json.JsonException ex) { throw new ArgumentException("Evidence input selectors must be valid JSON.", ex); }
        }
    }
}

/// <summary>A successful receipt is necessary evidence; the host acceptance auditor must also accept delivery.</summary>
public sealed record MissionEvidenceRequirement(string ToolName, bool MustBeReadOnly = true,
    string? InputJson = null, string? ResultContains = null)
{
    /// <summary>Host declaration that this check depends on tracked file contents, not external state.</summary>
    public bool BindToFileArtifacts { get; init; }
}
public sealed record MissionCheckpoint(string CurrentWork, IReadOnlyList<string> PendingWork, string? ResultReference = null);
public sealed record MissionEvent(Guid ObjectiveId, long Version, DateTimeOffset AtUtc, string Kind, string? Detail);
public sealed record MissionTargetClarification(string Question, IReadOnlyList<string> Candidates,
    int AuthorityRevision, string? SelectedTarget = null);
public enum MissionDependencyKind { Credential, OsInteraction, SoftwareRepair }
public sealed record MissionExternalDependency(MissionDependencyKind Kind, string Reference, string RequiredChange,
    IReadOnlyList<string> DiagnosticReceiptIds, IReadOnlyList<string> AlternativeReceiptIds,
    string CompletedWork, string VerifiedReason, string InteractionId, bool Resolved = false);

public enum MissionControl { Pause, Resume, Cancel }

/// <summary>Explicit transition table. Idle is legacy stopped work, never successful delivery.</summary>
public static class MissionTransitions
{
    public static bool CanControl(ContinualObjectiveStatus status, MissionControl control) => control switch
    {
        MissionControl.Pause => status is ContinualObjectiveStatus.InProgress or ContinualObjectiveStatus.RetryScheduled
            or ContinualObjectiveStatus.VerificationPending or ContinualObjectiveStatus.WaitingForInput,
        MissionControl.Resume => status is ContinualObjectiveStatus.Paused or ContinualObjectiveStatus.WaitingForInput or ContinualObjectiveStatus.Uncertain,
        MissionControl.Cancel => !ContinualObjectiveStateMachine.IsTerminal(status),
        _ => false,
    };

    public static bool NeedsInspection(ContinualToolReceipt receipt)
        => receipt.WasDispatched && receipt.SideEffect != ContinualSideEffectClass.ReadOnly
            && !receipt.LocalProcessCompleted
            && (receipt.State == ContinualToolReceiptState.Started || receipt.IsError);

    public static string? MissingEvidence(ContinualObjectiveSnapshot objective)
    {
        if (objective.Mission is not { } mission) return null;
        if (objective.Receipts.Any(NeedsInspection))
            return "A started operation has an uncertain outcome and needs inspection.";
        // A successful check before a later mutation is historical evidence. Require fresh
        // checks after the last artifact-changing action, including actions from later turns.
        // Checkpoints describe work; they do not change the delivered artifact.
        var lastMutation = objective.Receipts
            .Where(r => r.WasDispatched && r.SideEffect != ContinualSideEffectClass.ReadOnly && r.ToolName != "missions.checkpoint")
            .Select(r => r.Sequence).DefaultIfEmpty(-1).Max();
        foreach (var required in mission.EvidenceRequirements)
        {
            if (!objective.Receipts.Any(r => r.ToolName == required.ToolName && r.State == ContinualToolReceiptState.Completed
                && !r.IsError && r.ArtifactEvidenceError is null && !string.IsNullOrWhiteSpace(r.Result)
                && r.WasDispatched && (r.Sequence >= lastMutation
                    || required.BindToFileArtifacts && SameArtifactsAfterLaterActions(r, objective.Receipts))
                && (!required.MustBeReadOnly || r.SideEffect == ContinualSideEffectClass.ReadOnly)
                && (required.ResultContains is null || r.Result.Contains(required.ResultContains, StringComparison.Ordinal))
                && (required.InputJson is null || EquivalentInput(required.InputJson, r.InputJson))))
                return $"Missing successful acceptance evidence from {required.ToolName}.";
        }
        return null;
    }

    private static bool SameArtifactsAfterLaterActions(ContinualToolReceipt evidence, IReadOnlyList<ContinualToolReceipt> receipts)
        => evidence.ArtifactHashes is { Count: > 0 } hashes && receipts
            .Where(r => r.Sequence > evidence.Sequence && r.WasDispatched
                && r.SideEffect != ContinualSideEffectClass.ReadOnly && r.ToolName != "missions.checkpoint")
            .All(r => r.ArtifactEvidenceError is null && r.ArtifactHashes is { } after
                && after.Count == hashes.Count && hashes.All(p => after.TryGetValue(p.Key, out var value) && value == p.Value));

    internal static bool EquivalentInput(string expected, string? actual)
    {
        if (actual is null) return false;
        try { return Matches(System.Text.Json.Nodes.JsonNode.Parse(expected), System.Text.Json.Nodes.JsonNode.Parse(actual)); }
        catch (System.Text.Json.JsonException) { return false; }
    }
    private static bool Matches(System.Text.Json.Nodes.JsonNode? expected, System.Text.Json.Nodes.JsonNode? actual)
        => expected is System.Text.Json.Nodes.JsonObject obj && actual is System.Text.Json.Nodes.JsonObject saved
            ? obj.All(p => saved.TryGetPropertyValue(p.Key, out var value) && Matches(p.Value, value))
            : System.Text.Json.Nodes.JsonNode.DeepEquals(expected, actual);
}

public interface IMissionStore : IContinualObjectiveStore
{
    Task<bool> BindOutcomeInspectionAsync(Guid id, string partition, long expectedVersion, MissionOutcomeInspection inspection, CancellationToken ct = default)
        => Task.FromResult(false);
    Task<bool> UpdateDeadlineAsync(Guid id, string partition, long expectedVersion, DateTimeOffset? deadlineUtc, CancellationToken ct = default)
        => Task.FromResult(false);
    /// <summary>Trusted host/user compare-and-swap that gives an existing root mission (no parent, not
    /// yet budgeted) its first budget account reference. Never callable for a model-authored plan's own
    /// request and never replaces an already-assigned reference.</summary>
    Task<bool> AssignBudgetReferenceAsync(Guid id, string partition, long expectedVersion, string budgetReference, CancellationToken ct = default)
        => Task.FromResult(false);
    Task<ContinualObjectiveLease?> TryClaimSecurityAsync(Guid id, long expectedVersion, string ownerId,
        DateTimeOffset nowUtc, DateTimeOffset expiresUtc, CancellationToken ct = default)
        => Task.FromResult<ContinualObjectiveLease?>(null);

    Task<ContinualObjectiveSnapshot?> TryPrepareSecurityWorkspaceAsync(Guid id, long expectedVersion,
        SwarmCore.Abstractions.CodingTasks.CodingWorkspaceIdentity workspace, CancellationToken ct = default)
        => throw new NotSupportedException("Durable security workspace preparation is unavailable.");

    Task<ContinualObjectiveSnapshot?> TryUpdateSecurityRunAsync(Guid id, long expectedVersion,
        SwarmCore.Abstractions.Security.SecurityRunState updated, CancellationToken ct = default)
        => Task.FromResult<ContinualObjectiveSnapshot?>(null);
    /// <summary>Host-only compare-and-swap of preparation ownership and current coding evidence.</summary>
    Task<ContinualObjectiveSnapshot?> TryUpdateCodingRunAsync(Guid id, long expectedVersion,
        SwarmCore.Abstractions.CodingTasks.CodingRunState updated, MissionAuthority? preparedResources = null,
        CancellationToken ct = default) => Task.FromResult<ContinualObjectiveSnapshot?>(null);
    Task<MissionSteeringCommit> ApplySteeringAsync(SwarmCore.Agent.Runtime.AgentTurnSteeringRequest request,
        SwarmCore.Agent.Runtime.AgentTurnSteeringRegistration registration, DateTimeOffset now, CancellationToken ct = default)
        => Task.FromResult(new MissionSteeringCommit(null, new(false, "mission-steering-store-unavailable")));
    Task<bool> InitializeLegacyAuthorityAsync(Guid id, string leaseToken, MissionAuthority authority, CancellationToken ct = default)
        => Task.FromResult(false);
    Task<bool> SaveConstraintConflictAsync(Guid id, string leaseToken, MissionConstraintConflict conflict, CancellationToken ct = default)
        => Task.FromResult(false);
    /// <summary>Durable record that two verified attached children touched the same source path in their
    /// isolated workspaces with different final content. Refuses a silent join; no automatic three-way merge.</summary>
    Task<bool> SaveIntegrationConflictAsync(Guid id, string leaseToken, MissionIntegrationConflict conflict, CancellationToken ct = default)
        => Task.FromResult(false);
    Task<bool> AttachRecoveryProofAsync(Guid id, string leaseToken, string toolCallId, string recoveryJson, CancellationToken ct = default)
        => Task.FromResult(false);
    Task<bool> SaveExternalDependencyAsync(Guid id, string leaseToken, MissionExternalDependency dependency, CancellationToken ct = default)
        => Task.FromResult(false);
    Task<IReadOnlyList<ContinualObjectiveSnapshot>> ListDependencyWaitersAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ContinualObjectiveSnapshot>>([]);
    Task<bool> ResumeDependencyAsync(Guid id, string interactionId, CancellationToken ct = default) => Task.FromResult(false);
    Task<bool> SaveTargetClarificationAsync(Guid id, string leaseToken, MissionTargetClarification clarification, CancellationToken ct = default)
        => Task.FromResult(false);
    Task<bool> ResolveTargetClarificationAsync(Guid id, string partition, int authorityRevision, string target, CancellationToken ct = default)
        => Task.FromResult(false);
    Task<bool> SetDerivedAuthorityAsync(Guid id, string leaseToken, int expectedRevision, MissionAuthority authority, CancellationToken ct = default)
        => Task.FromResult(false);
    Task<MissionDelegationResult> CreateDelegationAsync(Guid parentId, string parentLeaseToken, long expectedVersion,
        ContinualObjectiveStart child, MissionDelegationLimits limits, DateTimeOffset nowUtc, CancellationToken ct = default)
        => Task.FromResult(new MissionDelegationResult("unsupported", null, "Atomic mission delegation is not configured."));
    Task<bool> SaveJoinWaitAsync(Guid parentId, string parentLeaseToken, MissionJoinWait wait, CancellationToken ct = default)
        => Task.FromResult(false);
    Task<bool> RetireDelegationAsync(Guid parentId, string parentLeaseToken, Guid childId, long expectedChildVersion,
        DateTimeOffset nowUtc, CancellationToken ct = default) => Task.FromResult(false);
    Task<bool> MessageChildAsync(Guid parentId, string parentLeaseToken, Guid childId, string message, DateTimeOffset nowUtc,
        CancellationToken ct = default, MissionDelegation? contextBinding = null) => Task.FromResult(false);
    Task<IReadOnlyList<ContinualObjectiveSnapshot>> ListChildrenAsync(Guid parentId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ContinualObjectiveSnapshot>>([]);
    Task<bool> BindTurnAsync(Guid id, string leaseToken, Guid turnId, string? provider, string? model, CancellationToken ct = default);
    Task<bool> ReconcileReceiptAsync(Guid id, string leaseToken, ContinualToolReceipt receipt, CancellationToken ct = default);
    Task<bool> ReconcileReceiptAsync(Guid id, string leaseToken, ContinualToolReceipt receipt,
        int inspectedAuthorityRevision, int inspectedConstraintRevision, CancellationToken ct = default)
        => Task.FromResult(false);
    Task ReleaseExecutionAsync(Guid id, string leaseToken, CancellationToken ct = default);
    Task<IReadOnlyList<ContinualObjectiveSnapshot>> ListAsync(string partitionKey, int take = 50, CancellationToken ct = default);
    Task<bool> ControlAsync(Guid id, long version, MissionControl control, string reason, DateTimeOffset now, CancellationToken ct = default);
    Task<bool> CheckpointAsync(Guid id, string leaseToken, MissionCheckpoint checkpoint, DateTimeOffset now, CancellationToken ct = default);
    Task<IReadOnlyList<MissionEvent>> EventsAsync(Guid id, long afterVersion = -1, int take = 100, CancellationToken ct = default);
}

public sealed record MissionConstraintConflict(string ExclusionProvenance, string RequiredOutcome,
    IReadOnlyList<string> EvidenceReceiptIds, string CompletedWork, string VerifiedReason);

/// <summary>Two verified attached children wrote different final content to the same source path inside their
/// own isolated workspaces. Both contributions are preserved; recovery is a fresh reviewable assignment, never
/// a silent overwrite or an automatic three-way merge.</summary>
public sealed record MissionIntegrationConflict(IReadOnlyList<string> Paths, Guid FirstChildId, Guid SecondChildId,
    DateTimeOffset DetectedAtUtc);

/// <summary>Host-owned inspection of unfinished operations. Never re-execute an external action here.</summary>
public interface IContinualObjectiveRecovery
{
    Task ReconcileAsync(ContinualObjectiveExecutionRequest request, CancellationToken ct = default);
}

/// <summary>Host-owned final inspection; model claims never substitute for current artifact evidence.</summary>
public interface IContinualObjectiveEvidenceValidator
{
    Task<string?> ValidateEvidenceAsync(ContinualObjectiveSnapshot objective, CancellationToken ct = default);
}
