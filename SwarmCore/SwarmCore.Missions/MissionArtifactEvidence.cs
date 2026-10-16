using System.Security.Cryptography;
using SwarmCore.Agent.Continual;

namespace SwarmCore.Missions;

/// <summary>Content evidence for artifacts modified through the production file writer.
/// Read-only inputs such as continuously changing logs are not silently made immutable artifacts.</summary>
public static class MissionArtifactEvidence
{
    public static async Task<IReadOnlyDictionary<string, string>?> CaptureAsync(IEnumerable<string> paths, CancellationToken ct)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in paths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var physical = SandboxPathResolver.ResolvePhysicalPath(path);
            if (!string.Equals(path, physical, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new IOException("An observed artifact path now resolves to a different physical target.");
            try
            {
                await using var stream = File.OpenRead(path);
                hashes[path] = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            { hashes[path] = "missing"; }
        }
        return hashes.Count == 0 ? null : hashes;
    }

    public static async Task<string?> ValidateAsync(ContinualObjectiveSnapshot objective, CancellationToken ct)
    {
        var latest = objective.Receipts.Where(r => r.WasDispatched
                && (r.ArtifactHashes is not null || r.ArtifactEvidenceError is not null))
            .MaxBy(r => r.Sequence);
        if (latest is null) return null; // Legacy receipts retain the conservative sequence gate.
        if (latest.ArtifactEvidenceError is not null) return "Host artifact evidence is unavailable; inspect the affected files and verify again.";
        try
        {
            var actual = await CaptureAsync(latest.ArtifactHashes!.Keys, ct);
            return actual is not null && latest.ArtifactHashes.All(p => actual.TryGetValue(p.Key, out var hash) && hash == p.Value)
                ? null : "Recorded artifact contents changed after verification. Inspect and rerun affected checks against the current files.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "Recorded artifacts could not be inspected at completion; recover access and verify again.";
        }
    }
}
