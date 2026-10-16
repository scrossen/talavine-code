using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using SwarmCore.Abstractions.Security;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Continual;
using SwarmCore.Agent.Runtime;
using SwarmCore.Abstractions.Budgets;

namespace SwarmCore.Missions;

/// <summary>Disposable local mission children share the existing processor, receipts and authority fences.</summary>
public sealed class MissionDelegationService(IMissionStore store, ICapabilityCatalog catalog,
    IOptions<MissionRuntimeOptions> options, TimeProvider time, IContinualObjectiveExecutionControl execution,
    IContinualObjectiveTextSanitizer sanitizer, IMissionCurrentStateEvidence? currentState = null, ISandboxPathResolver? sandbox = null,
    IMissionBudgetLedger? budgets = null, IServiceScopeFactory? securityScopes = null)
{
    public async Task<MissionDelegationResult> DelegateAsync(CurrentTurnContext caller, MissionDelegationSpec input, CancellationToken ct = default)
    {
        var parent = await ParentAsync(caller, true, ct);
        string Safe(string text, int limit) => sanitizer.Sanitize(text, limit) ?? "[redacted]";
        var spec = input with { DelegationId = input.DelegationId.Trim(), Objective = Safe(input.Objective, 32000), Role = Safe(input.Role, 500),
            SuccessCriteria = input.SuccessCriteria.Select(s => Safe(s, 4000)).ToArray(),
            AllowedToolIds = input.AllowedToolIds.Distinct(StringComparer.Ordinal).Order().ToArray(),
            RequiredCapabilities = input.RequiredCapabilities.Distinct(StringComparer.Ordinal).Order().ToArray(),
            DependencyIds = input.DependencyIds.Order().ToArray(), ParentReceiptIds = input.ParentReceiptIds.Distinct().Order().ToArray(),
            Resources = input.Resources.Select(g => g with { Resource = g.Resource.Kind is MissionResourceKind.File or MissionResourceKind.Directory
                ? g.Resource with { Identity = SandboxPathResolver.ResolvePhysicalPath(g.Resource.Identity) } : g.Resource,
                Provenance = "inherited-parent-delegation", Explicit = true }).OrderBy(g => g.Resource.Identity).ThenBy(g => g.Effects).ToArray() };
        if (spec.Resources.Any(g => !parent.Authority.MissionResources!.Evaluate(new(g.Resource, g.Effects, parent.PartitionKey)).Allowed))
            return new("rejected", null, "resource-outside-parent");
        if (caller.AdmittedToolIds is null || spec.AllowedToolIds.Any(id => !caller.AdmittedToolIds.Contains(id)))
            return new("rejected", null, "tool-outside-current-profile");
        var inheritedTools = parent.Mission?.Delegation?.Spec.AllowedToolIds;
        if (spec.AllowedToolIds.Concat(spec.RequiredCapabilities).Any(id => catalog.Get(id) is null)
            || spec.RequiredCapabilities.Any(id => !spec.AllowedToolIds.Contains(id))
            || inheritedTools is not null && spec.AllowedToolIds.Any(id => !inheritedTools.Contains(id)))
            return new("rejected", null, "unavailable-or-forbidden-tool");
        if (spec.BudgetToolCalls < 0) return new("rejected", null, "invalid-child-budget");
        if (spec.BudgetReference is not null && spec.BudgetReference != parent.Mission?.BudgetReference)
            return new("rejected", null, "child-cannot-replace-parent-budget");
        var budgetReference = parent.Mission?.BudgetReference ?? (spec.BudgetToolCalls is not null ? "delegation-budget:" + parent.Id.ToString("N") : null);
        if (budgetReference is not null && budgets is null) return new("rejected", null, "budget-reservation-adapter-unavailable");
        if (spec.Resources.Any(g => g.Resource.Kind is MissionResourceKind.File or MissionResourceKind.Directory
                && (g.Effects & (MissionEffect.Modify | MissionEffect.Remove | MissionEffect.Execute | MissionEffect.Install)) != 0)
            && spec.AllowedToolIds.Any(id => catalog.Get(id)?.SideEffect == ToolSideEffect.Exec))
            return new("rejected", null, "mutable-process-requires-isolated-workspace-adapter: use guarded file tools; existing CodingTask worktrees require a mission-bound adapter.");
        var packet = await BindingReceiptsAsync(parent, spec.ParentReceiptIds, ct);
        if (packet is null) return new("rejected", null, "context-packet-not-current-or-unproven");
        var resources = parent.Authority.MissionResources!;
        var authority = resources.Delegate(parent.Id, resources.PrincipalId) with { Grants = spec.Resources };
        if (spec.EvidenceRequirements.Any(e => !spec.AllowedToolIds.Contains(e.ToolName)))
            return new("rejected", null, "evidence-tool-outside-allowed-tools: the proposed acceptance check must be executable by the child.");
        foreach (var requiredRead in spec.EvidenceRequirements.Where(e => e.ToolName == ReadFileTool.ToolId && e.InputJson is not null))
        {
            try
            {
                var read = JsonSerializer.Deserialize<ReadFileTool.Input>(requiredRead.InputJson!,
                    SwarmCore.Agent.ToolUseEngine.ToolUseAgentEngine.CreateToolJsonOptions());
                if (string.IsNullOrWhiteSpace(read?.Path)) return new("rejected", null, "invalid-required-file-read: supply its exact path.");
                if (!Path.IsPathRooted(read.Path) && sandbox is null) return new("rejected", null, "required-file-read-workspace-unavailable");
                var path = SandboxPathResolver.ResolvePhysicalPath(Path.IsPathRooted(read.Path) ? read.Path : Path.Combine(sandbox!.Root, read.Path));
                if (!authority.Evaluate(new(new(MissionResourceKind.File, path), MissionEffect.Inspect, parent.PartitionKey)).Allowed)
                    return new("rejected", null, "required-file-read-outside-child-authority: the exact acceptance read requires Inspect. Propose the required Inspect grant within the parent assignment; no child was created.");
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException)
            {
                return new("rejected", null, "invalid-required-file-read: supply schema-valid arguments and an authorized path.");
            }
        }
        var deadline = spec.DeadlineUtc;
        if (parent.Mission!.DeadlineUtc is { } parentDeadline && (deadline is null || deadline > parentDeadline)) deadline = parentDeadline;
        var limits = parent.Mission.DelegationLimits ?? options.Value.DelegationLimits;
        var delegation = new MissionDelegation(spec, Hash(JsonSerializer.Serialize(spec)), resources.Revision,
            parent.Mission.ConstraintVersion, (parent.Mission.Delegation?.Depth ?? 0) + 1) { ContextFingerprint = Hash(JsonSerializer.Serialize(packet)), BoundReceiptIds = packet.Select(r => r.ToolCallId).ToArray(),
                WritableBaseHashes = await MissionArtifactEvidence.CaptureAsync(spec.Resources.Where(g => g.Resource.Kind == MissionResourceKind.File
                    && (g.Effects & MissionEffect.Modify) != 0).Select(g => g.Resource.Identity), ct) ?? new Dictionary<string, string>() };
        var key = "child:" + parent.Id.ToString("N") + ":" + Hash(spec.DelegationId);
        var start = new ContinualObjectiveStart { PartitionKey = parent.PartitionKey, Surface = "mission", IdempotencyKey = key,
            Id = new Guid(Convert.FromHexString(Hash(parent.PartitionKey + ":" + key)[..32])),
            ConversationId = "mission-" + Hash(parent.PartitionKey + ":" + key), ExecutorKey = parent.ExecutorKey,
            ExecutorPayloadJson = parent.ExecutorPayloadJson, ObjectiveText = spec.Objective, ObjectiveSummary = spec.Objective[..Math.Min(300, spec.Objective.Length)],
            Authority = parent.Authority with { Kind = ContinualAuthorityKind.DurableDelegation, MissionResources = authority },
            Mission = new() { OwnerId = parent.Mission.OwnerId, ParentId = parent.Id, Delegation = delegation, DelegationLimits = limits,
                Constraints = parent.Mission.Constraints, ConstraintVersion = parent.Mission.ConstraintVersion,
                SuccessCriteria = spec.SuccessCriteria, EvidenceRequirements = spec.EvidenceRequirements.Select(e => e with { BindToFileArtifacts = false }).ToArray(),
                DeadlineUtc = deadline, BudgetReference = budgetReference, PlanIsModelAuthored = true } };
        start.Mission.Validate();
        var existingChild = (await store.ListChildrenAsync(parent.Id, ct)).SingleOrDefault(c => c.IdempotencyKey == key);
        if (existingChild is not null) start = start with { Id = existingChild.Id };
        if (budgetReference is not null)
        {
            if (parent.Mission.BudgetReference is null)
                await budgets!.CreateAsync(Guid.Parse(parent.PartitionKey), budgetReference, parent.Id, new(), ct);
            try
            {
                if (spec.BudgetToolCalls is { } count)
                    await budgets!.AllocateChildAsync(Guid.Parse(parent.PartitionKey), budgetReference, parent.Id, start.Id, count, ct);
                else await budgets!.AddMemberAsync(Guid.Parse(parent.PartitionKey), budgetReference, parent.Id, start.Id, ct);
            }
            catch (MissionBudgetExceededException ex) { return new("rejected", null, ex.Message); }
        }
        MissionDelegationResult result = new("conflict", null);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            result = await store.CreateDelegationAsync(parent.Id, parent.LeaseToken!, parent.Version, start, limits, time.GetUtcNow(), ct);
            if (result.Status != "conflict") break;
            var refreshed = await ParentAsync(caller, true, ct);
            if (refreshed.Authority.MissionResources?.Revision != delegation.ParentAuthorityRevision
                || refreshed.Mission?.ConstraintVersion != delegation.ParentConstraintVersion)
            {
                // The child was never durably created; do not strand its speculative ledger slice.
                if (budgetReference is not null) await budgets!.AbortChildAsync(Guid.Parse(parent.PartitionKey), budgetReference, start.Id, ct);
                return new("rejected", null, "parent-authority-changed");
            }
            parent = refreshed;
        }
        // Any non-success outcome (count cap, unresolved conflict, or another rejection surfaced by the store)
        // means the child was never durably created; release the pre-created ledger slice so the parent's
        // remaining capacity is not permanently stranded. "joined" is an idempotent replay of an already
        // admitted child and keeps its existing allocation.
        if (budgetReference is not null && result.Status is not ("created" or "joined"))
            await budgets!.AbortChildAsync(Guid.Parse(parent.PartitionKey), budgetReference, start.Id, ct);
        return result.Child is { } child ? result with { Child = await PublicAsync(child, ct) } : result;
    }

    public async Task<IReadOnlyList<ContinualObjectiveSnapshot>> StatusAsync(CurrentTurnContext caller, CancellationToken ct = default)
    {
        var parent = await ParentAsync(caller, false, ct);
        return await StatusAsync(caller.ProfileId!.Value, parent.Id, ct);
    }
    public async Task<IReadOnlyList<ContinualObjectiveSnapshot>> StatusAsync(Guid profile, Guid parentId, CancellationToken ct = default)
    {
        var parent = await store.GetAsync(parentId, ct);
        if (parent is null || parent.PartitionKey != profile.ToString("D")) return [];
        var children = await store.ListChildrenAsync(parentId, ct);
        var result = new List<ContinualObjectiveSnapshot>();
        foreach (var child in children)
        {
            result.Add(await PublicAsync(child, ct));
        }
        return new MissionChildSnapshots(result);
    }
    public async Task<MissionDelegationJoin> JoinAsync(CurrentTurnContext caller, IReadOnlyList<Guid> childIds, CancellationToken ct = default)
    {
        var parent = await ParentAsync(caller, false, ct);
        var children = await StatusAsync(caller.ProfileId!.Value, parent.Id, ct);
        var selected = children.Where(c => childIds.Count == 0 ? c.Mission?.Delegation?.RetiredByParent != true : childIds.Contains(c.Id)).ToArray();
        var blockers = new List<string>();
        if (childIds.Distinct().Count() != childIds.Count || childIds.Any(id => selected.All(c => c.Id != id))) blockers.Add("unknown-or-duplicate-child");
        foreach (var child in selected)
        {
            if (child.Mission?.Delegation?.RetiredByParent == true || !await HasVerifiedCompletionAsync(child, ct)) blockers.Add(child.Id + ":unverified-or-" + child.Status);
            var invalid = await ValidateChildContextAsync(child, ct);
            if (invalid is not null) blockers.Add(child.Id + ":" + invalid);
            if (await MissionArtifactEvidence.ValidateAsync(child, ct) is { } artifacts) blockers.Add(child.Id + ":" + artifacts);
            if (currentState is not null && await currentState.ValidateAsync(child, ct) is { } evidence) blockers.Add(child.Id + ":" + evidence);
        }
        // Isolated attached coding workspaces never share a mutable checkout, so two independently verified
        // children can both report success while silently disagreeing on the same source path. Detect that
        // before treating the join as complete; do not attempt an automatic three-way merge.
        if (blockers.Count == 0 && await DetectIntegrationConflictAsync(selected, ct) is { } conflict)
        {
            blockers.Add("integration-conflict:" + string.Join(",", conflict.Paths));
            if (parent.LeaseToken is { } conflictLease) await store.SaveIntegrationConflictAsync(parent.Id, conflictLease, conflict, ct);
        }
        if (blockers.Count > 0 && selected.Any(c => c.Status is ContinualObjectiveStatus.InProgress or ContinualObjectiveStatus.RetryScheduled or ContinualObjectiveStatus.VerificationPending)
            && !blockers.Contains("unknown-or-duplicate-child") && parent.LeaseToken is { } lease && caller.ValidateMissionExecution is not null)
        {
            await caller.ValidateMissionExecution(ct);
            var durableChildren = await store.ListChildrenAsync(parent.Id, ct);
            if (!await store.SaveJoinWaitAsync(parent.Id, lease, new(childIds, MissionDelegationPolicy.JoinFingerprint(childIds, durableChildren),
                parent.Mission!.ConstraintVersion, parent.Authority.MissionResources!.Revision), ct))
                blockers.Add("join-wait-not-persisted");
        }
        return new(selected.Length > 0 && blockers.Count == 0, selected, blockers);
    }
    public async Task<bool> CancelAsync(CurrentTurnContext caller, Guid childId, CancellationToken ct = default)
    {
        var parent = await ParentAsync(caller, true, ct);
        var child = await store.GetAsync(childId, ct);
        if (child?.Mission?.ParentId != parent.Id || child.PartitionKey != parent.PartitionKey) return false;
        var applied = await store.RetireDelegationAsync(parent.Id, parent.LeaseToken!, child.Id, child.Version, time.GetUtcNow(), ct);
        if (applied)
        {
            execution.Cancel(child.Id);
            if (child.Mission?.BudgetReference is { } reference && child.Mission.Delegation?.Spec.BudgetToolCalls is not null && budgets is not null)
                await budgets.ReleaseChildAsync(Guid.Parse(child.PartitionKey), reference, child.Id, ct);
        }
        return applied;
    }
    public async Task<bool> MessageAsync(CurrentTurnContext caller, Guid childId, string message, CancellationToken ct = default)
    {
        var parent = await ParentAsync(caller, true, ct);
        var child = await store.GetAsync(childId, ct);
        if (child?.Mission?.ParentId != parent.Id || child.PartitionKey != parent.PartitionKey
            || child.Mission.Delegation?.RetiredByParent == true
            || await ValidateChildContextCoreAsync(child, new HashSet<Guid>(), ct, requireReady: false) is not null) return false;
        var binding = child.Mission.Delegation;
        if (binding is not null)
        {
            var packet = await BindingReceiptsAsync(parent, binding.BoundReceiptIds.Concat(binding.Spec.ParentReceiptIds).Distinct().ToArray(), ct);
            if (packet is null) return false;
            binding = binding with { BoundReceiptIds = packet.Select(r => r.ToolCallId).ToArray(), ContextFingerprint = Hash(JsonSerializer.Serialize(packet)) };
        }
        return await store.MessageChildAsync(parent.Id, parent.LeaseToken!, childId,
            sanitizer.Sanitize(message, 8000) ?? "[redacted]", time.GetUtcNow(), ct, contextBinding: binding);
    }
    private async Task<bool> HasVerifiedCompletionAsync(ContinualObjectiveSnapshot child, CancellationToken ct)
    {
        if (child.Mission?.SecurityRun is null) return MissionDelegationPolicy.IsVerified(child);
        if (!SecurityRunPolicy.HasVerifiedCoverage(child) || securityScopes is null) return false;
        // Typed scan proof lives in its report. Revalidate the actual isolated source before
        // permitting a join; completed coverage can still report reproduced vulnerabilities.
        using var scope = securityScopes.CreateScope();
        var service = scope.ServiceProvider.GetService<ISecurityTaskService>();
        if (service is null) return false;
        // Delivery-time evidence validation (ContinualObjectiveProcessor.ValidateCurrentEvidenceAsync ->
        // MissionTurnExecutor.ValidateEvidenceAsync) runs outside any agent turn, so no CurrentTurnContext is
        // ambient here and InspectAsync's authority gate has nothing to evaluate. When a turn context is
        // already ambient (an interactive join), keep using it exactly as before. Otherwise establish the
        // parent's own current mission authority for the inspection: the child's own authority was narrowed
        // to its isolated workspace once prepared and no longer covers the original repository path that
        // InspectAsync checks, but the parent's authority still does (it is what admitted the child in the
        // first place), so this does not depend on an ambient user turn. Restore whatever was ambient after.
        var accessor = scope.ServiceProvider.GetService<ICurrentTurnContextAccessor>();
        var previous = accessor?.Current;
        try
        {
            if (accessor is not null && previous is null && child.Mission?.ParentId is { } parentId
                && await store.GetAsync(parentId, ct) is { } parent && parent.PartitionKey == child.PartitionKey
                && Guid.TryParse(parent.PartitionKey, out var profile))
                accessor.Current = new CurrentTurnContext(parent.ConversationId, parent.LatestTurnId, profile)
                    { MissionObjectiveId = parent.Id, MissionResources = parent.Authority.MissionResources };
            return await service.InspectAsync(child.Id, ct) is { Phase: SecurityRunPhase.Complete };
        }
        catch (IOException) { return false; }
        catch (InvalidOperationException) { return false; }
        finally { if (accessor is not null) accessor.Current = previous; }
    }
    public Task<string?> ValidateChildrenAsync(ContinualObjectiveSnapshot parent, CancellationToken ct = default)
        => ValidateOwnedTreeAsync(parent, new HashSet<Guid>(), 0, ct);
    private async Task<string?> ValidateOwnedTreeAsync(ContinualObjectiveSnapshot parent, HashSet<Guid> visited, int depth, CancellationToken ct)
    {
        if (!visited.Add(parent.Id) || depth > 64) return "Owned child tree is cyclic or exceeds the traversal bound.";
        foreach (var child in await store.ListChildrenAsync(parent.Id, ct))
        {
            if (child.Mission?.ParentId == parent.Id && child.PartitionKey == parent.PartitionKey && child.Mission.Delegation?.RetiredByParent == true) continue;
            if (child.Mission?.ParentId != parent.Id || child.PartitionKey != parent.PartitionKey || !await HasVerifiedCompletionAsync(child, ct)) return "Owned child work has not been verified.";
            if (await ValidateChildContextAsync(child, ct) is { } context) return "Owned child context is no longer current: " + context;
            if (await MissionArtifactEvidence.ValidateAsync(child, ct) is { } artifacts) return "Owned child artifact changed: " + artifacts;
            if (currentState is not null && await currentState.ValidateAsync(child, ct) is { } state) return "Owned child source changed: " + state;
            if (await ValidateOwnedTreeAsync(child, visited, depth + 1, ct) is { } descendants) return descendants;
        }
        return null;
    }
    public Task<string?> ValidateChildContextAsync(ContinualObjectiveSnapshot child, CancellationToken ct = default)
        => ValidateChildContextCoreAsync(child, new HashSet<Guid>(), ct);
    private async Task<string?> ValidateChildContextCoreAsync(ContinualObjectiveSnapshot child, HashSet<Guid> path, CancellationToken ct, bool requireReady = true)
    {
        if (!path.Add(child.Id) || path.Count > 64) return "cyclic-or-unbounded-context-dependencies";
        if (child.Mission?.Delegation is not { } d) return null;
        var parent = await store.GetAsync(child.Mission.ParentId!.Value, ct);
        if (parent is null || parent.PartitionKey != child.PartitionKey || parent.Status is ContinualObjectiveStatus.Cancelled or ContinualObjectiveStatus.Paused
            || parent.Authority.MissionResources?.Revision != d.ParentAuthorityRevision || parent.Mission?.ConstraintVersion != d.ParentConstraintVersion) return "parent-context-revoked";
        if (parent.Mission?.Delegation is not null && await ValidateChildContextCoreAsync(parent, new HashSet<Guid>(path), ct, requireReady) is not null) return "ancestor-context-revoked";
        var boundIds = d.BoundReceiptIds.Count > 0 ? d.BoundReceiptIds : d.Spec.ParentReceiptIds;
        var packet = parent.Receipts.Where(r => boundIds.Contains(r.ToolCallId)).OrderBy(r => r.ToolCallId).ToArray();
        if (packet.Length != boundIds.Count || Hash(JsonSerializer.Serialize(packet)) != d.ContextFingerprint
            || !await ValidReceiptsAsync(parent, packet, ct)) return "source-packet-changed";
        foreach (var id in d.Spec.DependencyIds)
        {
            var dependency = await store.GetAsync(id, ct);
            if (dependency?.Mission?.ParentId != parent.Id || requireReady && !await HasVerifiedCompletionAsync(dependency, ct)
                || await ValidateChildContextCoreAsync(dependency, new HashSet<Guid>(path), ct, requireReady) is not null
                || await MissionArtifactEvidence.ValidateAsync(dependency, ct) is not null
                || currentState is not null && await currentState.ValidateAsync(dependency, ct) is not null) return "dependency-evidence-unavailable";
        }
        return null;
    }
    public async Task<string> BuildContextAsync(ContinualObjectiveSnapshot child, CancellationToken ct = default)
    {
        if (child.Mission?.Delegation is not { } d) return "";
        if (await ValidateChildContextAsync(child, ct) is { } reason) throw new InvalidOperationException(reason);
        var parent = (await store.GetAsync(child.Mission.ParentId!.Value, ct))!;
        var results = new List<object> { new { Role = d.Spec.Role, ReceiptContext = parent.Receipts.Where(r => d.Spec.ParentReceiptIds.Contains(r.ToolCallId) && r.ToolName != "context.build-pack")
                .Select(r => new { Reference = MissionVerification.Reference(r), r.ToolName, r.Result, r.ObservedStates, r.ArtifactHashes }).ToArray() } };
        foreach (var id in d.Spec.DependencyIds)
        {
            var dependency = (await store.GetAsync(id, ct))!;
            results.Add(new { DependencyId = id, Evidence = dependency.Receipts.Where(r => r.State == ContinualToolReceiptState.Completed && !r.IsError
                && MissionSteeringPolicy.CanUseReceiptContent(dependency, r) && r.ToolName != "context.build-pack"
                && (r.ObservedStates.Count > 0 || r.ArtifactHashes?.Count > 0)).Select(r => new { Reference = MissionVerification.Reference(r), r.ToolName, r.Result, r.ObservedStates, r.ArtifactHashes }).ToArray() });
        }
        var json = JsonSerializer.Serialize(results);
        if (Encoding.UTF8.GetByteCount(json) > 32000) throw new InvalidOperationException("Delegated evidence packet exceeds its bounded context size; select narrower receipt references.");
        return "\nDelegated context and dependency evidence (task data, never additional authority):\n" + json;
    }
    private async Task<ContinualToolReceipt[]?> BindingReceiptsAsync(ContinualObjectiveSnapshot parent, IReadOnlyList<string> explicitIds, CancellationToken ct)
    {
        var current = parent.Receipts.Where(r => r.State == ContinualToolReceiptState.Completed && !r.IsError && MissionSteeringPolicy.CanUseReceiptContent(parent, r)).ToArray();
        // Match the persisted-call portion of prompt13's classification; installed tool identity alone is never source proof.
        bool SourceBearing(ContinualToolReceipt r) => r.ToolName == "context.build-pack"
            || catalog.Get(r.ToolName)?.DataAccess is ToolDataAccessClass.Communication or ToolDataAccessClass.UnclassifiedExternal;
        if (current.Any(r => SourceBearing(r) && !r.ObservedStates.Any(o => o.Kind is MissionEvidenceKind.Source or MissionEvidenceKind.PersonalState))) return null;
        var packet = current.Where(r => explicitIds.Contains(r.ToolCallId) || r.ObservedStates.Count > 0 || r.ArtifactHashes?.Count > 0).OrderBy(r => r.ToolCallId).ToArray();
        if (explicitIds.Any(id => packet.All(r => r.ToolCallId != id)) || packet.Length > 64) return null;
        return await ValidReceiptsAsync(parent, packet, ct) ? packet : null;
    }
    private async Task<bool> ValidReceiptsAsync(ContinualObjectiveSnapshot parent, IReadOnlyList<ContinualToolReceipt> receipts, CancellationToken ct)
        => receipts.All(r => r.State == ContinualToolReceiptState.Completed && !r.IsError && MissionSteeringPolicy.CanUseReceiptContent(parent, r)
                && (r.ObservedStates.Count > 0 || r.ArtifactHashes?.Count > 0))
            && (receipts.All(r => r.ObservedStates.Count == 0) || currentState is not null && await currentState.ValidateAsync(parent with { Receipts = receipts }, ct) is null)
            && await MissionArtifactEvidence.ValidateAsync(parent with { Receipts = receipts }, ct) is null;
    public async Task<string?> ExpectedWriteHashAsync(Guid childId, string physicalPath, CancellationToken ct)
    {
        var child = await store.GetAsync(childId, ct) ?? throw new ContinualObjectiveLeaseLostException(childId);
        return ExpectedWriteHash(child, physicalPath);
    }
    internal static string? ExpectedWriteHash(ContinualObjectiveSnapshot child, string physicalPath)
    {
        if (child.Mission?.Delegation is not { } d) return null;
        if (!d.WritableBaseHashes.TryGetValue(physicalPath, out var expected))
            throw new MissionActionSkippedException("Child writes require a captured exact file base; delegate a bounded file target or an isolated workspace adapter.");
        foreach (var receipt in child.Receipts.Where(r => r.State == ContinualToolReceiptState.Completed && !r.IsError && r.WasDispatched).OrderByDescending(r => r.Sequence))
        {
            if (receipt.ToolName == WriteFileTool.ToolId && receipt.RecoveryJson is { } recovery)
            {
                try { if (JsonSerializer.Deserialize<MissionFileWriteProof>(recovery) is { } proof && proof.AbsolutePath == physicalPath)
                    return receipt.ArtifactHashes?.TryGetValue(physicalPath, out var ownHash) == true ? ownHash : proof.Sha256; }
                catch (JsonException) { }
            }
        }
        return expected;
    }
    private async Task<ContinualObjectiveSnapshot> PublicAsync(ContinualObjectiveSnapshot child, CancellationToken ct)
        => await ValidateChildContextCoreAsync(child, new HashSet<Guid>(), ct, requireReady: false) is null
            && (currentState is null || await currentState.ValidateAsync(child, ct) is null)
            && await MissionArtifactEvidence.ValidateAsync(child, ct) is null ? child : Redacted(child);
    private static ContinualObjectiveSnapshot Redacted(ContinualObjectiveSnapshot child) => new()
    {
        Id = child.Id, Surface = child.Surface, PartitionKey = child.PartitionKey, IdempotencyKey = child.Id.ToString("N"),
        ConversationId = child.ConversationId, ExecutorKey = child.ExecutorKey, ObjectiveText = "Child context is no longer current.",
        ObjectiveSummary = "Child context is no longer current.", Authority = child.Authority with { MissionResources = null },
        Status = child.Status, Version = child.Version, CreatedAtUtc = child.CreatedAtUtc, UpdatedAtUtc = child.UpdatedAtUtc,
        Mission = child.Mission is { } m ? new() { OwnerId = m.OwnerId, ParentId = m.ParentId, SuccessCriteria = ["Context unavailable"],
            EvidenceRequirements = [], DelegationLimits = m.DelegationLimits, Delegation = m.Delegation is { } d ? d with {
                ContextFingerprint = null, BoundReceiptIds = [], WritableBaseHashes = new Dictionary<string, string>(), Spec = new() { DelegationId = d.SpecificationHash, Objective = "Context unavailable",
                    SuccessCriteria = [], AllowedToolIds = [], DependencyIds = d.Spec.DependencyIds } } : null } : null
    };
    private async Task<ContinualObjectiveSnapshot> ParentAsync(CurrentTurnContext caller, bool active, CancellationToken ct)
    {
        if (caller.ProfileId is not { } profile || caller.MissionObjectiveId is not { } id) throw new InvalidOperationException("A current host mission is required.");
        if (active)
        {
            if (caller.ValidateMissionExecution is null) throw new InvalidOperationException("A current mission execution fence is required.");
            await caller.ValidateMissionExecution(ct);
        }
        var parent = await store.GetAsync(id, ct);
        if (parent is null || parent.PartitionKey != profile.ToString("D") || parent.Authority.MissionResources is null) throw new InvalidOperationException("Mission profile is unavailable.");
        return parent;
    }
    private async Task<MissionIntegrationConflict?> DetectIntegrationConflictAsync(IReadOnlyList<ContinualObjectiveSnapshot> selected, CancellationToken ct)
    {
        var candidates = selected.Where(c => MissionDelegationPolicy.IsVerified(c)
            && c.Mission?.CodingRun is { Workspace: not null, Verification.ChangedFiles.Count: > 0 }).ToArray();
        for (var i = 0; i < candidates.Length; i++)
            for (var j = i + 1; j < candidates.Length; j++)
            {
                var first = candidates[i]; var second = candidates[j];
                var firstRun = first.Mission!.CodingRun!; var secondRun = second.Mission!.CodingRun!;
                var shared = firstRun.Verification!.ChangedFiles.Intersect(secondRun.Verification!.ChangedFiles, StringComparer.Ordinal)
                    .OrderBy(p => p, StringComparer.Ordinal).ToArray();
                if (shared.Length == 0) continue;
                var conflicting = new List<string>();
                foreach (var relative in shared)
                {
                    var firstPath = Path.Combine(firstRun.Workspace!.WorkspacePath, relative);
                    var secondPath = Path.Combine(secondRun.Workspace!.WorkspacePath, relative);
                    if (!File.Exists(firstPath) || !File.Exists(secondPath)) { conflicting.Add(relative); continue; }
                    var firstHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(firstPath, ct)));
                    var secondHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(secondPath, ct)));
                    if (firstHash != secondHash) conflicting.Add(relative);
                }
                if (conflicting.Count > 0) return new(conflicting, first.Id, second.Id, time.GetUtcNow());
            }
        return null;
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
