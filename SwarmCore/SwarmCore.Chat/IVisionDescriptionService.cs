namespace SwarmCore.Chat;

using SwarmCore.Abstractions.LlmRouting;

/// <summary>Why an image description did not contain analyzed visual content.</summary>
public enum VisionDescriptionFailureKind
{
    None = 0,
    NoUsableRoute = 1,
    RouteFailed = 2,
    EmptyResponse = 3,
}

/// <summary>
/// Typed vision outcome so a host can distinguish useful visual evidence from
/// a graceful failure and present an actionable recovery path.
/// </summary>
public sealed record VisionDescriptionResult(
    string Description,
    bool WasAnalyzed,
    VisionDescriptionFailureKind FailureKind = VisionDescriptionFailureKind.None)
{
    public static VisionDescriptionResult Success(string description) =>
        new(description, WasAnalyzed: true);

    public static VisionDescriptionResult Unavailable(
        string description,
        VisionDescriptionFailureKind failureKind) =>
        new(description, WasAnalyzed: false, failureKind);
}

/// <summary>
/// Turns one attached image into a text description an ordinary (non-vision) LLM
/// call can consume — the shared building block behind every "attach a screenshot"
/// feature. A failed or empty vision call degrades to a placeholder string rather
/// than throwing, so a flaky vision model never aborts the caller's larger request.
/// </summary>
public interface IVisionDescriptionService
{
    /// <summary>
    /// Describe one image via the router's vision lane. Never throws for a vision
    /// failure (HTTP timeout, missing vision model, empty response) — only for a
    /// genuine caller cancellation (<paramref name="ct"/> already signaled).
    /// </summary>
    /// <param name="functionType">Router function type used for model selection.</param>
    /// <param name="image">The attached image.</param>
    /// <param name="instructions">
    /// What to look for/describe. Defaults to a general-purpose "describe this for
    /// an app/assistant that can't see it" prompt when omitted.
    /// </param>
    Task<string> DescribeAsync(
        string functionType,
        ChatImageAttachment image,
        string? instructions = null,
        CancellationToken ct = default);

    /// <summary>
    /// Describe one image while preserving whether pixels were actually read.
    /// The default keeps existing host implementations source-compatible;
    /// production implementations should override it to classify route failure.
    /// </summary>
    async Task<VisionDescriptionResult> DescribeWithStatusAsync(
        string functionType,
        ChatImageAttachment image,
        string? instructions = null,
        CancellationToken ct = default)
    {
        var description = await DescribeAsync(functionType, image, instructions, ct).ConfigureAwait(false);
        return VisionDescriptionResult.Success(description);
    }

    /// <summary>
    /// Describe an image with an explicit routing contract. Interactive chat
    /// uses this overload so a conversation-pinned model also owns image
    /// understanding instead of silently invoking the automatic vision lane.
    /// Implementations that do not own a router remain source-compatible and
    /// may delegate to the ordinary description path.
    /// </summary>
    Task<VisionDescriptionResult> DescribeWithStatusAsync(
        string functionType,
        ChatImageAttachment image,
        string? instructions,
        LlmRouterOptions? options,
        CancellationToken ct = default)
        => DescribeWithStatusAsync(functionType, image, instructions, ct);
}
