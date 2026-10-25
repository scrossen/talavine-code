using System.Security.Cryptography;
using System.Text.Json;

namespace SwarmCore.Agent.Continual;

public enum ContinualReceiptResolutionOutcome { KnownNoEffect, KnownOutcome }

public sealed record ContinualReceiptObservationBinding(int Attempt, string ToolCallId, string ReceiptSha256);

/// <summary>Host-owned, additive inspection proof. It never changes the historical invocation or authorizes replay.</summary>
public sealed record ContinualReceiptResolution(Guid ObjectiveId, int Attempt, string ToolCallId,
    string ReceiptSha256, ContinualReceiptResolutionOutcome Outcome, string VerifierIdentity,
    string Evidence, IReadOnlyList<ContinualReceiptObservationBinding> Observations, DateTimeOffset VerifiedAtUtc);

public static class ContinualReceiptResolutionPolicy
{
    public static string Fingerprint(ContinualToolReceipt receipt)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(receipt with { HostResolution = null })));

    public static bool IsResolved(ContinualToolReceipt receipt, IReadOnlyList<ContinualToolReceipt> receipts)
        => receipt.HostResolution is { } proof && Matches(receipt, proof)
            && proof.Observations.All(binding => binding is not null && receipts.Count(observation => observation.ObjectiveId == receipt.ObjectiveId
                && observation.Attempt == binding.Attempt && observation.ToolCallId == binding.ToolCallId
                && observation.State == ContinualToolReceiptState.Completed && !observation.IsError
                && observation.WasDispatched && observation.SideEffect == ContinualSideEffectClass.ReadOnly
                && observation.CompletedAtUtc >= observation.StartedAtUtc && observation.CompletedAtUtc <= proof.VerifiedAtUtc
                && observation.StartedAtUtc >= (receipt.CompletedAtUtc ?? receipt.StartedAtUtc)
                && Fingerprint(observation) == binding.ReceiptSha256) == 1);

    public static bool Matches(ContinualToolReceipt receipt, ContinualReceiptResolution proof)
        => proof.ObjectiveId == receipt.ObjectiveId && proof.Attempt == receipt.Attempt && proof.ToolCallId == receipt.ToolCallId
            && proof.ReceiptSha256 == Fingerprint(receipt) && Enum.IsDefined(proof.Outcome)
            && !string.IsNullOrWhiteSpace(proof.VerifierIdentity) && !string.IsNullOrWhiteSpace(proof.Evidence)
            && proof.Observations is { Count: > 0 } && proof.Observations.All(binding => binding is not null)
            && proof.Observations.DistinctBy(binding => (binding.Attempt, binding.ToolCallId)).Count() == proof.Observations.Count
            && proof.VerifiedAtUtc >= (receipt.CompletedAtUtc ?? receipt.StartedAtUtc);

    public static bool NeedsInspection(ContinualToolReceipt receipt, IReadOnlyList<ContinualToolReceipt> receipts)
        => MissionTransitions.NeedsInspection(receipt) && !IsResolved(receipt, receipts);
}

/// <summary>Explicit executor opt-in for non-mission receipt inspection before ordinary execution or verification.</summary>
public interface IContinualObjectiveReceiptRecovery
{
    bool NeedsInspection(ContinualObjectiveSnapshot objective, ContinualToolReceipt receipt);
    Task ReconcileAsync(ContinualObjectiveExecutionRequest request, IContinualObjectiveExecutionReporter reporter,
        CancellationToken ct = default);
}
