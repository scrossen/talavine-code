namespace SwarmCore.Agent.Continual;

/// <summary>Completion-proof classification, never an execution permission or uncertain-outcome exemption.</summary>
public static class ContinualSideEffects
{
    /// <summary>Execution bookkeeping does not itself produce the user's requested resource.
    /// Unknown, failed and Started operation inspection still uses the unchanged full receipt policy.</summary>
    public static bool MayAffectTaskResources(ContinualSideEffectClass effect)
        => effect is not (ContinualSideEffectClass.ReadOnly or ContinualSideEffectClass.ExecutionMetadata);
}
