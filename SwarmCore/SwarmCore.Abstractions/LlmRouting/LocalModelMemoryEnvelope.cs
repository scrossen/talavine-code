namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// The memory a local model artifact is admitted with: its weights plus
/// headroom for the KV cache, compute buffers, and runtime. Route admission
/// requires the serving device to hold this envelope, so a runtime setting that
/// grows a loaded model's memory (a larger context window) must stay inside it.
/// </summary>
public static class LocalModelMemoryEnvelope
{
    public const long BytesPerGiB = 1024L * 1024 * 1024;

    /// <summary>
    /// Compute graphs and runtime overhead held back from the KV-cache budget.
    /// </summary>
    public const long RuntimeReserveBytes = 2 * BytesPerGiB;

    /// <summary>
    /// Whole GiB a model needs: at least 8, otherwise half again its artifact
    /// plus 4. Zero when the artifact size is unknown.
    /// </summary>
    public static int RequiredMemoryGb(long artifactBytes)
    {
        if (artifactBytes <= 0)
        {
            return 0;
        }

        var artifactGb = Math.Max(1, (int)Math.Ceiling(artifactBytes / (double)BytesPerGiB));
        return Math.Max(8, (int)Math.Ceiling(artifactGb * 1.5) + 4);
    }

    /// <summary>
    /// Bytes of the envelope left for the KV cache once the weights and the
    /// runtime reserve are counted. Zero when the artifact size is unknown.
    /// </summary>
    public static long KvCacheBudgetBytes(long artifactBytes)
        => artifactBytes <= 0
            ? 0
            : Math.Max(0, RequiredMemoryGb(artifactBytes) * BytesPerGiB - artifactBytes - RuntimeReserveBytes);
}
