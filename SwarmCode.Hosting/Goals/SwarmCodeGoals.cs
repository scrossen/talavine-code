using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.Continual;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Coding;
using SwarmCore.Coding.Goals;
using SwarmCore.Coding.Permissions;
using SwarmCore.Coding.Routing;
using SwarmCore.Agent.Communication;

namespace SwarmCode.Hosting.Goals;

public static class SwarmCodeGoals
{
    public static IServiceCollection AddSwarmCodeGoals(this IServiceCollection services)
    {
        services.TryAddSingleton<FileCodingGoalStore>();
        services.TryAddSingleton<ICodingGoalStore>(provider => provider.GetRequiredService<FileCodingGoalStore>());
        services.TryAddSingleton<FileGoalMailbox>();
        services.TryAddSingleton<IGoalMailbox>(provider => provider.GetRequiredService<FileGoalMailbox>());
        services.TryAddSingleton<FileGoalResults>();
        services.TryAddSingleton<IGoalResultLedger>(provider => provider.GetRequiredService<FileGoalResults>());
        services.TryAddSingleton<FileGoalReports>();
        services.TryAddSingleton<IGoalReportLedger>(provider => provider.GetRequiredService<FileGoalReports>());
        services.AddHostedService<GoalReplyToolRegistration>();
        services.TryAddSingleton<CodingGoalRuntimeState>();
        services.TryAddSingleton<ICodingGoalRequestPolicy, SwarmCodeGoalRequestPolicy>();
        // Idle-time compaction of old receipt bodies: opt-in through preferences, scheduled by the dreaming loop.
        services.TryAddSingleton(provider => GoalReceiptCompactionOptions.Read(provider.GetService<IConfiguration>(),
            provider.GetService<ILoggerFactory>()?.CreateLogger<GoalReceiptCompactionOptions>()));
        services.TryAddSingleton<GoalReceiptCompaction>();
        services.AddSingleton<IIdleMaintenanceStep>(provider => provider.GetRequiredService<GoalReceiptCompaction>());
        // Host-owned outcome proof for this host's own process calls: a failed shell.exec whose completion
        // envelope the bridge produced is settled from that envelope during read-only reconciliation, so a
        // non-zero exit no longer parks a goal in Uncertain. Foreign servers, timeouts and sends stay held.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICodingGoalOutcomeInspector, OwnHostProcessCompletionInspector>(
            provider => new(provider.GetService<ICapabilityCatalog>())));
        // This evaluator belongs only to explicitly started /goal work; ordinary turns retain their own completion behavior.
        services.TryAddSingleton<CodingGoalOrchestrator>(provider => new(
            provider.GetRequiredService<ICodingTurnOrchestrator>(), provider.GetRequiredService<ICodingGoalStore>(),
            new SwarmCodeGoalAcceptance(provider.GetRequiredService<IServiceScopeFactory>()),
            provider.GetRequiredService<CodingGoalRuntimeState>(), policy: provider.GetRequiredService<ICodingGoalRequestPolicy>(),
            tools: provider.GetService<ICapabilityCatalog>(), receiptVerifier: provider.GetService<ICodingGoalReceiptVerifier>(),
            mailbox: provider.GetRequiredService<IGoalMailbox>(),
            outcomeInspectors: provider.GetServices<ICodingGoalOutcomeInspector>()));
        return services;
    }
}

internal sealed class SwarmCodeGoalAcceptance(IServiceScopeFactory scopes)
    : IContinualObjectiveAcceptanceService
{
    public async Task<ContinualAcceptanceVerdict> EvaluateAsync(ContinualAcceptanceRequest request, CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        if (request.Objective is { } objective)
        {
            var team = scope.ServiceProvider.GetService<Agents.SwarmCodeAgentHost>()?.Team;
            if (team is not null && team.Children(objective.ConversationId).Any(child => !child.Independent
                && (team.IsActive(child.Id) || !child.ResultDelivered && child.State != SwarmCore.Coding.Agents.AgentState.Cancelled)))
                return new ContinualAcceptanceVerdict(ContinualAcceptanceOutcome.Retry,
                    "Task agent results still need review. Wait for running agents, collect their results with agents.wait, and verify their work before completing the goal.");
            if (objective.PartitionKey == CodingGoalOrchestrator.Partition && objective.Mission is null)
            {
                var mailbox = scope.ServiceProvider.GetRequiredService<FileGoalMailbox>();
                if (await mailbox.HasPendingAsync(objective.Id, ct).ConfigureAwait(false))
                    return new ContinualAcceptanceVerdict(ContinualAcceptanceOutcome.Retry,
                        "Read and reply to the goal's saved user messages at the next natural stopping point before completing the goal. Continue the existing work without replaying actions.");
                var snapshot = await mailbox.GetInstructionSnapshotAsync(objective.Id, ct).ConfigureAwait(false);
                if (snapshot.Sources.Count <= 1)
                    return await new ContinualObjectiveAcceptanceService(scope.ServiceProvider.GetRequiredService<ILlmRouter>())
                        .EvaluateAsync(request, ct).ConfigureAwait(false);
                var verdict = await new GoalInstructionAcceptance(scope.ServiceProvider.GetRequiredService<ILlmRouter>())
                    .EvaluateAsync(request, snapshot, ct).ConfigureAwait(false);
                if (verdict.Outcome != ContinualAcceptanceOutcome.Delivered) return verdict;
                if (!await mailbox.TryRecordAcceptanceAsync(snapshot, objective.LeaseToken ?? "",
                    FileGoalMailbox.AcceptanceEvidenceDigest(objective), ContinualDeliveryEvidence.OutputDigest(request.FinalOutput), ct).ConfigureAwait(false))
                    return new(ContinualAcceptanceOutcome.VerificationUnavailable,
                        "Goal instructions or completion evidence changed during verification. Retain the existing result and audit the current input without replaying actions.") { EvaluationUnavailable = true };
                return verdict;
            }
        }
        return await new ContinualObjectiveAcceptanceService(scope.ServiceProvider.GetRequiredService<ILlmRouter>())
            .EvaluateAsync(request, ct).ConfigureAwait(false);
    }
}

internal sealed class SwarmCodeGoalRequestPolicy(IServiceProvider services, IConfiguration configuration,
    ICodingWorkspaceResolver workspaces, AppPreferencesStore preferences) : ICodingGoalRequestPolicy
{
    public async Task<CodingTurnRequest> PrepareAsync(CodingTurnRequest request, CancellationToken ct = default)
    {
        var rules = new CodingPermissionRules();
        lock (configuration)
        {
            if (configuration is IConfigurationRoot root) root.Reload();
            configuration.GetSection(CodingOptions.SectionName).GetSection("Permissions").Bind(rules);
        }
        var workspace = await workspaces.ResolveAsync(request.ConversationId, ct).ConfigureAwait(false);
        var mode = CodingPermissionPolicy.Stricter(request.Permissions?.Mode ?? CodingPermissionMode.Auto, preferences.Current.Mode);
        var policy = await SwarmCodePermissions.PolicyAsync(mode, services, rules,
            workspace?.RepositoryRoot ?? request.Workspace?.RepositoryRoot, ct).ConfigureAwait(false);
        return request with { Workspace = workspace, Permissions = policy };
    }
}
