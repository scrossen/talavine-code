namespace SwarmCore.Chat;

/// <summary>
/// An image a user attaches to any chat surface — a screenshot of a problem, a UI
/// mockup, a sketch, a page of a spec. Host-agnostic so every app (Vibes,
/// Talavine, CRM) can pass the same shape into <see cref="IVisionDescriptionService"/>.
/// </summary>
public sealed record ChatImageAttachment
{
    public required string FileName { get; init; }

    /// <summary>MIME type, e.g. "image/png".</summary>
    public required string MediaType { get; init; }

    /// <summary>Raw base64 image bytes (no <c>data:</c> URL prefix).</summary>
    public required string Base64Data { get; init; }
}
