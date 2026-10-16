using System.Text.Json;
using System.Text.Json.Nodes;
using SwarmCore.Abstractions.AgentWork;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Continual;
using SwarmCore.Agent.Procedures;

namespace SwarmCore.Agent.Business;

/// <summary>
/// Stable host-authenticated routine family, separate from per-run operation/source IDs.
/// Fingerprints contain no credentials. Changes to task/schema, scope, tools, route or
/// safety invalidate replay; current inputs and actual results still change every run.
/// </summary>
public sealed record BusinessStageProcedureDefinition(AgentProcedureKey Key, string TaskSchemaSha256,
    string AuthorityScopeSha256, string ProfileRouteSha256, string SafetyPolicySha256);

/// <summary>
/// Composes the existing learned-procedure runtime with a registered canonical stage
/// verifier. Hosts retain ordinary invocation guards, independent audit and atomic
/// terminal/procedure promotion. This never adopts its own output or grants effects.
/// </summary>
public static class BusinessStageProcedureContextFactory
{
    public static AgentProcedureContext Create(BusinessStageProcedureDefinition definition,
        BusinessStageResultContract contract, OperatorWorkContract work, OperatorWorkOccupancyIdentity identity,
        Guid objectiveId, JsonObject inputs, IAgentProcedureStore store, IReadOnlyList<ToolDescriptor> tools,
        Func<ToolDescriptor, JsonObject, bool> eligibleTool,
        Func<AgentProcedureStep, JsonObject, CancellationToken, Task<bool>> currentPrecondition,
        Func<AgentProcedureRunEvidence, CancellationToken, Task<BusinessStageMatchedResult?>> verifyCurrentResult,
        Func<string, string, JsonNode?, bool>? allowLiteral = null,
        Func<string, string>? formatReceiptReference = null,
        Func<JsonNode?, JsonNode?, bool>? valueEquivalence = null)
    {
        if (definition.Key is null || !BusinessStageResultContract.Bounded(definition.Key.ProfileId, 200)
            || !BusinessStageResultContract.Bounded(definition.Key.AgentId, 200)
            || !BusinessStageResultContract.Bounded(definition.Key.TaskId, 200)
            || new[] { definition.TaskSchemaSha256, definition.AuthorityScopeSha256, definition.ProfileRouteSha256,
                definition.SafetyPolicySha256 }.Any(hash => !ContinualSavedReportCorrectionPolicy.IsSha256(hash))
            || identity.SourceId != work.SourceId || identity.WorkSha256 != work.Sha256
            || definition.Key.AgentId != identity.AgentId || objectiveId == Guid.Empty
            || contract.ResultKind != work.RequiredLocalResult || tools.Count is < 1 or > 32
            || tools.Any(tool => !contract.AllowedToolIds.Contains(tool.ToolId, StringComparer.Ordinal))
            || tools.Select(tool => tool.ToolId).Distinct(StringComparer.Ordinal).Count() != tools.Count)
            throw new ArgumentException("An exact host-bound routine family, current work and registered stage tools are required.");
        ArgumentNullException.ThrowIfNull(eligibleTool);
        ArgumentNullException.ThrowIfNull(currentPrecondition);
        ArgumentNullException.ThrowIfNull(verifyCurrentResult);
        var toolFingerprint = AgentProcedureRuntime.FingerprintTools(tools);
        var binding = ContinualVerificationCandidatePolicy.Hash(JsonSerializer.Serialize(new
        {
            Version = 1, definition.Key, definition.TaskSchemaSha256, definition.AuthorityScopeSha256,
            definition.ProfileRouteSha256, definition.SafetyPolicySha256, toolFingerprint,
            identity.TenantId, identity.EmployeeId, identity.OperatorId, identity.AgentId,
            contract.CanonicalProofKind, contract.ResultKind,
        }));
        BusinessStageMatchedResult? fresh = null;
        return new AgentProcedureContext
        {
            Key = definition.Key, BindingFingerprint = binding, Inputs = (JsonObject)inputs.DeepClone(), Store = store,
            Mode = AgentProcedureMode.VerifiedCompletion, AllowLiteral = allowLiteral, ValueEquivalence = valueEquivalence,
            AllowTool = (tool, arguments) => contract.AllowedToolIds.Contains(tool.ToolId, StringComparer.Ordinal)
                && work.AllowedEffects.Any(effect => effect.ToolId == tool.ToolId)
                && eligibleTool(tool, arguments),
            CheckPreconditionAsync = currentPrecondition,
            VerifyAsync = async (evidence, ct) =>
            {
                fresh = null;
                if (evidence.Receipts.Count is < 1 or > 256 || evidence.Receipts.Any(receipt => !receipt.WasDispatched
                    || receipt.IsError || !receipt.HostCompletionVerified || receipt.OutcomeUncertain == true
                    || receipt.MayHaveSideEffects && receipt.OutcomeUncertain != false
                    || string.IsNullOrWhiteSpace(receipt.InvocationId)
                    || !contract.AllowedToolIds.Contains(receipt.ToolId, StringComparer.Ordinal)
                    || !work.AllowedEffects.Any(effect => effect.ToolId == receipt.ToolId))
                    || evidence.Receipts.Select(receipt => receipt.InvocationId).Distinct(StringComparer.Ordinal).Count() != evidence.Receipts.Count)
                    return null;
                var verified = await verifyCurrentResult(evidence, ct).ConfigureAwait(false);
                if (verified is null || verified.Readback.Identity != identity || verified.Readback.ObjectiveId != objectiveId
                    || verified.Readback.CanonicalProofKind != contract.CanonicalProofKind || verified.Readback.ResultKind != contract.ResultKind
                    || verified.Readback.References.Any(reference => evidence.Receipts.Count(receipt => receipt.InvocationId == reference.SourceReceiptId) != 1))
                    return null;
                fresh = verified;
                return new AgentProcedureVerification(contract.CanonicalProofKind, verified.Readback.CanonicalResultSha256,
                    verified.Readback.ObservedAtUtc)
                {
                    PreconditionsFingerprint = ContinualVerificationCandidatePolicy.Hash(JsonSerializer.Serialize(new
                    { identity.PayloadSha256, identity.WorkSha256, verified.Readback.ResourceFootprintSha256 })),
                    SafetyFingerprint = definition.SafetyPolicySha256,
                };
            },
            FormatResult = _ => fresh is null ? "" : JsonSerializer.Serialize(new
            {
                report = "The current registered local result is verified; this does not claim external delivery or customer acceptance.",
                references = fresh.Readback.References.OrderBy(reference => reference.Kind, StringComparer.Ordinal)
                    .ThenBy(reference => reference.RecordId, StringComparer.Ordinal).Select(reference => new
                    // The host may render its own receipt-reference convention (for example a "tool:" prefix); verification stays on raw invocation ids.
                    { kind = reference.Kind, recordId = reference.RecordId, sourceReceiptId = formatReceiptReference?.Invoke(reference.SourceReceiptId) ?? reference.SourceReceiptId }).ToArray(),
            }),
        };
    }
}
