namespace SwarmCore.Abstractions.Scheduling;

/// <summary>Retry transient work; defer deterministic input failures until its evidence changes.</summary>
public static class InputEvidenceRetry
{
    public static bool IsInputFailure(Exception error)
        => error is InvalidDataException or FormatException or NotSupportedException;

    public static bool ShouldAttempt(string? blockedEvidence, string currentEvidence)
        => string.IsNullOrEmpty(blockedEvidence)
           || !string.Equals(blockedEvidence, currentEvidence, StringComparison.Ordinal);
}
