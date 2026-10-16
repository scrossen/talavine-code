using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Continual;
using SwarmCore.Agent.Runtime;
using SwarmCore.Abstractions.Packaging;

namespace SwarmCore.Missions;

/// <summary>Assistant capability catalog is also the host's public tool/API discovery surface.</summary>
public sealed class MissionToolRegistrationService(ICapabilityCatalog catalog, IServiceProvider services,
    OptionalHostCapabilityPolicy? hostCapabilities = null) : IHostedService
{
    private readonly OptionalHostCapabilityPolicy _hostCapabilities = hostCapabilities
        ?? services.GetService<OptionalHostCapabilityPolicy>() ?? OptionalHostCapabilityPolicy.Unrestricted;
    public Task StartAsync(CancellationToken cancellationToken)
    {
        catalog.Register(ToolDescriptor.Create<MissionDelegationSpec, MissionDelegateToolResult>("missions.delegate", "missions.tools",
            "Delegate genuinely independent specialist work within the current mission. Supply stable delegationId, objective, successCriteria, evidenceRequirements, installed allowedToolIds, narrowed resources and optional dependencies/parent receipt references. Simple recall or task creation stays on one agent. No employees, credentials or new authority are created. Results need host verification; identical source repetition is not independent corroboration.",
            ToolCostClass.Cheap, ToolSideEffect.WritesLocal, async (input, ct) => {
                using var scope = services.CreateScope();
                return MissionDelegationToolProjection.Delegate(await scope.ServiceProvider.GetRequiredService<MissionDelegationService>().DelegateAsync(Caller(scope.ServiceProvider), input, ct));
            }, enforcesMissionResourceBoundary: true) with { SupportsIdempotentMissionReplay = true });
        catalog.Register(ToolDescriptor.Create<MissionChildrenInput, MissionChildrenToolResult>("missions.children", "missions.tools",
            "Inspect current mission children, dependencies, verified receipts, limits and blockers. Completed child text alone is not parent acceptance.",
            ToolCostClass.Cheap, ToolSideEffect.ReadOnly, async (input, ct) => {
                using var scope = services.CreateScope();
                return MissionDelegationToolProjection.Children(await scope.ServiceProvider.GetRequiredService<MissionDelegationService>().StatusAsync(Caller(scope.ServiceProvider), ct));
            }));
        catalog.Register(ToolDescriptor.Create<MissionJoinInput, MissionJoinToolResult>("missions.join", "missions.tools",
            "Inspect and join selected current mission children after verifying their source and artifact evidence. Empty childIds selects all children. Do independent work before joining. An unresolved join saves the wait and yields this turn after durable tool completion so children can run. This does not mark the parent complete.",
            ToolCostClass.Cheap, ToolSideEffect.WritesLocal, async (input, ct) => {
                using var scope = services.CreateScope();
                return MissionDelegationToolProjection.Join(await scope.ServiceProvider.GetRequiredService<MissionDelegationService>().JoinAsync(Caller(scope.ServiceProvider), input.ChildIds, ct));
            }, enforcesMissionResourceBoundary: true) with { SupportsIdempotentMissionReplay = true });
        catalog.Register(ToolDescriptor.Create<MissionChildMessageInput, bool>("missions.child_message", "missions.tools",
            "Send a bounded clarification to an owned child. The note cannot replace its authority or immutable assignment. Authority changes use the parent's existing steering controls.",
            ToolCostClass.Cheap, ToolSideEffect.WritesLocal, async (input, ct) => {
                using var scope = services.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<MissionDelegationService>().MessageAsync(Caller(scope.ServiceProvider), input.ChildId, input.Message, ct);
            }, enforcesMissionResourceBoundary: true));
        catalog.Register(ToolDescriptor.Create<MissionChildCancelInput, bool>("missions.child_cancel", "missions.tools",
            "Cancel obsolete owned child work while retaining its receipts and valid sibling results.",
            ToolCostClass.Cheap, ToolSideEffect.WritesLocal, async (input, ct) => {
                using var scope = services.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<MissionDelegationService>().CancelAsync(Caller(scope.ServiceProvider), input.ChildId, ct);
            }, enforcesMissionResourceBoundary: true));
        catalog.Register(ToolDescriptor.Create<MissionConstraintInput, string>(
            "missions.report_constraint_conflict", "missions.tools", "Report a proven conflict between the requested outcome and an explicit exclusion, after exhausting authorized alternatives and completing independent work. Supply actual receipt ids and exclusion provenance. The host independently verifies the conflict; this never requests an override.",
            ToolCostClass.LlmCall, ToolSideEffect.WritesLocal, async (input, ct) =>
            {
                using var scope = services.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<MissionConstraintService>().ReportAsync(Caller(scope.ServiceProvider), input, ct);
            }, enforcesMissionResourceBoundary: true));
        catalog.Register(ToolDescriptor.Create<MissionDependencyInput, string>(
            "missions.report_external_dependency", "missions.tools", "Report an indispensable external credential or mandatory OS/MFA prerequisite only after diagnosis, authorized alternatives and all independent work. Supply actual receipt ids; the host independently verifies the need. Temporary outages and routine failures remain recovery work, never permission-to-continue requests.",
            ToolCostClass.LlmCall, ToolSideEffect.WritesLocal, async (input, ct) =>
            {
                using var scope = services.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<MissionDependencyService>().ReportAsync(Caller(scope.ServiceProvider), input, ct);
            }, enforcesMissionResourceBoundary: true));
        catalog.Register(ToolDescriptor.Create<MissionDeployInput, MissionDeployResult>(
            "missions.deploy_artifact", "missions.tools", "Deploy a verified artifact to an explicitly requested configured development environment. Coding alone does not authorize deployment. If the requested target is ambiguous, finish preparation first; the host persists one bound target question.",
            ToolCostClass.External, ToolSideEffect.ExternalSend, async (input, ct) =>
            {
                using var scope = services.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<MissionDeploymentService>().DeployAsync(Caller(scope.ServiceProvider), input, ct);
            }, enforcesMissionResourceBoundary: true));
        catalog.Register(ToolDescriptor.Create<CreateMissionInput, ContinualObjectiveSnapshot>(
            toolId: "missions.create", ownerAgentId: "missions.tools",
            enforcesMissionResourceBoundary: true,
            description: "Create durable multi-step work under the current user objective. Requires success criteria and named verification tools. Returns mission identity, status and committed progress; execution continues through the host scheduler after restart. Authority is inherited from this turn. Never call for work the user did not request.",
            cost: ToolCostClass.Cheap, sideEffect: ToolSideEffect.WritesLocal,
            keywords: ["mission", "durable", "long-running", "background", "objective"],
            invoke: async (input, ct) =>
            {
                using var scope = services.CreateScope();
                var caller = Caller(scope.ServiceProvider);
                if (caller.MissionObjectiveId is not null) throw new InvalidOperationException("Use missions.delegate for bounded child work.");
                var approval = ToolApprovalContextAccessor.Current;
                if (approval?.Approved != true) throw new InvalidOperationException("Mission creation requires the active tool authority.");
                // The proposed objective is task data. The immutable durable objective must
                // retain the authenticated request, never a model-authored replacement.
                var originalIntent = approval.UserMessage;
                if (string.IsNullOrWhiteSpace(originalIntent))
                    throw new InvalidOperationException("Mission creation requires the authenticated original request.");
                return await scope.ServiceProvider.GetRequiredService<MissionService>().CreateAsync(caller,
                    $"{caller.TurnId}:{approval.RequestId}", originalIntent,
                    new MissionDefinition
                    {
                        OwnerId = caller.ActingAgentId ?? caller.ProfileId!.Value.ToString("D"),
                        SuccessCriteria = input.SuccessCriteria,
                        // Only a host-defined verifier can declare that external state is irrelevant.
                        EvidenceRequirements = input.EvidenceRequirements.Select(e => e with { BindToFileArtifacts = false }).ToArray(),
                        Constraints = input.Constraints ?? [], ResourceReferences = input.ResourceReferences ?? [],
                        // Model-suggested deadlines are not user resource limits, and a
                        // model cannot select an unrelated mission as its authority parent.
                        DeadlineUtc = null, ParentId = caller.MissionObjectiveId, PlanIsModelAuthored = true,
                    }, ct);
            }));
        catalog.Register(ToolDescriptor.Create<ListMissionsInput, IReadOnlyList<ContinualObjectiveSnapshot>>(
            toolId: "missions.list", ownerAgentId: "missions.tools",
            description: "List current-profile missions with status, current work, blocking reason, saved results and evidence. Idle/retry/waiting/paused/uncertain are not completed.",
            cost: ToolCostClass.Cheap, sideEffect: ToolSideEffect.ReadOnly,
            keywords: ["mission", "progress", "status", "results"],
            invoke: async (input, ct) =>
            {
                using var scope = services.CreateScope();
                var snapshots = await scope.ServiceProvider.GetRequiredService<MissionService>().ListAsync(Caller(scope.ServiceProvider).ProfileId!.Value, input.Take, ct);
                return snapshots.Select(snapshot => MissionToolSnapshotProjection.ForTool(snapshot)!).ToArray();
            }));
        catalog.Register(ToolDescriptor.Create<InspectMissionInput, MissionInspection>(
            toolId: "missions.inspect", ownerAgentId: "missions.tools",
            description: "Retrieve a mission's committed checkpoint, pending work, results, acceptance receipts and versioned event history. Host-only coding file inventories are omitted. afterVersion paginates events. A pause with a lease/current tool is still draining an action.",
            cost: ToolCostClass.Cheap, sideEffect: ToolSideEffect.ReadOnly,
            keywords: ["mission", "evidence", "result", "checkpoint", "history"],
            invoke: async (input, ct) =>
            {
                using var scope = services.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<MissionService>();
                var profile = Caller(scope.ServiceProvider).ProfileId!.Value;
                return new(MissionToolSnapshotProjection.ForTool(await service.InspectAsync(profile, input.Id, ct)), await service.EventsAsync(profile, input.Id, input.AfterVersion, ct));
            }));
        catalog.Register(ToolDescriptor.Create<MissionCheckpoint, bool>(
            toolId: "missions.checkpoint", ownerAgentId: "missions.tools",
            enforcesMissionResourceBoundary: true,
            description: "Save current work, pending steps and an optional result reference for the mission executing this turn. No mission id or authority can be supplied. Call after completing a meaningful step; tool receipts are saved automatically.",
            cost: ToolCostClass.Cheap, sideEffect: ToolSideEffect.WritesLocal,
            keywords: ["mission", "checkpoint", "progress", "pending"],
            invoke: (input, ct) => services.GetRequiredService<MissionTurnObserver>().CheckpointAsync(Caller(services), input, ct)));
        foreach (var action in Enum.GetValues<MissionControl>())
        {
            var control = action;
            catalog.Register(ToolDescriptor.Create<ControlMissionInput, MissionControlResult>(
                toolId: "missions." + action.ToString().ToLowerInvariant(), ownerAgentId: "missions.tools",
                description: $"{action} an explicitly user-selected mission in the current profile. Preserve receipts. Pause/cancel signal running tools; a non-interruptible tool may still be finishing. Resume is rejected while its prior execution lease remains active. Uncertain outcomes require inspection before retry.",
                cost: ToolCostClass.Cheap, sideEffect: ToolSideEffect.WritesLocal,
                keywords: ["mission", action.ToString().ToLowerInvariant()],
                invoke: async (input, ct) =>
                {
                    using var scope = services.CreateScope();
                    var caller = Caller(scope.ServiceProvider);
                    if (!caller.CommunicationScopeUserInitiated || ToolApprovalContextAccessor.Current?.Approved != true)
                        throw new InvalidOperationException("Mission controls require current user authority.");
                    var service = scope.ServiceProvider.GetRequiredService<MissionService>();
                    var applied = await service.ControlAsync(caller.ProfileId!.Value, input.Id, control, input.Reason ?? $"{control} requested by user.", ct);
                    return new(applied, MissionToolSnapshotProjection.ForTool(await service.InspectAsync(caller.ProfileId.Value, input.Id, ct)));
                }));
        }
        catalog.Register(ToolDescriptor.Create<MissionMcpProcessInput, MissionMcpProcessResult>(
            toolId: "missions.mcp", ownerAgentId: "missions.tools", enforcesMissionResourceBoundary: true,
            description: "Discover or call any local stdio MCP server within the mission's kernel sandbox. Provide command, arguments, optional workingDirectory. Omit tool to list tools; provide tool and toolArguments to invoke. Each call owns a fresh contained server; no permission prompt is needed.",
            cost: ToolCostClass.External, sideEffect: ToolSideEffect.Exec, keywords: ["mcp", "connector", "stdio", "server"],
            invoke: async (input, ct) => { using var scope = services.CreateScope(); return await scope.ServiceProvider.GetRequiredService<MissionMcpProcessService>().ExecuteAsync(input, ct); }));
        if (_hostCapabilities.Allows(OptionalHostCapability.ManagedBrowser))
            catalog.Register(ToolDescriptor.Create<MissionBrowserInput, MissionBrowserResult>(
            toolId: "missions.browser", ownerAgentId: "missions.tools", enforcesMissionResourceBoundary: true,
            description: "Execute navigate, snapshot, screenshot, click, or fill in this mission's persistent browser. Supply url for navigate, selector for click/fill, value for fill. For click/fill, copy pageToken from the latest result; it binds the action to the observed page state. The assignment authorizes scoped work; no repeat permission request is needed. Inspect source-backed results to verify completion.",
            cost: ToolCostClass.External, sideEffect: ToolSideEffect.ExternalSend,
            resolveSideEffect: input => input.Action is "navigate" or "snapshot" or "screenshot" ? ToolSideEffect.ReadOnly : null, keywords: ["browser", "navigate", "click", "fill", "website"],
            invoke: (input, ct) => services.GetRequiredService<MissionBrowserService>().ExecuteAsync(input, ct)));
        else
            catalog.Unregister("missions.browser");
        return Task.CompletedTask;
    }
    private static CurrentTurnContext Caller(IServiceProvider sp)
    {
        var caller = sp.GetRequiredService<ICurrentTurnContextAccessor>().Current;
        return caller?.ProfileId is not null ? caller : throw new InvalidOperationException("An active profile-scoped turn is required.");
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
/// <summary>
/// What a model may propose for a mission. The durable objective is always the person's authenticated request, the
/// deadline is the person's to set and the parent is the current turn's mission, so none of them is an input here.
/// </summary>
public sealed record CreateMissionInput(
    [property: System.ComponentModel.Description("A short summary of the work, for your own reference only: the mission's objective is always the person's own request, never this text.")]
    string Objective,
    IReadOnlyList<string> SuccessCriteria,
    IReadOnlyList<MissionEvidenceRequirement> EvidenceRequirements, IReadOnlyList<string>? Constraints = null,
    IReadOnlyList<string>? ResourceReferences = null);
public sealed record ListMissionsInput(int Take = 50);
public sealed record InspectMissionInput(Guid Id, long AfterVersion = -1);
public sealed record ControlMissionInput(Guid Id, string? Reason = null);
public sealed record MissionInspection(ContinualObjectiveSnapshot? Mission, IReadOnlyList<MissionEvent> Events);
public sealed record MissionControlResult(bool Applied, ContinualObjectiveSnapshot? Mission);

public sealed record MissionChildrenInput;
public sealed record MissionJoinInput(IReadOnlyList<Guid> ChildIds);
public sealed record MissionChildMessageInput(Guid ChildId, string Message);
public sealed record MissionChildCancelInput(Guid ChildId);
