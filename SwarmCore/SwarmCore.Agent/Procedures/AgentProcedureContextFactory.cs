using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Learning;
using SwarmCore.Agent.ToolUseEngine;

namespace SwarmCore.Agent.Procedures;

public interface IAgentProcedureContextFactory
{
    AgentProcedureContext? CreateReadOnly(AgentProcedureKey key, string bindingFingerprint, JsonObject inputs,
        IReadOnlyList<ToolDescriptor> tools, bool terminalResultAuthorized = false,
        AgentRoutineLearningContext? routineLearningContext = null);
}

/// <summary>
/// Conservative shared host default for repeatable jobs. Only a typed successful execution-evidence
/// result with a host terminal formatter can prove completion. Arbitrary successful dispatch or model
/// prose cannot promote a plan. Richer jobs supply their own task-specific context/verifier.
/// </summary>
public sealed class AgentProcedureContextFactory(IAgentProcedureStore store) : IAgentProcedureContextFactory
{
    public AgentProcedureContext? CreateReadOnly(AgentProcedureKey key, string bindingFingerprint,
        JsonObject inputs, IReadOnlyList<ToolDescriptor> tools, bool terminalResultAuthorized = false,
        AgentRoutineLearningContext? routineLearningContext = null)
    {
        var terminal = terminalResultAuthorized && tools.Count == 1 && tools.Any(t => t.SideEffect == ToolSideEffect.ReadOnly && t.ResolveSideEffect is null
            && t.FormatResultForUser is not null && typeof(IToolExecutionEvidence).IsAssignableFrom(t.OutputType));
        bool ReadEvidence(ToolDescriptor t) => t.SupportsProcedureEvidencePreparation
            && t.SideEffect == ToolSideEffect.ReadOnly && t.ResolveSideEffect is null
            && typeof(IToolExecutionEvidence).IsAssignableFrom(t.OutputType);
        if (!terminal && !tools.Any(ReadEvidence)) return null;
        if (routineLearningContext is not null
            && (routineLearningContext.Key != key || routineLearningContext.BindingFingerprint != bindingFingerprint
                || routineLearningContext.ToolFingerprint != AgentProcedureRuntime.FingerprintTools(
                    tools.GroupBy(tool => tool.ToolId, StringComparer.Ordinal).Select(group => group.First()))))
            throw new InvalidDataException("Routine learning must use the exact procedure key, binding, and routed tool fingerprint.");
        return new()
        {
            Key = key, BindingFingerprint = bindingFingerprint, Inputs = inputs, Store = store,
            Mode = terminal ? AgentProcedureMode.VerifiedCompletion : AgentProcedureMode.EvidencePreparation,
            RoutineLearningContext = routineLearningContext,
            AllowTool = (tool, _) => terminal ? tool.SideEffect == ToolSideEffect.ReadOnly && tool.ResolveSideEffect is null : ReadEvidence(tool),
            VerifyAsync = (evidence, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                var last = evidence.Receipts.LastOrDefault();
                AgentProcedureVerification? proof = last is { IsError: false, HostCompletionVerified: true }
                    && (!terminal || !string.IsNullOrWhiteSpace(last.FormattedResult))
                    && evidence.Receipts.All(r => !r.IsError && !r.MayHaveSideEffects && r.HostCompletionVerified)
                    ? new(terminal ? "typed-terminal-result-v1" : "typed-read-prefix-v1", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                        JsonSerializer.Serialize(evidence.Receipts)))), DateTimeOffset.UtcNow)
                    {
                        PreconditionsFingerprint = routineLearningContext?.PreconditionsFingerprint,
                        SafetyFingerprint = routineLearningContext is null ? null
                            : AgentRoutineLearningPolicy.SafetyFingerprint(routineLearningContext.Safety),
                    } : null;
                return Task.FromResult(proof);
            },
            FormatResult = evidence => evidence.Receipts[^1].FormattedResult ?? ""
        };
    }
}
