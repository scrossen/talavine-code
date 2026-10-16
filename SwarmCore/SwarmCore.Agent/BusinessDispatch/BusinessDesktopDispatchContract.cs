namespace SwarmCore.Agent.BusinessDispatch;

/// <summary>Explicit desktop business assignment. CRM derives employee, tenant and sender from its owner policy.</summary>
public sealed record BusinessDesktopDispatchRequest(BusinessDispatchRequest Action, bool Dispatch);
public interface IBusinessDesktopDispatchConnector
{
    Task<string?> ResolveDestinationAsync(CancellationToken ct);
    Task<BusinessDispatchResult> ExecuteAsync(BusinessDesktopDispatchRequest request,CancellationToken ct);
}
