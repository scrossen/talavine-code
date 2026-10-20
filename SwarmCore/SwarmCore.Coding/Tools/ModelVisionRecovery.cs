using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Procedures;
using SwarmCore.Agent.Runtime;
using SwarmCore.LlmRouting.LocalRuntime;
using SwarmCore.LlmRouting.Services;

namespace SwarmCore.Coding.Tools;

/// <summary>Host-authorized recovery shared by automatic image admission and the ordinary repair tool.</summary>
public sealed class ModelVisionRecovery(IServiceProvider services)
{
    public const string ToolId = "models.repair_vision";
    public async Task<VisionRepairResult> RepairAsync(string providerName, string model, string profile, CancellationToken ct)
    {
        var provider = services.GetRequiredService<ILlmProviderRegistry>().GetProvider(providerName)
            ?? throw new InvalidOperationException("Choose a registered provider.");
        var evidence = await LlmModelCapabilityEvidenceResolver.ResolveAsync(provider, model, ct)
            ?? throw new InvalidOperationException("Fresh artifact metadata is unavailable.");
        if (evidence.SupportsVision == true) return new(false, model, "This artifact already declares vision. No component was changed.", evidence.ArtifactDigest, evidence.ArtifactDigest);
        if (evidence.Transport != "ollama-native-v1" || !new Uri(evidence.Endpoint).IsLoopback)
            return new(false, model, "Automatic component repair is available for verified on-device Ollama imports.");
        var repair = services.GetService<OllamaVisionRepair>() ?? throw new InvalidOperationException("This host has no local model installation adapter.");
        var store = services.GetRequiredService<IAgentProcedureStore>();
        var inputs = new JsonObject { ["provider"] = providerName, ["model"] = model, ["endpoint"] = evidence.Endpoint, ["artifact"] = evidence.ArtifactDigest };
        var binding = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("vision-repair-v1\n" + inputs.ToJsonString())));
        var descriptor = ToolDescriptor.Create<JsonObject, VisionRepairResult>(ToolId, "coding", "Install a verified matching image component.",
            ToolCostClass.External, ToolSideEffect.Exec, (_, _) => throw new NotSupportedException()) with { ImplementationFingerprint = "matching-projector-v1" };
        var context = new AgentProcedureContext
        {
            Key = new(profile, "model-capability-repair", providerName + "/" + model), Store = store,
            Inputs = inputs, BindingFingerprint = binding,
            AllowTool = (tool, args) => tool.ToolId == ToolId && JsonNode.DeepEquals(args, inputs),
            CheckPreconditionAsync = async (_, _, token) => (await LlmModelCapabilityEvidenceResolver.ResolveAsync(provider, model, token))?.ArtifactDigest == evidence.ArtifactDigest,
            VerifyAsync = (run, _) => Task.FromResult<AgentProcedureVerification?>(run.Receipts is [{ HostCompletionVerified: true, IsError: false }]
                ? new("matching-projector-two-images-v1", binding, DateTimeOffset.UtcNow) : null),
            FormatResult = _ => "Matching vision component installed and verified."
        };
        var history = await store.GetAsync(context.Key, ct);
        if (history?.ActiveRevisionId is null && history?.Revisions.LastOrDefault() is
            { FailureCode: "outcome_uncertain" or "effect_checkpoint_required", BindingFingerprint: var previousBinding }
            && previousBinding == binding)
        {
            // Wait for any surviving local operation, then inspect the exact unchanged original.
            // This clears only a local setup uncertainty; no user's browser/file action is replayed.
            using (await services.GetRequiredService<ILocalLlmRuntimeManager>().AcquireAsync(new(providerName, model, WorkloadKind: LocalLlmWorkloadKind.Vision), ct))
                if ((await LlmModelCapabilityEvidenceResolver.ResolveAsync(provider, model, ct))?.ArtifactDigest == evidence.ArtifactDigest)
                    await AgentProcedureRuntime.ResolveUncertaintyAsync(store, context.Key, history.Version,
                        new("original-model-unchanged-v1", evidence.ArtifactDigest, DateTimeOffset.UtcNow), ct);
        }
        var routine = await AgentProcedureRuntime.StartAsync(context, [descriptor], ct);
        if (routine.StopReason is { } stop) return new(false, model, stop);
        if (routine.IsReplaying && await routine.NextAsync(ct) is null) return new(false, model, "The model changed; fresh discovery is required.");
        var invocation = Guid.NewGuid().ToString("N");
        await routine.BeforeDispatchAsync(invocation, ct);
        try
        {
            var result = await repair.RepairAsync(evidence.Endpoint, model, ct);
            await routine.RecordAsync(new(ToolId, inputs, JsonSerializer.SerializeToNode(result), !result.Repaired, true, true,
                HostCompletionVerified: result.Repaired, OutcomeUncertain: false, InvocationId: invocation), CancellationToken.None);
            if (result.Repaired)
            {
                // This subtask has an independent physical contract; acceptance does not certify the user's larger task.
                if (await routine.TryCompleteAsync(CancellationToken.None) is not null) await context.AcceptAsync(CancellationToken.None);
                await LlmModelCapabilityEvidenceResolver.ResolveAsync(provider, model, ct); // refresh the living provider
            }
            return result;
        }
        catch
        {
            var unchanged = false;
            try { unchanged = (await LlmModelCapabilityEvidenceResolver.ResolveAsync(provider, model, CancellationToken.None))?.ArtifactDigest == evidence.ArtifactDigest; }
            catch { /* Unknown local state retains the procedure's inspection barrier. */ }
            await routine.RecordAsync(new(ToolId, inputs, null, true, true, true, OutcomeUncertain: !unchanged, InvocationId: invocation), CancellationToken.None);
            throw;
        }
    }

    public async Task<string?> AutomaticallyAsync(Routing.CodingTurnRequest request, Guid profileId, string provider, string model, CancellationToken ct)
    {
        if (request.PlanMode || request.UnattendedAgentTurn || request.CommunicationScope?.UserInitiated != true) return null;
        var input = new ModelCapabilityTools.Input(provider, model);
        var policy = request.Permissions?.Decide(ToolId, ToolSideEffect.Exec);
        if (policy is not null && policy.Verdict != Permissions.CodingPermissionVerdict.Run) return null;
        var guard = services.GetService<IToolInvocationGuard>();
        if (guard is not null && await guard.InspectAsync(new(ToolId, ToolSideEffect.Exec, input, request.ConversationId), ct)
            is not { Allowed: true, RequiresApproval: false }) return null;
        try
        {
            var result = await RepairAsync(provider, model, profileId.ToString("N"), ct);
            return result.Repaired ? JsonSerializer.Serialize(result) : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return "Automatic vision setup could not be verified: " + ex.Message + " Preserve the original request and use an admitted fallback if available."; }
    }
}
