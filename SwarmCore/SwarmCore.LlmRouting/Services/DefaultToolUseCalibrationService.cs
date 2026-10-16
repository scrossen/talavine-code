using Microsoft.Extensions.Logging;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting.Services;

/// <summary>
/// Default calibration orchestrator: cache → validator → cache write. Staleness is encoded
/// entirely in the <see cref="RoutingCalibrationKey"/> — assembly version is never a
/// component, so daily dev rebuilds do NOT trigger recalibration. Triggers are:
///
///   - <see cref="ToolUseCalibrationRequest.ForceRecalibrate"/> — user clicked retest
///   - The schema content changed → caller supplies a new <c>SchemaHash</c>
///   - A new (provider, model) tuple has no cached row yet
///   - A local artifact digest, runtime version, or quantization changed
///   - <see cref="IToolUseCalibrationCatalog.ClearAsync"/> was called (provider added/removed)
/// </summary>
public sealed class DefaultToolUseCalibrationService : IToolUseCalibrationService
{
    /// <summary>Calibration lane key. Distinguishes tool-use entries from other lanes in catalog snapshots.</summary>
    public const string LaneKey = "tool-use";

    private readonly IToolUseCalibrationCatalog _catalog;
    private readonly IToolUseValidator _validator;
    private readonly ILogger<DefaultToolUseCalibrationService> _logger;

    public DefaultToolUseCalibrationService(
        IToolUseCalibrationCatalog catalog,
        IToolUseValidator validator,
        ILogger<DefaultToolUseCalibrationService> logger)
    {
        _catalog = catalog;
        _validator = validator;
        _logger = logger;
    }

    public async Task<ToolUseCalibrationResult> CalibrateAsync(
        ToolUseCalibrationRequest request,
        CancellationToken ct = default)
    {
        var key = BuildKey(request);

        if (!request.ForceRecalibrate)
        {
            var cached = await _catalog.GetAsync(key, ct).ConfigureAwait(false);
            if (cached is not null && IsDefinitive(cached))
            {
                _logger.LogDebug(
                    "Tool-use calibration cache hit for {Provider}/{Model} (status={Status})",
                    request.ProviderId, request.ModelId, cached.Status);
                return cached;
            }
        }

        _logger.LogInformation(
            "Tool-use calibration running for {Provider}/{Model} (force={Force})",
            request.ProviderId, request.ModelId, request.ForceRecalibrate);

        var result = await _validator.RunAsync(request.ProviderId, request.ModelId, ct).ConfigureAwait(false);
        // Only conclusive model capability evidence is durable. Runtime outages,
        // caller/worker timeouts, and other transient "untested" outcomes must be
        // retried instead of becoming a permanent negative cache entry.
        if (IsDefinitive(result))
            await _catalog.SetAsync(key, result, ct).ConfigureAwait(false);
        return result;
    }

    public async Task<ToolUseCalibrationResult?> TryGetCachedAsync(
        ToolUseCalibrationRequest request,
        CancellationToken ct = default)
    {
        var cached = await _catalog.GetAsync(BuildKey(request), ct).ConfigureAwait(false);
        return cached is not null && IsDefinitive(cached) ? cached : null;
    }

    private static RoutingCalibrationKey BuildKey(ToolUseCalibrationRequest request) =>
        new(
            LaneKey: LaneKey,
            PromptHash: request.SchemaHash,
            ProviderId: request.ProviderId,
            ModelId: request.ModelId,
            HardwareKey: string.IsNullOrWhiteSpace(request.HardwareKey) ? "cloud" : request.HardwareKey,
            ModelDigest: request.ModelDigest,
            RuntimeVersion: request.RuntimeVersion,
            Quantization: request.Quantization);

    private static bool IsDefinitive(ToolUseCalibrationResult result)
        => result.Status.Equals("passed", StringComparison.OrdinalIgnoreCase)
           || result.Status.Equals("failed", StringComparison.OrdinalIgnoreCase)
           || result.Status.Equals("excluded", StringComparison.OrdinalIgnoreCase);
}
