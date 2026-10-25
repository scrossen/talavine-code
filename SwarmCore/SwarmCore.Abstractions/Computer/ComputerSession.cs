using System.Text.Json.Serialization;

namespace SwarmCore.Abstractions.Computer;

public sealed record ComputerPoint(double X, double Y);
public sealed record ComputerAction(string Type)
{
    public double? X { get; init; }
    public double? Y { get; init; }
    public string? Button { get; init; }
    public string? Text { get; init; }
    public IReadOnlyList<string> Keys { get; init; } = [];
    public IReadOnlyList<ComputerPoint> Path { get; init; } = [];
    public double ScrollX { get; init; }
    public double ScrollY { get; init; }
    public int WaitMilliseconds { get; init; } = 250;
    public string? Url { get; init; }
}
public sealed record ComputerObservation(string SessionId, string SurfaceId, string ObservationId,
    string Url, DateTimeOffset CapturedAt, int Width, int Height, double DeviceScaleFactor) : SwarmCore.Abstractions.LlmRouting.IToolResultImages,
    SwarmCore.Abstractions.Context.IContextPackEvidence
{
    public string? Title { get; init; }
    public string? SemanticPageToken { get; init; }
    public string Trust { get; init; } = "untrusted-browser-evidence";
    [JsonIgnore] public string? PngBase64 { get; init; }
    IReadOnlyList<SwarmCore.Abstractions.LlmRouting.ImagePart> SwarmCore.Abstractions.LlmRouting.IToolResultImages.Images
        => PngBase64 is { } png ? [new(png, "image/png")] : [];
    SwarmCore.Abstractions.Context.ContextPack? SwarmCore.Abstractions.Context.IContextPackEvidence.WorkingContextSourcePack => null;
    public override string ToString() => $"ComputerObservation({SessionId}, {SurfaceId}, {ObservationId}, {Width}x{Height})";
}
public sealed record ComputerActionBatch(string SessionId, string SurfaceId, string ObservationId,
    IReadOnlyList<ComputerAction> Actions);
public sealed record ComputerActionResult(bool Succeeded, int CompletedActions, ComputerObservation? Observation,
    string? Error = null) : SwarmCore.Abstractions.LlmRouting.IToolResultImages, SwarmCore.Abstractions.Context.IContextPackEvidence,
    SwarmCore.Abstractions.Tools.IToolDispatchEvidence
{
    public string Mode { get; init; } = "browser";
    public string? BeforeObservationId { get; init; }
    public bool OutcomeUncertain { get; init; }
    [JsonIgnore] public bool NoPhysicalAction { get; init; }
    IReadOnlyList<SwarmCore.Abstractions.LlmRouting.ImagePart> SwarmCore.Abstractions.LlmRouting.IToolResultImages.Images
        => Observation is SwarmCore.Abstractions.LlmRouting.IToolResultImages images ? images.Images : [];
    SwarmCore.Abstractions.Context.ContextPack? SwarmCore.Abstractions.Context.IContextPackEvidence.WorkingContextSourcePack => null;
}
public interface IComputerSession
{
    Task<ComputerObservation> ObserveAsync(string? url = null, CancellationToken ct = default);
    Task<ComputerActionResult> ExecuteBatchAsync(ComputerActionBatch batch, CancellationToken ct = default);
}
