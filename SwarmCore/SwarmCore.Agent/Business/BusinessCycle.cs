using System.Collections.ObjectModel;
using SwarmCore.Agent.Catalog;

namespace SwarmCore.Agent.Business;

/// <summary>The ten stages of an owner-led business's client cycle, in clockwise order.</summary>
public enum BusinessCycleStage
{
    Attract = 1,
    Capture = 2,
    QualifyNurture = 3,
    ProposeClose = 4,
    Onboard = 5,
    Deliver = 6,
    CommunicateSupport = 7,
    InvoiceCollect = 8,
    RetainReviewRefer = 9,
    OperateLearn = 10,
}

/// <summary>Display copy for one stage: its name and the Today chip label shown when its count is non-zero.</summary>
public sealed record BusinessCycleStageInfo(BusinessCycleStage Stage, string DisplayName, string ChipLabel);

/// <summary>
/// Stage metadata shared by hosts. Stage membership of a tool is descriptor metadata the
/// host tags under <see cref="StageMetadataKey"/>; SwarmCore keeps no list of host tool ids.
/// </summary>
public static class BusinessCycle
{
    /// <summary>Tool-descriptor metadata key whose value is a <see cref="BusinessCycleStage"/> name.</summary>
    public const string StageMetadataKey = "businessCycleStage";

    /// <summary>All ten stages in cycle order with their display name and Today chip label.</summary>
    public static IReadOnlyList<BusinessCycleStageInfo> Stages { get; } =
    [
        new(BusinessCycleStage.Attract, "Attract", "New visitors"),
        new(BusinessCycleStage.Capture, "Capture", "New inquiries"),
        new(BusinessCycleStage.QualifyNurture, "Qualify and nurture", "Quiet leads"),
        new(BusinessCycleStage.ProposeClose, "Propose and close", "Awaiting yes"),
        new(BusinessCycleStage.Onboard, "Onboard", "Getting started"),
        new(BusinessCycleStage.Deliver, "Deliver", "In progress"),
        new(BusinessCycleStage.CommunicateSupport, "Communicate and support", "Needs reply"),
        new(BusinessCycleStage.InvoiceCollect, "Invoice and collect", "Money owed"),
        new(BusinessCycleStage.RetainReviewRefer, "Retain, review, refer", "Ask for review"),
        new(BusinessCycleStage.OperateLearn, "Operate and learn", "This week"),
    ];

    /// <summary>Display copy for one stage.</summary>
    public static BusinessCycleStageInfo Info(BusinessCycleStage stage)
        => Stages.FirstOrDefault(s => s.Stage == stage)
           ?? throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown business cycle stage.");

    /// <summary>Human display name, for example "Invoice and collect".</summary>
    public static string DisplayName(BusinessCycleStage stage) => Info(stage).DisplayName;

    /// <summary>Today chip label, for example "Money owed".</summary>
    public static string ChipLabel(BusinessCycleStage stage) => Info(stage).ChipLabel;

    /// <summary>Metadata entry a host merges into a tool descriptor to tag its stage.</summary>
    public static KeyValuePair<string, string> MetadataEntry(BusinessCycleStage stage)
    {
        _ = Info(stage);
        return new(StageMetadataKey, stage.ToString());
    }

    /// <summary>Returns a copy of <paramref name="metadata"/> with the stage tag set.</summary>
    public static IReadOnlyDictionary<string, string> Tag(IReadOnlyDictionary<string, string>? metadata, BusinessCycleStage stage)
    {
        var copy = metadata is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(metadata);
        var entry = MetadataEntry(stage);
        copy[entry.Key] = entry.Value;
        return new ReadOnlyDictionary<string, string>(copy);
    }

    /// <summary>Reads the stage tag. Only an exact defined stage name is accepted; numbers and unknown names fail.</summary>
    public static bool TryGetStage(IReadOnlyDictionary<string, string>? metadata, out BusinessCycleStage stage)
    {
        stage = default;
        if (metadata is null || !metadata.TryGetValue(StageMetadataKey, out var value) || string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        if (!char.IsLetter(text[0]) || text.Any(c => !char.IsAsciiLetter(c))) return false;
        if (!Enum.TryParse(text, ignoreCase: true, out BusinessCycleStage parsed) || !Enum.IsDefined(parsed)) return false;
        stage = parsed;
        return true;
    }

    /// <summary>Reads the stage tag a host placed on a tool descriptor.</summary>
    public static bool TryGetStage(ToolDescriptor descriptor, out BusinessCycleStage stage)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return TryGetStage(descriptor.Metadata, out stage);
    }
}

/// <summary>Count of open items in one stage.</summary>
public sealed record BusinessCycleStageCount(BusinessCycleStage Stage, int Count);

/// <summary>
/// Read model of the whole cycle shared by the CRM cycle endpoint and the Today strip.
/// Money totals are null when the caller lacks full-tenant visibility; null means "not shown",
/// never zero.
/// </summary>
public sealed record BusinessCycleSnapshot
{
    /// <summary>Per-stage open counts. A stage that is absent counts as zero.</summary>
    public IReadOnlyList<BusinessCycleStageCount> Stages { get; init; } = [];

    /// <summary>Cases with no activity for the configured number of days in any stage.</summary>
    public int StalledCount { get; init; }

    /// <summary>Optional quiet-days threshold used to derive <see cref="StalledCount"/>.</summary>
    public int? StalledAfterDays { get; init; }

    /// <summary>Issued and unpaid total; null without full-tenant visibility.</summary>
    public decimal? MoneyOwed { get; init; }

    /// <summary>Past-due portion of <see cref="MoneyOwed"/>; null without full-tenant visibility.</summary>
    public decimal? MoneyOverdue { get; init; }

    /// <summary>Recorded payments this calendar month; null without full-tenant visibility.</summary>
    public decimal? EarnedThisMonth { get; init; }

    /// <summary>ISO 4217 currency of the money totals, when any are present.</summary>
    public string? Currency { get; init; }

    /// <summary>When the snapshot was computed.</summary>
    public required DateTimeOffset GeneratedAtUtc { get; init; }

    /// <summary>True when the caller was allowed to see money totals.</summary>
    public bool IncludesMoney => MoneyOwed is not null || MoneyOverdue is not null || EarnedThisMonth is not null;

    /// <summary>Open count for one stage; zero when absent.</summary>
    public int CountFor(BusinessCycleStage stage) => Stages.Where(s => s.Stage == stage).Sum(s => s.Count);
}
