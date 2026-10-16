namespace SwarmCore.Agent.Catalog;

/// <summary>Physical adapter evidence, never parsed from model-authored text.</summary>
public interface IToolExecutionEvidence
{
    bool Succeeded { get; }
    /// <summary>The contained local process exited (including after timeout termination).
    /// No non-idempotent external operation can have occurred; local inspection/repair may retry.</summary>
    bool LocalProcessCompleted { get; }
    bool NoPhysicalAction => false;
}
