using System.Text.Json;
using SwarmCore.Agent.Continual;
using SwarmCore.Agent.Runtime;

namespace SwarmCore.Missions;

public sealed partial class MissionService
{
    private async Task<ContinualObjectiveSnapshot> CreateAttachedAsync(CurrentTurnContext caller, string triggerId,
        string objective, MissionDefinition contract, MissionAuthority resources, ContinualObjectiveSnapshot parent, CancellationToken ct)
    {
        if (contract.CodingRun is null && contract.SecurityRun is null)
            throw new InvalidOperationException("Attached mission creation requires a typed workspace adapter.");
        var allowed = contract.CodingRun?.Request.AllowedToolIds ?? [ShellExecTool.ToolId];
        if (allowed.Count == 0 || caller.AdmittedToolIds is null || allowed.Any(id => !caller.AdmittedToolIds.Contains(id))
            || parent.Mission?.Delegation?.Spec.AllowedToolIds is { } inherited && allowed.Any(id => !inherited.Contains(id)))
            throw new InvalidOperationException("Attached tools must remain inside the current parent tool boundary.");
        if (parent.Authority.MissionResources is not { } parentResources
            || resources.Grants.Any(g => !parentResources.Evaluate(new(g.Resource, g.Effects, parent.PartitionKey)).Allowed))
            throw new InvalidOperationException("Attached resources must remain inside the current parent assignment.");
        resources = resources with { Exclusions = parentResources.Exclusions.Concat(resources.Exclusions).Distinct().ToArray() };
        var key = "typed-child:" + parent.Id.ToString("N") + ":" + MissionVerification.Digest(triggerId);
        var id = new Guid(Convert.FromHexString(MissionVerification.Digest(parent.PartitionKey + ":" + key)[..32]));
        var spec = new MissionDelegationSpec
        {
            DelegationId = triggerId, Role = contract.CodingRun is null ? "security-analysis" : "verified-coding",
            Objective = objective, SuccessCriteria = contract.SuccessCriteria, AllowedToolIds = allowed,
            Resources = resources.Grants, EvidenceRequirements = contract.EvidenceRequirements,
            DeadlineUtc = contract.DeadlineUtc
        };
        var limits = parent.Mission!.DelegationLimits ?? options.Value.DelegationLimits;
        var binding = new MissionDelegation(spec, MissionVerification.Digest(JsonSerializer.Serialize(new { spec,
            contract.CodingRun, contract.SecurityRun, contract.AcceptanceCriteria })), parentResources.Revision,
            parent.Mission.ConstraintVersion, (parent.Mission.Delegation?.Depth ?? 0) + 1)
            { ContextFingerprint = MissionVerification.Digest(JsonSerializer.Serialize(Array.Empty<ContinualToolReceipt>())) };
        contract = contract with { ParentId = parent.Id, Delegation = binding, DelegationLimits = limits,
            Constraints = parent.Mission.Constraints.Concat(contract.Constraints).Distinct().ToArray(),
            ConstraintVersion = parent.Mission.ConstraintVersion,
            MaximumAttempts = parent.Mission.MaximumAttempts is { } cap && (contract.MaximumAttempts is null || contract.MaximumAttempts > cap)
                ? cap : contract.MaximumAttempts };
        var start = new ContinualObjectiveStart
        {
            Id = id, PartitionKey = parent.PartitionKey, Surface = "mission", IdempotencyKey = key,
            ConversationId = "mission-" + MissionVerification.Digest(parent.PartitionKey + ":" + key),
            ExecutorKey = parent.ExecutorKey, ExecutorPayloadJson = parent.ExecutorPayloadJson,
            ObjectiveText = objective, ObjectiveSummary = objective[..Math.Min(300, objective.Length)],
            Authority = parent.Authority with { Kind = ContinualAuthorityKind.DurableDelegation, MissionResources = resources },
            Mission = contract
        };
        start.Mission.Validate();
        CodingRunPolicy.ValidateRegistration(start, caller.ProfileId!.Value);
        SecurityRunPolicy.ValidateRegistration(start, caller.ProfileId.Value);
        if (contract.BudgetReference is { } reference)
            await budgets!.AddMemberAsync(caller.ProfileId!.Value, reference, parent.Id, start.Id, ct);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await caller.ValidateMissionExecution!(ct);
            var result = await store.CreateDelegationAsync(parent.Id, parent.LeaseToken ?? "", parent.Version, start, limits, time.GetUtcNow(), ct);
            if (result.Child is { } child) return child;
            if (result.Status != "conflict") throw new InvalidOperationException(result.Reason ?? "Attached admission rejected.");
            parent = await store.GetAsync(parent.Id, ct) ?? throw new InvalidOperationException("Parent disappeared.");
            if (parent.Authority.MissionResources?.Revision != binding.ParentAuthorityRevision
                || parent.Mission?.ConstraintVersion != binding.ParentConstraintVersion)
                throw new InvalidOperationException("Parent authority changed during attached admission.");
        }
        throw new InvalidOperationException("Attached admission conflicted; retry the same task key.");
    }
}
