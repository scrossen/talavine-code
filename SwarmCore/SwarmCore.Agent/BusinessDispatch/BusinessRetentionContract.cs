namespace SwarmCore.Agent.BusinessDispatch;

/// <summary>Explicit bounded customer care cadence; sending a check-in is never a retention outcome.</summary>
public static class BusinessRetentionContract
{
    public static void Validate(int intervalDays, int maxCheckIns, DateTimeOffset now, DateTimeOffset firstDue,
        DateTimeOffset permissionExpires)
    {
        if (intervalDays is < 1 or > 365 || maxCheckIns is < 1 or > 12 || firstDue < now
            || permissionExpires <= firstDue || permissionExpires > now.AddYears(1))
            throw new ArgumentException("Customer care requires a future bounded permission, one to twelve check-ins and an explicit interval of one to 365 days.");
    }
    public static string Objective(Guid id) => "retention:" + id.ToString("N");
    public static string Slot(int checkIn) => "check-in:" + checkIn;
}
