namespace SwarmCore.Abstractions.Crm;

/// <summary>
/// Wire shape of <c>GET api/crm/business/cycle</c>. Mirrors <c>SwarmCore.Agent.Business.BusinessCycleSnapshot</c>
/// (which this assembly cannot reference) with each stage carried by its enum name, so a stage the
/// desktop does not know is ignored instead of failing the read. Money totals are null when the
/// caller lacks full-tenant visibility; null means "not shown", never zero.
/// </summary>
public sealed record CrmBusinessCycleSummary
{
    public IReadOnlyList<CrmBusinessCycleStageCount> Stages { get; init; } = [];
    public int StalledCount { get; init; }
    public int? StalledAfterDays { get; init; }
    public decimal? MoneyOwed { get; init; }
    public decimal? MoneyOverdue { get; init; }
    public decimal? EarnedThisMonth { get; init; }
    public string? Currency { get; init; }
    public DateTimeOffset GeneratedAtUtc { get; init; }
}

/// <summary>Open count for one stage, keyed by the stage's enum name (for example <c>Capture</c>).</summary>
public sealed record CrmBusinessCycleStageCount(string Stage, int Count);
