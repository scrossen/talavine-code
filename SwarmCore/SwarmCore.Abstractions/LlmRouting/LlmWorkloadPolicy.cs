namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>Host-owned purpose, not inferred from untrusted prompt/message text.</summary>
public enum LlmWorkloadKind
{
    Unspecified = 0,
    InteractiveResponse = 1,
    FirstValueBootstrap = 2,
    FreshnessCritical = 3,
    DeferredEnrichment = 4,
    Maintenance = 5,
}

/// <summary>
/// Scheduling intent is independent of qualification, privacy, pins and spend
/// authority. These preferences never relax a hard admission gate or mint budget.
/// Hosts opt bounded bootstrap/dependency work in explicitly; background alone
/// does not imply that slow, low-capability processing is appropriate.
/// </summary>
public static class LlmWorkloadPolicy
{
    public const string Version = "workload-v1";

    public static LlmWorkloadKind Resolve(
        LlmWorkloadKind requested, LlmInteractivity interactivity, string? functionType = null)
    {
        if (requested != LlmWorkloadKind.Unspecified && Enum.IsDefined(requested)) return requested;
        if (interactivity == LlmInteractivity.Unspecified && functionType is not null)
            interactivity = FunctionTypeProfiles.Resolve(functionType).Interactivity;
        return interactivity == LlmInteractivity.UserWaiting
            ? LlmWorkloadKind.InteractiveResponse : LlmWorkloadKind.DeferredEnrichment;
    }

    public static bool IsLatencySensitive(LlmWorkloadKind workload)
        => workload is LlmWorkloadKind.InteractiveResponse
            or LlmWorkloadKind.FirstValueBootstrap
            or LlmWorkloadKind.FreshnessCritical;

    public static bool PrefersHighQuality(LlmWorkloadKind workload, ModelScoutTier tier)
        => IsLatencySensitive(workload) || tier == ModelScoutTier.Frontier;

    public static bool UsesEconomicalOrdering(LlmWorkloadKind workload, ModelScoutTier tier)
        => !IsLatencySensitive(workload) && tier != ModelScoutTier.Frontier;
}
