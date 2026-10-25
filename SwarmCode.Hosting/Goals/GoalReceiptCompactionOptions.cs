using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SwarmCore.Agent.Continual;

namespace SwarmCode.Hosting.Goals;

/// <summary>
/// Host settings for the idle-time compaction of goal receipt bodies (<c>Goals:ReceiptCompaction</c> in settings.json).
/// The person's own switch is <see cref="AppPreferences.CompactGoalReceipts"/>; the shared eligibility rules are
/// <see cref="ContinualReceiptRetentionPolicy"/>. Nothing here changes what counts as evidence, only when storage is reclaimed.
/// </summary>
public sealed class GoalReceiptCompactionOptions
{
    public const string SectionName = "Goals:ReceiptCompaction";

    public ContinualReceiptRetentionOptions Retention { get; set; } = new();

    /// <summary>How often an idle workspace looks for releasable receipt texts.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>A goal's body log is rewritten only when at least this many of its bytes are no longer referenced...</summary>
    public long VacuumMinimumDeadBytes { get; set; } = 4L * 1024 * 1024;

    /// <summary>...and those bytes are at least this share of the log (0..1).</summary>
    public double VacuumMinimumDeadShare { get; set; } = 0.25;

    /// <summary>Unused files in the goals folder above this many bytes are reported in the log by the idle pass (0 = never).</summary>
    public long StrayWarningBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>Binds the section; an invalid value keeps the defaults and is logged, so a typo in settings.json never stops the host.</summary>
    public static GoalReceiptCompactionOptions Read(IConfiguration? configuration, ILogger? logger = null)
    {
        var options = new GoalReceiptCompactionOptions();
        if (configuration is null) return options;
        try
        {
            configuration.GetSection(SectionName).Bind(options);
            options.Validate();
            return options;
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidOperationException or FormatException)
        {
            logger?.LogWarning("The {Section} settings are invalid ({Error}); the defaults are used.", SectionName, ex.Message);
            return new GoalReceiptCompactionOptions();
        }
    }

    public void Validate()
    {
        Retention.Validate();
        if (Interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(Interval));
        if (VacuumMinimumDeadBytes < 0) throw new ArgumentOutOfRangeException(nameof(VacuumMinimumDeadBytes));
        if (VacuumMinimumDeadShare is < 0 or > 1 || double.IsNaN(VacuumMinimumDeadShare)) throw new ArgumentOutOfRangeException(nameof(VacuumMinimumDeadShare));
        if (StrayWarningBytes < 0) throw new ArgumentOutOfRangeException(nameof(StrayWarningBytes));
    }
}
