using System.Text.Json;
using SwarmCore.Agent.Continual;
using SwarmCore.Agent.Runtime;
using Microsoft.Extensions.Options;
using SwarmCore.Abstractions.Budgets;

namespace SwarmCore.Missions;

/// <summary>Public host entry point. Authority and profile come from the trusted current turn, never tool JSON.</summary>
public sealed partial class MissionService(IMissionStore store, MissionProcessorHost host,
    IContinualObjectiveExecutionControl execution, TimeProvider time, IContinualObjectiveTextSanitizer sanitizer,
    ISandboxPathResolver sandbox, IOptions<MissionDeploymentOptions> deployments, IOptions<MissionRuntimeOptions> options,
    IMissionBudgetLedger? budgets = null)
{
    public async Task<ContinualObjectiveSnapshot> CreateAsync(CurrentTurnContext caller, string triggerId,
        string objective, MissionDefinition contract, CancellationToken ct = default)
    {
        var systemCodingRepair = contract.CodingRun is { } coding
            && caller.CodingRepairGrant?.Allows(coding.Request) == true && caller.MissionResources is not null;
        if ((!caller.CommunicationScopeUserInitiated && caller.MissionObjectiveId is null && !systemCodingRepair) || caller.ProfileId is not { } profile)
            throw new InvalidOperationException("Creating a mission requires an existing user-authorized objective and profile.");
        if (contract.OwnerId != (caller.ActingAgentId ?? profile.ToString("D")))
            throw new InvalidOperationException("Mission owner must match the current host identity.");
        contract.Validate();
        ContinualObjectiveSnapshot? parentBudgetOwner = null;
        if (caller.MissionObjectiveId is { } parentId)
        {
            await (caller.ValidateMissionExecution ?? throw new InvalidOperationException("Attached work requires the current parent fence."))(ct);
            parentBudgetOwner = await store.GetAsync(parentId, ct);
            if (parentBudgetOwner?.PartitionKey != profile.ToString("D") || parentBudgetOwner.Mission is null)
                throw new InvalidOperationException("Attached work requires a current same-profile parent.");
            if (contract.BudgetReference is not null && contract.BudgetReference != parentBudgetOwner.Mission.BudgetReference)
                throw new InvalidOperationException("A child cannot replace its parent's budget.");
            contract = contract with { BudgetReference = parentBudgetOwner.Mission.BudgetReference, BudgetLimits = null,
                DeadlineUtc = parentBudgetOwner.Mission.DeadlineUtc is { } deadline && (contract.DeadlineUtc is null || contract.DeadlineUtc > deadline) ? deadline : contract.DeadlineUtc };
        }
        else if (contract.BudgetLimits is not null)
        {
            if (contract.PlanIsModelAuthored) throw new InvalidOperationException("Budget limits require a trusted host assignment.");
            contract = contract with { BudgetReference = "mission-budget:" + MissionVerification.Digest(profile.ToString("D") + ":" + triggerId),
                BudgetLimits = contract.BudgetLimits with { DeadlineUtc = contract.DeadlineUtc is { } assigned
                    && (contract.BudgetLimits.DeadlineUtc is null || assigned < contract.BudgetLimits.DeadlineUtc) ? assigned : contract.BudgetLimits.DeadlineUtc },
                DeadlineUtc = null };
        }
        if (contract.BudgetReference is not null && budgets is null)
            throw new InvalidOperationException("Configured mission budgets require the durable ledger.");
        // Only trusted host assignment data can select resource authority. ResourceReferences
        // and Constraints in a model-generated delivery plan are never grants.
        var resources = caller.MissionResources ?? MissionResourceAuthority.LocalWorkspace(
            profile, contract.OwnerId, triggerId, sandbox.Root);
        if (caller.MissionObjectiveId is { } parent)
        {
            if (caller.MissionResources is null) throw new InvalidOperationException("Delegation requires inherited mission authority.");
            contract = contract with { ParentId = parent };
            resources = resources.Delegate(parent, contract.OwnerId);
        }
        if (resources.PartitionKey != profile.ToString("D"))
            throw new InvalidOperationException("Mission resource authority belongs to another profile.");
        // Configured deployment destinations are not ordinary source-workspace writes,
        // even when an operator placed one beneath the workspace directory.
        resources = resources with { Exclusions = [.. resources.Exclusions,
            .. deployments.Value.DevelopmentDirectories.Values.Select(path => new MissionResourceGrant(
                new(MissionResourceKind.Directory, SandboxPathResolver.ResolvePhysicalPath(path)),
                MissionEffect.Modify | MissionEffect.Remove | MissionEffect.Execute | MissionEffect.Install,
                "host-development-environment-boundary", true))] };
        resources = resources with { InitialAuthorityHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(resources with { InitialAuthorityHash = null })))) };
        string Safe(string value) => sanitizer.Sanitize(value, 4000) ?? "[redacted]";
        contract = contract with
        {
            OutcomeInspections = contract.PlanIsModelAuthored ? [] : contract.OutcomeInspections,
            SchemaResources = contract.PlanIsModelAuthored ? [] : contract.SchemaResources.Select(resource =>
                resource with { Identity = SandboxPathResolver.ResolvePhysicalPath(resource.Identity) }).ToArray(),
            Constraints = contract.Constraints.Select(Safe).ToArray(),
            SuccessCriteria = contract.SuccessCriteria.Select(Safe).ToArray(),
            ResourceReferences = contract.ResourceReferences.Select(Safe).ToArray(),
            BudgetReference = sanitizer.Sanitize(contract.BudgetReference, 1000),
            AcceptanceCriteria = contract.AcceptanceCriteria.Select(c => c with { Description = Safe(c.Description),
                ExpectedJson = sanitizer.Sanitize(c.ExpectedJson, 4000), Receipt = c.Receipt is { } r
                    ? r with { InputJson = sanitizer.Sanitize(r.InputJson, 4000), ResultContains = sanitizer.Sanitize(r.ResultContains, 4000) } : null }).ToArray(),
            EvidenceRequirements = contract.EvidenceRequirements.Select(e => e with
            {
                InputJson = sanitizer.Sanitize(e.InputJson, 4000), ResultContains = sanitizer.Sanitize(e.ResultContains, 4000),
            }).ToArray(),
        };
        contract.Validate();
        if (parentBudgetOwner is not null)
            return await CreateAttachedAsync(caller, triggerId, objective, contract, resources, parentBudgetOwner, ct);
        var conversationId = "mission-" + MissionVerification.Digest(profile.ToString("D") + ":" + triggerId);
        var existing = await store.GetLatestForConversationAsync(profile.ToString("D"), conversationId, ct);
        var missionId = existing?.Id ?? new Guid(Convert.FromHexString(MissionVerification.Digest("root:" + conversationId)[..32]));
        // Publish a runnable row only after its durable budget authority exists. Stable identity
        // makes concurrent deliveries and interrupted pre-admissions converge on one ledger.
        if (contract.BudgetReference is { } budgetReference)
        {
            if (contract.BudgetLimits is { } limits)
                await budgets!.CreateAsync(profile, budgetReference, missionId, limits, ct);
            else if ((await budgets!.ReadAsync(profile, budgetReference, ct))?.OwnerMissionId != missionId)
                throw new InvalidOperationException("This mission does not own the supplied budget reference.");
        }
        var saved = await host.Processor.StartOrJoinAsync(new ContinualObjectiveStart
        {
            Id = missionId,
            PartitionKey = profile.ToString("D"), Surface = "mission", IdempotencyKey = triggerId,
            ConversationId = "mission-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(profile.ToString("D") + ":" + triggerId))), ExecutorKey = host.ExecutorKey,
            ObjectiveText = objective, ObjectiveSummary = objective.Length <= 300 ? objective : objective[..300],
            Authority = (systemCodingRepair
                ? new ContinualObjectiveAuthority(ContinualAuthorityKind.System, caller.CodingRepairGrant!.AuthorityId,
                    UserInitiated: false, Unattended: true)
                : ContinualObjectiveAuthority.UserRequested(caller.CommunicationScopeAuthority,
                caller.CommunicationScopeKind.ToString(), caller.AllowedSourceAccountIds?.Select(x => x.ToString("D")).ToArray(), unattended: true)
                ) with { MissionResources = resources },
            Mission = contract, ExecutorPayloadJson = JsonSerializer.Serialize(new MissionTurnPayload(profile, caller.ActingAgentId)),
        }, ct);
        return saved;
    }

    public async Task<MissionBudgetSnapshot?> BudgetStatusAsync(Guid profileId, Guid id, CancellationToken ct = default)
    {
        var mission = await InspectAsync(profileId, id, ct);
        return mission?.Mission?.BudgetReference is { } reference && budgets is not null
            ? await budgets.ReadAsync(profileId, reference, ct) : null;
    }

    /// <summary>
    /// Host-owned budget-limit entry point for a directly authenticated user. When the mission already
    /// owns a budget account, <paramref name="expectedVersion"/> is the observed ledger version and this
    /// behaves as before. When a root mission has no account yet (every mission the model creates through
    /// <c>missions.create</c> starts this way, because a model-authored plan can never carry
    /// <see cref="MissionDefinition.BudgetLimits"/>), <paramref name="expectedVersion"/> is instead the
    /// observed mission version: the user's first save both assigns the account reference and creates it
    /// with the supplied limits, so the previously dead "Change limits" UI becomes the mission's actual
    /// production entry point for configuring a budget.
    /// </summary>
    public async Task<MissionBudgetSnapshot> UpdateBudgetLimitsAsync(CurrentTurnContext caller, Guid id,
        long expectedVersion, MissionBudgetLimits limits, CancellationToken ct = default)
    {
        if (!caller.CommunicationScopeUserInitiated || caller.MissionObjectiveId is not null || caller.ProfileId is not { } profile)
            throw new InvalidOperationException("Changing mission limits requires a direct authenticated user assignment.");
        limits.Validate();
        var mission = await InspectAsync(profile, id, ct) ?? throw new InvalidOperationException("Mission not found.");
        if (mission.Mission is null) throw new InvalidOperationException("This objective is not a mission.");
        var ledger = budgets ?? throw new InvalidOperationException("Mission budget ledger unavailable.");
        if (mission.Mission.BudgetReference is not { } reference)
        {
            if (mission.Mission.ParentId is not null) throw new InvalidOperationException("Only a root mission's own budget account can be assigned directly.");
            var generated = "mission-budget:" + MissionVerification.Digest(profile.ToString("D") + ":" + id.ToString("N"));
            if (!await store.AssignBudgetReferenceAsync(id, profile.ToString("D"), expectedVersion, generated, ct))
                throw new InvalidOperationException("The mission changed before its budget account could be assigned. Reload and try again.");
            return await ledger.CreateAsync(profile, generated, id, limits, ct);
        }
        return await ledger.UpdateLimitsAsync(profile, reference, id, expectedVersion, limits, ct);
    }

    public Task<IReadOnlyList<ContinualObjectiveSnapshot>> ListAsync(Guid profileId, int take = 50, CancellationToken ct = default)
        => store.ListAsync(profileId.ToString("D"), take, ct);

    public async Task<bool> UpdateDeadlineAsync(Guid profileId, Guid id, DateTimeOffset? deadlineUtc, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var mission = await InspectAsync(profileId, id, ct);
            if (mission?.Mission is null || ContinualObjectiveStateMachine.IsTerminal(mission.Status)) return false;
            if (mission.Mission.BudgetReference is { } reference && mission.Mission.ParentId is null)
            {
                var ledger = budgets ?? throw new InvalidOperationException("Mission budget ledger unavailable.");
                var current = await ledger.ReadAsync(profileId, reference, ct) ?? throw new InvalidOperationException("Mission budget was not found.");
                if (current.OwnerMissionId != id) return false;
                try { await ledger.UpdateLimitsAsync(profileId, reference, id, current.Version, current.Limits with { DeadlineUtc = deadlineUtc }, ct); return true; }
                catch (InvalidOperationException) when (attempt < 7) { continue; }
            }
            if (await store.UpdateDeadlineAsync(id, profileId.ToString("D"), mission.Version, deadlineUtc, ct)) return true;
        }
        return false;
    }

    public async Task<bool> BindOutcomeInspectionAsync(Guid profileId, Guid id, MissionOutcomeInspection inspection, CancellationToken ct = default)
    {
        // This is a trusted host/user entry point, never a model tool or inferred success.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var mission = await InspectAsync(profileId, id, ct);
            if (mission?.Mission is null || ContinualObjectiveStateMachine.IsTerminal(mission.Status)) return false;
            if (await store.BindOutcomeInspectionAsync(id, profileId.ToString("D"), mission.Version, inspection, ct)) return true;
        }
        return false;
    }

    public Task<bool> ResolveTargetAsync(Guid profileId, Guid id, int authorityRevision, string target, CancellationToken ct = default)
        => store.ResolveTargetClarificationAsync(id, profileId.ToString("D"), authorityRevision, target, ct);

    public async Task<ContinualObjectiveSnapshot?> InspectAsync(Guid profileId, Guid id, CancellationToken ct = default)
    {
        var mission = await store.GetAsync(id, ct);
        return mission?.PartitionKey == profileId.ToString("D") ? mission : null;
    }

    public async Task<IReadOnlyList<MissionEvent>> EventsAsync(Guid profileId, Guid id, long afterVersion = -1, CancellationToken ct = default)
        => await InspectAsync(profileId, id, ct) is null ? [] : await store.EventsAsync(id, afterVersion, ct: ct);

    public async Task<bool> ControlAsync(Guid profileId, Guid id, MissionControl control, string reason, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var mission = await InspectAsync(profileId, id, ct);
            if (mission is null || !MissionTransitions.CanControl(mission.Status, control)) return false;
            if (await store.ControlAsync(id, mission.Version, control, reason, time.GetUtcNow(), ct))
            {
                if (control != MissionControl.Resume) execution.Cancel(id);
                if (control == MissionControl.Cancel && budgets is not null)
                    await Task.Run(async () =>
                    {
                        var pending = new Stack<Guid>(); pending.Push(id);
                        var seen = new HashSet<Guid>();
                        while (pending.TryPop(out var next) && seen.Add(next))
                        {
                            var current = await store.GetAsync(next, CancellationToken.None);
                            if (current?.PartitionKey != profileId.ToString("D")) continue;
                            if (current.Status == ContinualObjectiveStatus.Cancelled && current.Mission is
                                { BudgetReference: { } reference, Delegation.Spec.BudgetToolCalls: not null })
                                await budgets.ReleaseChildAsync(profileId, reference, next, CancellationToken.None);
                            foreach (var child in await store.ListChildrenAsync(next, CancellationToken.None)) pending.Push(child.Id);
                        }
                    }, CancellationToken.None);
                return true;
            }
        }
        return false;
    }
}

internal sealed record MissionTurnPayload(Guid ProfileId, string? ActingAgentId);
