using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SwarmCore.Agent.ToolUseEngine;

using SwarmCore.Agent.Continual;

namespace SwarmCore.Missions;

/// <summary>Immutable protected evidence storage; the host supplies its owning partition root and protector.</summary>
public sealed class ProtectedMissionEvidenceArchive(string profileRoot, IToolJournalProtector protector) : IMissionEvidenceArchive
{
    public async Task RestoreArtifactAsync(Guid objectiveId, string digest, Stream destination, CancellationToken ct)
    {
        ValidateDigest(digest);
        var path = Path.Combine(Root(objectiveId), digest + ".artifact");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var reader = File.OpenText(path);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            var clear = protector.Unprotect(line);
            if (string.IsNullOrEmpty(clear)) throw new InvalidDataException("Evidence could not be decrypted.");
            var bytes = Convert.FromBase64String(clear);
            hash.AppendData(bytes);
            await destination.WriteAsync(bytes, ct);
        }
        if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(digest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Archived artifact digest mismatch; restored bytes are not verified.");
    }

    public async Task<string> PreserveAsync(ContinualToolReceipt receipt, CancellationToken ct)
    {
        var root = Root(receipt.ObjectiveId);
        Directory.CreateDirectory(root);
        foreach (var artifact in receipt.ArtifactHashes ?? new Dictionary<string, string>())
        {
            if (artifact.Value == "missing") continue;
            ValidateDigest(artifact.Value);
            var target = Path.Combine(root, artifact.Value + ".artifact");
            if (File.Exists(target)) continue;
            var temporary = Path.Combine(root, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await using var input = File.OpenRead(artifact.Key);
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var output = new StreamWriter(new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    65536, FileOptions.Asynchronous | FileOptions.WriteThrough), Encoding.UTF8))
                {
                    var buffer = new byte[65536]; int count;
                    while ((count = await input.ReadAsync(buffer, ct)) > 0)
                    {
                        digest.AppendData(buffer, 0, count);
                        await output.WriteLineAsync(protector.Protect(Convert.ToBase64String(buffer, 0, count)).AsMemory(), ct);
                    }
                    await output.FlushAsync(ct);
                }
                if (!Convert.ToHexString(digest.GetHashAndReset()).Equals(artifact.Value, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Artifact changed during preservation; rerun verification.");
                File.Move(temporary, target, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        var json = JsonSerializer.Serialize(receipt);
        var reference = MissionVerification.Digest(json) + ".receipt";
        var path = Path.Combine(root, reference);
        if (!File.Exists(path))
        {
            var temporary = Path.Combine(root, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await File.WriteAllTextAsync(temporary, protector.Protect(json), ct);
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return $"mission-evidence/{receipt.ObjectiveId:N}/{reference}";
    }

    private string Root(Guid objectiveId) => Path.Combine(Path.GetFullPath(profileRoot), "mission-evidence", objectiveId.ToString("N"));
    private static void ValidateDigest(string digest)
    {
        if (digest.Length != 64 || digest.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("Invalid artifact digest.");
    }
}
