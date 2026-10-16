using SwarmCore.Agent.Runtime;
using SwarmCore.Agent.Continual;

namespace SwarmCore.Missions;

/// <summary>Direct production primitives obey the same revocation fence as engine dispatch.</summary>
public static class MissionPrimitiveFence
{
    public static bool IsMission => AsyncLocalCurrentTurnContextAccessor.Ambient?.MissionObjectiveId is not null;
    public static Task AuthorizeHttpAsync(string url, string method, CancellationToken ct)
    {
        if (!IsMission) return Task.CompletedTask;
        var uri = new Uri(url, UriKind.Absolute);
        if (uri.Scheme is not ("http" or "https")) throw new MissionActionSkippedException("unsupported-endpoint-scheme");
        return AuthorizeAsync(new(MissionResourceKind.Endpoint, uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped)),
            method.Equals("GET", StringComparison.OrdinalIgnoreCase) || method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)
                ? MissionEffect.Inspect : MissionEffect.Send, ct);
    }

    public static async Task AuthorizeAsync(MissionResource resource, MissionEffect effects, CancellationToken ct)
    {
        if (AsyncLocalCurrentTurnContextAccessor.Ambient is not { MissionObjectiveId: not null } turn) return;
        var decision = turn.MissionResources?.Evaluate(new(resource, effects, turn.ProfileId?.ToString("D") ?? ""));
        if (decision?.Allowed != true) throw new MissionActionSkippedException(decision?.ReasonCode ?? "missing-resource-authority");
        await ValidateAsync(ct);
    }

    public static async Task ValidateAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (AsyncLocalCurrentTurnContextAccessor.Ambient is not { MissionObjectiveId: not null } turn) return;
        var validate = turn.ValidateMissionExecution
            ?? throw new InvalidOperationException("Mission execution requires the current durable lease fence.");
        await validate(ct);
        ct.ThrowIfCancellationRequested();
    }
}
