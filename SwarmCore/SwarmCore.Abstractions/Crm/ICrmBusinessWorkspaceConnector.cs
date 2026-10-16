namespace SwarmCore.Abstractions.Crm;

/// <summary>Authenticated host readiness, separate from a Work account's authentication
/// and from permission to perform any business action.</summary>
public sealed record CrmBusinessWorkspaceReadiness(bool Ready, string State, string Detail, string? CandidateVersion = null)
{
    public int ProtocolVersion { get; init; } = 1;
}

public interface ICrmBusinessWorkspaceConnector
{
    Task<CrmBusinessWorkspaceReadiness> CheckAsync(CrmConnection connection, CancellationToken ct = default);
}
