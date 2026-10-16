using SwarmCore.Agent.BusinessScenarios;

namespace SwarmCore.Agent.Business;

public enum BusinessInferenceActivityKind { Unknown, Execution, CreativeSynthesis, IndependentAudit, ReportCorrection }
public enum BusinessEvidenceOrigin { Unknown, SeededWorldEvent, InjectedWorldEvent, AgentOutput, SimulatedEffect, PublicResearch, ProcedureReplay, ObservedInbound }

/// <summary>Classifies actual execution and binding metadata, not success or a model's claim.</summary>
public static class BusinessLifecycleTelemetryContract
{
    public static BusinessInferenceActivityKind ClassifyInference(bool executionAudit, bool bindingAudit,
        Guid? executionCorrectionId, Guid? bindingCorrectionId, bool creativeSynthesis = false)
    {
        // Corrections also use an audit HTTP scope. Never count that scope as a
        // new independent audit or routine execution, including inconsistent proof.
        if (executionCorrectionId is not null || bindingCorrectionId is not null)
            return executionCorrectionId is { } id && id != Guid.Empty && bindingCorrectionId == id
                ? BusinessInferenceActivityKind.ReportCorrection : BusinessInferenceActivityKind.Unknown;
        if (executionAudit != bindingAudit) return BusinessInferenceActivityKind.Unknown;
        if (executionAudit) return BusinessInferenceActivityKind.IndependentAudit;
        return creativeSynthesis ? BusinessInferenceActivityKind.CreativeSynthesis : BusinessInferenceActivityKind.Execution;
    }

    public static BusinessEvidenceOrigin WorldOrigin(BusinessScenarioEventKind kind) => kind switch
    {
        BusinessScenarioEventKind.Seed => BusinessEvidenceOrigin.SeededWorldEvent,
        BusinessScenarioEventKind.Injected => BusinessEvidenceOrigin.InjectedWorldEvent,
        BusinessScenarioEventKind.AgentOutput => BusinessEvidenceOrigin.AgentOutput,
        _ => BusinessEvidenceOrigin.Unknown,
    };
}
