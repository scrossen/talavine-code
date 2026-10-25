namespace SwarmCore.Abstractions.Tools;

/// <summary>Trusted in-process adapter evidence, never parsed from model or external tool JSON.
/// True means no physical action in this entire invocation was dispatched. Unknown defaults to false.</summary>
public interface IToolDispatchEvidence
{
    bool NoPhysicalAction => false;
}
