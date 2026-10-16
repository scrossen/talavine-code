using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace SwarmCore.Abstractions.Crm;

[JsonConverter(typeof(JsonStringEnumConverter<ImprovementStage>))]
public enum ImprovementStage { Reported, Reproduced, Fixing, Validated, Released, Verified, NeedsInformation, Deferred }

/// <summary>Sanitized vendor evidence. Original objectives and their authority remain in the originating host.</summary>
public sealed record ImprovementReport(
    [Required, MaxLength(200)] string Title,
    [Required, MaxLength(64)] string Fingerprint,
    [Required, MaxLength(4000)] string ExpectedBehavior,
    [Required, MaxLength(4000)] string ActualBehavior,
    [Required, MaxLength(4000)] string Reproduction,
    [Required, MaxLength(2000)] string AcceptanceTest,
    [Required, MaxLength(80)] string Product,
    [Required, MaxLength(80)] string AppVersion,
    [MaxLength(255)] string? ReplyEmail = null,
    [MaxLength(80)] string Platform = "unknown");

public sealed record ImprovementTicket(
    long TicketId, string TicketNumber, ImprovementReport Report, ImprovementStage Stage,
    long Revision = 0, string? ChangeRevision = null, string? ReleasedVersion = null,
    string? Evidence = null, string? ClaimOwner = null, DateTimeOffset? ClaimExpiresAt = null,
    IReadOnlyList<string>? ReleasedPlatforms = null);

public sealed record ImprovementAdvance(
    long ExpectedRevision, ImprovementStage Stage, [Required, MaxLength(4000)] string Evidence,
    [MaxLength(64)] string? ChangeRevision = null, [MaxLength(80)] string? ReleasedVersion = null,
    IReadOnlyList<string>? ReleasedPlatforms = null);

public sealed record ImprovementVerification(long ExpectedRevision, [Required, MaxLength(80)] string InstalledVersion,
    bool Passed, [Required, MaxLength(4000)] string Evidence);
public sealed record ImprovementClaim(long ExpectedRevision, [Required, MaxLength(120)] string Owner, [Range(1, 120)] int LeaseMinutes = 30);

/// <summary>Host identity of the installed artifact, distinct from class-library assembly versions.</summary>
public interface IImprovementInstalledArtifact
{
    string Product { get; }
    string Version { get; }
    string Platform { get; }
}

/// <summary>Versioned repair lifecycle; release is never verification of the originating task.</summary>
public static class ImprovementTicketPolicy
{
    public static ImprovementTicket Advance(ImprovementTicket ticket, ImprovementAdvance change)
    {
        if (ticket.Revision != change.ExpectedRevision) throw new InvalidOperationException("Ticket changed; reload before updating.");
        if (string.IsNullOrWhiteSpace(change.Evidence) || change.Evidence.Length > 4000) throw new ArgumentException("Bounded evidence is required.");
        var allowed = (ticket.Stage, change.Stage) switch
        {
            (ImprovementStage.Reported, ImprovementStage.Reproduced or ImprovementStage.NeedsInformation or ImprovementStage.Deferred) => true,
            (ImprovementStage.NeedsInformation or ImprovementStage.Deferred, ImprovementStage.Reported) => true,
            (ImprovementStage.Reproduced, ImprovementStage.Fixing or ImprovementStage.NeedsInformation) => true,
            (ImprovementStage.Fixing, ImprovementStage.Validated or ImprovementStage.Reproduced or ImprovementStage.NeedsInformation) => true,
            (ImprovementStage.Validated, ImprovementStage.Released or ImprovementStage.Reproduced) => true,
            (ImprovementStage.Released, ImprovementStage.Released) => change.ReleasedVersion == ticket.ReleasedVersion
                && (change.ChangeRevision is null || change.ChangeRevision == ticket.ChangeRevision),
            _ => false,
        };
        if (!allowed) throw new InvalidOperationException($"Cannot move {ticket.Stage} to {change.Stage}.");
        var commit = change.ChangeRevision ?? ticket.ChangeRevision;
        var version = change.ReleasedVersion ?? ticket.ReleasedVersion;
        if (change.Stage is ImprovementStage.Validated or ImprovementStage.Released
            && (commit is null || !System.Text.RegularExpressions.Regex.IsMatch(commit, "\\A[0-9a-fA-F]{40,64}\\z")))
            throw new ArgumentException("An exact committed source revision is required.");
        if (change.Stage == ImprovementStage.Released && !Version.TryParse(version, out _))
            throw new ArgumentException("An exact released app version is required.");
        if (change.Stage == ImprovementStage.Released && change.ReleasedPlatforms is not { Count: > 0 })
            throw new ArgumentException("Identify the platform lanes actually published.");
        return ticket with { Stage = change.Stage, Revision = ticket.Revision + 1,
            Evidence = change.Evidence, ChangeRevision = commit, ReleasedVersion = version,
            ClaimOwner = change.Stage is ImprovementStage.Validated or ImprovementStage.Released or ImprovementStage.NeedsInformation ? null : ticket.ClaimOwner,
            ClaimExpiresAt = change.Stage is ImprovementStage.Validated or ImprovementStage.Released or ImprovementStage.NeedsInformation ? null : ticket.ClaimExpiresAt,
            ReleasedPlatforms = change.ReleasedPlatforms is null ? ticket.ReleasedPlatforms
                : (ticket.ReleasedPlatforms ?? []).Concat(change.ReleasedPlatforms).Distinct(StringComparer.Ordinal).ToArray() };
    }

    public static ImprovementTicket Verify(ImprovementTicket ticket, ImprovementVerification verification)
    {
        if (ticket.Revision != verification.ExpectedRevision) throw new InvalidOperationException("Ticket changed; reload before verifying.");
        if (ticket.Stage != ImprovementStage.Released) throw new InvalidOperationException("Only a released repair can be verified.");
        if (ticket.ReleasedPlatforms?.Contains(ticket.Report.Platform, StringComparer.Ordinal) != true)
            throw new InvalidOperationException("The repair has not been published for the originating platform.");
        if (!Version.TryParse(ticket.ReleasedVersion, out var released)
            || !Version.TryParse(verification.InstalledVersion, out var installed) || installed < released)
            throw new InvalidOperationException("The running artifact does not contain the released repair.");
        if (string.IsNullOrWhiteSpace(verification.Evidence) || verification.Evidence.Length > 4000) throw new ArgumentException("Bounded verification evidence is required.");
        return ticket with { Stage = verification.Passed ? ImprovementStage.Verified : ImprovementStage.Reported,
            Revision = ticket.Revision + 1, Evidence = verification.Evidence,
            ClaimOwner = null, ClaimExpiresAt = null,
            ChangeRevision = verification.Passed ? ticket.ChangeRevision : null,
            ReleasedVersion = verification.Passed ? ticket.ReleasedVersion : null,
            ReleasedPlatforms = verification.Passed ? ticket.ReleasedPlatforms : null };
    }

    public static string SupportStatus(ImprovementStage stage) => stage switch
    {
        ImprovementStage.Reported => "New",
        ImprovementStage.NeedsInformation => "Waiting on Customer",
        ImprovementStage.Released => "Resolved",
        ImprovementStage.Verified => "Closed",
        _ => "In Progress",
    };
}
