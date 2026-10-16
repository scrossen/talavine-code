namespace SwarmCore.Agent.Continual;

/// <summary>Completion alerts describe host-verified outcomes, never model promises or routine reads.</summary>
public static class CompletedWorkNotificationPolicy
{
    public static bool IsSignificant(bool hasVerifiedChange, TimeSpan elapsed, bool hasResult, string? deliveryMode = null)
        => hasResult && (deliveryMode switch
        {
            "report" => true,
            "verified_actions" => hasVerifiedChange,
            _ => hasVerifiedChange || elapsed >= TimeSpan.FromMinutes(2),
        });
}
