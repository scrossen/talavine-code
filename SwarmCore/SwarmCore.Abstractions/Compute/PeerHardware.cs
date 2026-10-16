namespace SwarmCore.Abstractions.Compute;

/// <summary>Path- and credential-free physical inventory. Null means unmeasured, never zero capacity.</summary>
public sealed record PeerHardware
{
    public string? CpuName { get; init; }
    public int? LogicalProcessors { get; init; }
    public long? TotalMemoryBytes { get; init; }
    public IReadOnlyList<PeerGpu> Gpus { get; init; } = [];
    public long MeasuredUtcMs { get; init; }
}

/// <summary>Unified memory is system RAM, not dedicated VRAM; do not add the two.</summary>
public sealed record PeerGpu(string Name, long? DedicatedMemoryBytes = null, bool UnifiedMemory = false);
