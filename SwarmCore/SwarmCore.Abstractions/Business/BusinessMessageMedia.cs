namespace SwarmCore.Agent.BusinessDispatch;

/// <summary>Frozen content identity, not a filesystem path, remote URL or authority grant.
/// The host resolves the exact tenant-owned resource and checks its bytes before use.</summary>
public sealed record BusinessMessageMedia(string ResourceId, string FileName, string ContentType,
    long ByteCount, string Sha256, string Disposition = "attachment", string? ContentId = null);
