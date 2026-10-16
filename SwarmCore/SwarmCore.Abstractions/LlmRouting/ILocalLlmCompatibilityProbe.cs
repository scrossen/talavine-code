using System.Text.Json.Serialization;

namespace SwarmCore.Abstractions.LlmRouting;

public sealed class LocalLlmCompatibilityProbeRequest
{
    public required string Provider { get; set; }
    public required string Model { get; set; }
    public string? Endpoint { get; set; }
    public string Attempt { get; set; } = "json-contract";
    public int TimeoutSeconds { get; set; } = 30;
    /// <summary>
    /// Maximum time the background probe may wait for exclusive access to the
    /// local runtime. Admission expiry is a deferred scheduling outcome, not
    /// compatibility evidence for the model.
    /// </summary>
    public TimeSpan? RuntimeAdmissionTimeout { get; set; } = TimeSpan.FromMinutes(3);
    public string KeepAlive { get; set; } = "30m";
    public string ExpectedJsonPrompt { get; set; } = "Return {\"ok\":true,\"kind\":\"json-compatibility\"}.";
    public string? ExpectedArtifactDigest { get; set; }
    public LlmGenerationParameters GenerationParameters { get; set; } = new()
    {
        MaxOutputTokens = 256,
        Temperature = 0,
        Reasoning = LlmReasoningMode.Disabled,
        StructuredOutput = LlmStructuredOutputMode.JsonObject,
        Stream = false,
        KeepAlive = TimeSpan.FromMinutes(30),
    };
    [JsonIgnore]
    public Dictionary<string, object> ExecutionSettings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class LocalLlmCompatibilityProbeResult
{
    public required string Provider { get; set; }
    public required string Model { get; set; }
    public string Status { get; set; } = "pending";
    public bool Compatible { get; set; }
    public string Attempt { get; set; } = "";
    public string? Issue { get; set; }
    public string? ResponsePreview { get; set; }
    public int OutputTokens { get; set; }
    public double LatencyMs { get; set; }
    public string ExecutionContractId { get; set; } = "";
    public List<LlmExecutionParameterDiagnostic> ParameterDiagnostics { get; set; } = [];
    [JsonIgnore]
    public Dictionary<string, object> ExecutionSettings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public interface ILocalLlmCompatibilityProbe
{
    Task<LocalLlmCompatibilityProbeResult> ProbeJsonContractAsync(
        LocalLlmCompatibilityProbeRequest request,
        CancellationToken ct = default);
}
