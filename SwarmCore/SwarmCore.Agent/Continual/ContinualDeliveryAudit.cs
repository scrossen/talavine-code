namespace SwarmCore.Agent.Continual;

/// <summary>Structured assessment of completion, shared by host acceptance gates.</summary>
public sealed class ContinualDeliveryAudit
{
    public IReadOnlyList<string> RequiredCapabilityReferences { get; set; } = [];
    public required string Verdict { get; set; }
    public string? Reason { get; set; }
    public IReadOnlyList<string> EvidenceReferences { get; set; } = [];
    /// <summary>Completed tool outcomes cited as observations, never successful action support.</summary>
    public IReadOnlyList<string> ObservedReceiptReferences { get; set; } = [];
    /// <summary>The required durable host state for a delivered action objective.</summary>
    public string? HostDisposition { get; set; }
}
