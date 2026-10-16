namespace SwarmCore.Agent.Runtime;

/// <summary>
/// Optional host signal that detects real user interaction while an agent turn
/// is running. This lets completion handlers distinguish an unattended result
/// from one that silently depended on the user clearing a native dialog.
/// </summary>
public interface IAgentTurnInterventionMonitor
{
    long CaptureMarker();

    bool WasInterventionObserved(long marker);
}
