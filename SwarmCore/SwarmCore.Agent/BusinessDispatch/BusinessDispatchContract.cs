using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SwarmCore.Agent.BusinessDispatch;

public enum BusinessDispatchState { AwaitingReview, Prepared, Dispatching, Uncertain, Accepted, Confirmed, Rejected, Cancelled, Simulated }
public enum BusinessReplyIntent { Interested, Declined, Unsubscribe, Bounce, NotNow, Ambiguous }

/// <summary>Host-bound exact action. Objective + slot is stable across midnight and restarts.</summary>
public sealed record BusinessDispatchRequest(Guid PolicyId, string ObjectiveId, string Slot,
    string CampaignId, string Target, string Subject, string Body, decimal CostReservation = 0,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<BusinessMessageMedia>? Media = null);
public sealed record BusinessDispatchResult(bool Ok, Guid? ActionId, string State, string Detail, bool Replay = false);
public enum BusinessReceiptDisposition { Provider, Simulated }
public sealed record BusinessProviderReceipt(bool Accepted, bool Confirmed, string ReceiptId, string Detail,
    BusinessReceiptDisposition Disposition = BusinessReceiptDisposition.Provider);
public sealed record BusinessDispatchAllowances(int EmployeeDaily, int AccountDaily, int CampaignDaily, int ContactDaily, decimal DailyBudget);
public sealed record BusinessDispatchUsage(int EmployeeReserved, int AccountReserved, int CampaignReserved, int ContactReserved, decimal SpendReserved);
public sealed record BusinessProviderAttempt(BusinessProviderReceipt? Receipt, bool Uncertain);

/// <summary>The host resolves credentials and current account identity; providers never receive authority from content.</summary>
public interface IBusinessDispatchTransport
{
    string Channel { get; }
    Task<bool> IsReadyAsync(BusinessProviderEnvelope envelope, CancellationToken ct);
    Task<BusinessProviderReceipt> SendOnceAsync(BusinessProviderEnvelope envelope, CancellationToken ct);
    /// <summary>Read-only exact receipt reconciliation. Missing receipts must never trigger a resend.</summary>
    Task<BusinessProviderReceipt?> ReconcileAsync(BusinessProviderEnvelope envelope, CancellationToken ct);
}

public static class BusinessDispatchContract
{
    public const string SendCapability = "business.send";
    public static string Hash(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    public static string NormalizeTarget(string value) => value.Trim().ToLowerInvariant();
    public static void Validate(BusinessDispatchRequest request)
    {
        if (request.PolicyId == Guid.Empty || string.IsNullOrWhiteSpace(request.ObjectiveId) || request.ObjectiveId.Length > 200
            || string.IsNullOrWhiteSpace(request.Slot) || request.Slot.Length > 80 || string.IsNullOrWhiteSpace(request.CampaignId)
            || request.CampaignId.Length > 100 || string.IsNullOrWhiteSpace(request.Target) || request.Target.Length > 320
            || string.IsNullOrWhiteSpace(request.Subject) || request.Subject.Length > 200
            || string.IsNullOrWhiteSpace(request.Body) || request.Body.Length > 10000 || request.CostReservation < 0)
            throw new ArgumentException("An exact bounded objective, action, campaign, target and content are required.");
        BusinessMessageMediaContract.Validate(request.Media);
    }
    public static bool IsWorkingTime(DateTimeOffset now, string timezone, int startHour, int endHour)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timezone);
        var hour = TimeZoneInfo.ConvertTime(now, zone).Hour;
        return startHour <= hour && hour < endHour;
    }
    public static bool StopsSequence(BusinessReplyIntent intent) => Enum.IsDefined(intent);
    /// <summary>Usage includes pending provider attempts and uncertainty. Hosts reserve atomically after this check.</summary>
    public static bool FitsAllowance(BusinessDispatchAllowances limits, BusinessDispatchUsage usage, decimal cost)
    {
        if (cost < 0 || limits.EmployeeDaily < 1 || limits.AccountDaily < 1 || limits.CampaignDaily < 1 || limits.ContactDaily < 1
            || limits.DailyBudget < 0 || usage.EmployeeReserved < 0 || usage.AccountReserved < 0 || usage.CampaignReserved < 0
            || usage.ContactReserved < 0 || usage.SpendReserved < 0) return false;
        return usage.EmployeeReserved < limits.EmployeeDaily && usage.AccountReserved < limits.AccountDaily
            && usage.CampaignReserved < limits.CampaignDaily && usage.ContactReserved < limits.ContactDaily
            && usage.SpendReserved <= limits.DailyBudget && cost <= limits.DailyBudget - usage.SpendReserved;
    }
    /// <summary>Only a host with a committed Dispatching claim may call this. Errors retain uncertainty; there is no retry.</summary>
    public static async Task<BusinessProviderAttempt> ExecuteClaimedOnceAsync(IBusinessDispatchTransport transport,
        BusinessProviderEnvelope envelope, CancellationToken ct)
    {
        try { return new(await transport.SendOnceAsync(envelope, ct), false); }
        catch (Exception) { return new(null, true); }
    }
}
