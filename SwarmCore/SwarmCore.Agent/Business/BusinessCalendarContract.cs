using SwarmCore.Agent.BusinessDispatch;

namespace SwarmCore.Agent.Business;

public sealed record BusinessMeetingIntent(Guid CaseId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Summary, string Context)
{
    public void Validate(DateTimeOffset now)
    {
        if (CaseId == Guid.Empty || StartUtc <= now || EndUtc <= StartUtc || EndUtc - StartUtc > TimeSpan.FromHours(8)
            || StartUtc > now.AddDays(365) || string.IsNullOrWhiteSpace(Summary) || Summary.Length > 200 || Context is null || Context.Length > 4000)
            throw new ArgumentException("Select an exact case, future meeting interval, summary and bounded context.");
    }
}
public interface IBusinessCalendarProvider
{
    bool Supports(string accountId);
    /// <summary>Fresh provider availability; authentication failures and incomplete results must fail closed.</summary>
    Task<bool> IsAvailableAsync(BusinessProviderEnvelope envelope, BusinessMeetingIntent intent, CancellationToken ct);
    Task<BusinessProviderReceipt> BookOnceAsync(BusinessProviderEnvelope envelope, BusinessMeetingIntent intent, CancellationToken ct);
    Task<BusinessProviderReceipt?> FindAsync(BusinessProviderEnvelope envelope, BusinessMeetingIntent intent, CancellationToken ct);
}
