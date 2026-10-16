using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.Chat;

/// <summary>Default <see cref="IVisionDescriptionService"/> over <see cref="ILlmRouter"/>.</summary>
public sealed class VisionDescriptionService : IVisionDescriptionService
{
    private const string DefaultInstructions = """
Describe this image precisely and usefully for an assistant that cannot see it.
Transcribe any visible text, error messages, field names, labels, column headers,
statuses, options, and numbers; describe the layout, tables, and controls; and call
out anything that looks broken or annotated (arrows, circles, highlights, handwritten
notes). Reply with the description only, no preamble.
""";

    private readonly ILlmRouter _router;
    private readonly ILogger<VisionDescriptionService> _logger;

    public VisionDescriptionService(ILlmRouter router, ILogger<VisionDescriptionService>? logger = null)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _logger = logger ?? NullLogger<VisionDescriptionService>.Instance;
    }

    public async Task<string> DescribeAsync(
        string functionType, ChatImageAttachment image, string? instructions = null, CancellationToken ct = default)
        => (await DescribeWithStatusAsync(functionType, image, instructions, ct).ConfigureAwait(false)).Description;

    public async Task<VisionDescriptionResult> DescribeWithStatusAsync(
        string functionType, ChatImageAttachment image, string? instructions = null, CancellationToken ct = default)
        => await DescribeWithStatusAsync(functionType, image, instructions, options: null, ct)
            .ConfigureAwait(false);

    public async Task<VisionDescriptionResult> DescribeWithStatusAsync(
        string functionType,
        ChatImageAttachment image,
        string? instructions,
        LlmRouterOptions? options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ct.ThrowIfCancellationRequested();

        var optionsToken = options?.RequestCancellationToken ?? CancellationToken.None;
        using var linkedCancellation = ct.CanBeCanceled
                                       && optionsToken.CanBeCanceled
                                       && ct != optionsToken
            ? CancellationTokenSource.CreateLinkedTokenSource(ct, optionsToken)
            : null;
        var requestCancellationToken = linkedCancellation?.Token
                                       ?? (ct.CanBeCanceled ? ct : optionsToken);

        try
        {
            var routingOptions = options is null
                ? new LlmRouterOptions
                {
                    Interactivity = LlmInteractivity.UserWaiting,
                    RequestCancellationToken = requestCancellationToken,
                }
                : CloneForVision(options, requestCancellationToken);
            var description = await _router.GenerateVisionResponseAsync(
                functionType,
                instructions ?? DefaultInstructions,
                image.Base64Data,
                routingOptions).ConfigureAwait(false);

            return string.IsNullOrWhiteSpace(description)
                ? VisionDescriptionResult.Unavailable(
                    "(this image could not be analyzed because the vision route returned no description)",
                    VisionDescriptionFailureKind.EmptyResponse)
                : VisionDescriptionResult.Success(description.Trim());
        }
        // HTTP-client timeouts surface as TaskCanceledException (an OperationCanceledException)
        // even though the caller's token was never signaled — only a real cancel rethrows.
        catch (OperationCanceledException) when (requestCancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vision description failed for {FileName}", image.FileName);
            var failureKind = IsNoUsableVisionRoute(ex)
                ? VisionDescriptionFailureKind.NoUsableRoute
                : VisionDescriptionFailureKind.RouteFailed;
            var reason = failureKind == VisionDescriptionFailureKind.NoUsableRoute
                ? "no usable vision-capable route is configured"
                : "automatic vision routing and its fallback did not complete";
            return VisionDescriptionResult.Unavailable(
                $"(this image could not be analyzed because {reason}; rely only on the surrounding text)",
                failureKind);
        }
    }

    private static LlmRouterOptions CloneForVision(
        LlmRouterOptions source,
        CancellationToken requestCancellationToken) => new()
    {
        OverrideProvider = source.OverrideProvider,
        OverrideModel = source.OverrideModel,
        OverrideProvenance = source.OverrideProvenance,
        PrivacyClass = source.PrivacyClass,
        Interactivity = source.Interactivity,
        Workload = source.Workload,
        TaskDemand = source.TaskDemand,
        Urgency = source.Urgency,
        SessionId = source.SessionId,
        TurnId = source.TurnId,
        SourceType = source.SourceType,
        SourceId = source.SourceId,
        Temperature = source.Temperature,
        MaxTokens = source.MaxTokens,
        DisableFallback = source.DisableFallback,
        AllowAnyProviderFallback = source.AllowAnyProviderFallback,
        RequiresVision = true,
        RequestCancellationToken = requestCancellationToken,
        ProviderExecutionStartCallback = source.ProviderExecutionStartCallback,
        ExecutionSettings = source.ExecutionSettings,
    };

    private static bool IsNoUsableVisionRoute(Exception exception)
    {
        var diagnostic = exception.ToString();
        return diagnostic.Contains("no vision", StringComparison.OrdinalIgnoreCase)
               || diagnostic.Contains("not vision-capable", StringComparison.OrdinalIgnoreCase)
               || diagnostic.Contains("does not support vision", StringComparison.OrdinalIgnoreCase)
               || diagnostic.Contains("no usable", StringComparison.OrdinalIgnoreCase)
               || diagnostic.Contains("no fallback route", StringComparison.OrdinalIgnoreCase)
               || diagnostic.Contains("no current function-scoped fallback proof", StringComparison.OrdinalIgnoreCase);
    }
}
