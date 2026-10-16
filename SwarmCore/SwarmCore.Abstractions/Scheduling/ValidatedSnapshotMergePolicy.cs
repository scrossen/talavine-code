namespace SwarmCore.Abstractions.Scheduling;

/// <summary>Orders result epochs while preserving a successful repair within its epoch.</summary>
public static class ValidatedSnapshotMergePolicy
{
    public static bool CanReplace(DateTime? currentEpoch, bool? currentValidated,
        DateTime? incomingEpoch, bool? incomingValidated)
    {
        if (currentEpoch is null) return true; // Legacy, unversioned state.
        if (incomingEpoch is null || incomingEpoch < currentEpoch) return false;
        if (incomingEpoch > currentEpoch) return true; // A genuinely new attempt may fail.
        return currentValidated != true || incomingValidated == true;
    }
}
