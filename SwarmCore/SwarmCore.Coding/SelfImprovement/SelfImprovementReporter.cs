using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SwarmCore.Coding.SelfImprovement;

/// <summary>
/// The one way sightings enter the backlog. Folds repeats by fingerprint, keeps a dismissed item dismissed, never
/// throws (a failed report is logged), and refuses new items once the open backlog reaches its cap.
/// </summary>
public sealed class SelfImprovementReporter
{
    private readonly ISelfImprovementBacklog _backlog;
    private readonly Func<SelfImprovementOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public SelfImprovementReporter(ISelfImprovementBacklog backlog, Func<SelfImprovementOptions> options, TimeProvider? time = null, ILogger<SelfImprovementReporter>? logger = null)
    {
        _backlog = backlog;
        _options = options;
        _time = time ?? TimeProvider.System;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public ISelfImprovementBacklog Backlog => _backlog;

    public SelfImprovementOptions Options => _options();

    /// <summary>Raised on any thread after a sighting changed the backlog.</summary>
    public event Action<SelfImprovementItem, bool>? Reported;

    /// <summary>Files a sighting. Returns the stored item, or null when the loop is off, the report is empty or the backlog is full.</summary>
    public async Task<SelfImprovementItem?> ReportAsync(SelfImprovementReport report, CancellationToken ct = default)
    {
        var options = _options();
        if (!options.Enabled) return null;
        if (report.Kind == SelfImprovementKind.Defect && !options.CaptureDefects) return null;
        if (report.Kind == SelfImprovementKind.FeatureRequest && !options.CaptureFeatureRequests) return null;
        if (string.IsNullOrWhiteSpace(report.Title) || string.IsNullOrWhiteSpace(report.Details)) return null;
        var title = Bound(report.Title.Trim(), 200);
        var details = Bound(report.Details.Trim(), Math.Max(200, options.MaxDetailCharacters));
        var fingerprint = SelfImprovementFingerprint.IsValid(report.Fingerprint) ? report.Fingerprint!
            : SelfImprovementFingerprint.Compute(report.Kind, title, details);
        var now = _time.GetUtcNow().UtcDateTime;
        try
        {
            var open = (await _backlog.ListAsync(ct).ConfigureAwait(false)).Count(i => i.IsOpen);
            var full = open >= options.MaxQueued;
            var (item, created) = await _backlog.UpsertByFingerprintAsync(fingerprint,
                create: () =>
                {
                    if (full) throw new BacklogFullException();
                    return new SelfImprovementItem
                    {
                        Id = Guid.NewGuid().ToString("N"), Kind = report.Kind, Title = title, Fingerprint = fingerprint, Details = details,
                        Source = report.Source, ConversationId = report.ConversationId, Occurrences = 1, FirstSeenUtc = now, LastSeenUtc = now,
                        Stage = SelfImprovementStage.Queued,
                    };
                },
                // A repeat sighting: count it, refresh where it was last seen. Stage is untouched: dismissed stays dismissed,
                // a verified fix that is not installed yet keeps showing up until the person ships it.
                update: existing => existing with
                {
                    Occurrences = existing.Occurrences + 1, LastSeenUtc = now,
                    ConversationId = report.ConversationId ?? existing.ConversationId, Source = report.Source ?? existing.Source,
                }, ct).ConfigureAwait(false);
            try { Reported?.Invoke(item, created); } catch (Exception ex) { _logger.LogDebug(ex, "A self-improvement listener failed."); }
            return item;
        }
        catch (BacklogFullException)
        {
            _logger.LogInformation("Self-improvement backlog is full ({Max} open items); not queuing '{Title}'.", options.MaxQueued, title);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not record a self-improvement sighting ({Title}).", title);
            return null;
        }
    }

    /// <summary>Files an exception when the classifier calls it a product defect; otherwise nothing.</summary>
    public Task<SelfImprovementItem?> ReportDefectAsync(Exception exception, string source, string? conversationId = null, string? context = null, CancellationToken ct = default)
    {
        var candidate = DefectClassifier.FromException(exception, context);
        return candidate is null ? Task.FromResult<SelfImprovementItem?>(null)
            : ReportAsync(new SelfImprovementReport(SelfImprovementKind.Defect, candidate.Title, candidate.Details, candidate.Fingerprint, source, conversationId), ct);
    }

    /// <summary>Files error text (a lane error, a failed tool result) when it names a product exception; otherwise nothing.</summary>
    public Task<SelfImprovementItem?> ReportErrorTextAsync(string? message, string? details, string source, string? conversationId = null, string? context = null, CancellationToken ct = default)
    {
        var candidate = DefectClassifier.FromErrorText(message, details, context);
        return candidate is null ? Task.FromResult<SelfImprovementItem?>(null)
            : ReportAsync(new SelfImprovementReport(SelfImprovementKind.Defect, candidate.Title, candidate.Details, candidate.Fingerprint, source, conversationId), ct);
    }

    /// <summary>Files a capability the person asked for and SwarmCode does not have.</summary>
    public Task<SelfImprovementItem?> ReportFeatureRequestAsync(string request, string missing, string? workaround, string source, string? conversationId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request)) return Task.FromResult<SelfImprovementItem?>(null);
        var title = Bound("Feature request: " + FirstLine(request), 200);
        var details = "The person asked: " + request.Trim()
            + "\nWhat is missing: " + (string.IsNullOrWhiteSpace(missing) ? "Talavine Code has no way to do this yet." : missing.Trim())
            + (string.IsNullOrWhiteSpace(workaround) ? "" : "\nWorkaround used meanwhile: " + workaround.Trim());
        var fingerprint = SelfImprovementFingerprint.Compute(SelfImprovementKind.FeatureRequest, FirstLine(request), missing);
        return ReportAsync(new SelfImprovementReport(SelfImprovementKind.FeatureRequest, title, details, fingerprint, source, conversationId), ct);
    }

    private sealed class BacklogFullException : Exception;

    private static string FirstLine(string text)
    {
        var trimmed = text.Trim();
        var newline = trimmed.IndexOfAny(['\r', '\n']);
        return newline < 0 ? trimmed : trimmed[..newline];
    }

    private static string Bound(string text, int max) => text.Length <= max ? text : text[..(max - 3)] + "...";
}
