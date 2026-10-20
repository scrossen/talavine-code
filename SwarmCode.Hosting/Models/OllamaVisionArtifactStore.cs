using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SwarmCore.LlmRouting.LocalRuntime;

namespace SwarmCode.Hosting.Models;

public sealed class OllamaVisionStorageOptions
{
    public string? ModelsDirectory { get; set; }
}

/// <summary>Native Ollama v2 manifest adapter. Backups stay outside the model catalog.</summary>
public sealed class OllamaVisionArtifactStore(SwarmCodeDataPaths paths,
    Microsoft.Extensions.Options.IOptions<OllamaVisionStorageOptions> options) : IOllamaVisionArtifactStore
{
    private string Root => Path.GetFullPath(options.Value.ModelsDirectory
        ?? Environment.GetEnvironmentVariable("OLLAMA_MODELS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ollama", "models"));
    private string Repairs => Path.Combine(paths.RootDirectory, "model-repairs");
    private string ManifestPath(string model)
    {
        model = OllamaSetupClient.NormalizeModel(model);
        var colon = model.LastIndexOf(':'); var slash = model.LastIndexOf('/');
        var tag = colon > slash ? model[(colon + 1)..] : "latest";
        var name = colon > slash ? model[..colon] : model;
        var parts = name.Split('/');
        var relative = parts.Length switch
        {
            1 => new[] { "registry.ollama.ai", "library", parts[0], tag },
            2 => new[] { "registry.ollama.ai", parts[0], parts[1], tag },
            3 => new[] { parts[0], parts[1], parts[2], tag },
            _ => throw new InvalidDataException("Unsupported native model identity.")
        };
        if (relative.Any(p => p.Length == 0 || p is "." or ".." || p.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-'))))
            throw new InvalidDataException("Unsafe native model identity.");
        return Path.Combine([Root, "manifests", .. relative]);
    }
    private static string Digest(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    private static byte[] Bytes(JsonObject value) => Encoding.UTF8.GetBytes(value.ToJsonString());

    public Task<OllamaVisionArtifact> ReadAsync(string model, string expectedDigest, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var data = File.ReadAllBytes(ManifestPath(model));
        if (Digest(data) != expectedDigest) throw new InvalidOperationException("Local model storage does not match the running daemon's exact artifact.");
        var manifest = JsonNode.Parse(data)!.AsObject();
        if (manifest["schemaVersion"]?.GetValue<int>() != 2 || manifest["layers"] is not JsonArray layers)
            throw new InvalidDataException("The native manifest schema is unsupported.");
        if (layers.Any(l => l?["mediaType"]?.GetValue<string>() == "application/vnd.ollama.image.projector"))
            throw new InvalidOperationException("A projector is already installed; a component replacement requires separate diagnosis.");
        var weights = layers.Where(l => l?["mediaType"]?.GetValue<string>() == "application/vnd.ollama.image.model")
            .Select(l => new OllamaWeight(l!["from"]?.GetValue<string>() ?? "", l["digest"]!.GetValue<string>(), l["size"]!.GetValue<long>())).ToArray();
        if (weights.Any(w => !VisionProjectorDiscovery.SafeFile(w.Name) || !ValidDigest(w.Digest) || w.Size <= 0))
            throw new InvalidDataException("This import has no complete verifiable weight-file identities.");
        return new OllamaVisionArtifact(model, expectedDigest, manifest, weights);
    }, ct);

    public async Task<Stream> OpenProjectorAsync(VisionProjector component, Func<Stream, CancellationToken, Task> download, CancellationToken ct)
    {
        if (!VisionProjectorDiscovery.Hex(component.Sha256, 64) || component.Size is <= 0 or > 2L * 1024 * 1024 * 1024)
            throw new InvalidDataException("Invalid projector identity.");
        var directory = Path.Combine(Repairs, "components"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, component.Sha256 + ".gguf");
        var native = Path.Combine(Root, "blobs", "sha256-" + component.Sha256);
        if (!File.Exists(path) && File.Exists(native))
        {
            await VerifyAsync(native, component, ct).ConfigureAwait(false);
            return new FileStream(native, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        }
        if (!File.Exists(path))
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
            try
            {
                await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
                    await download(target, ct).ConfigureAwait(false);
                await VerifyAsync(temporary, component, ct).ConfigureAwait(false);
                File.Move(temporary, path, overwrite: false);
            }
            finally { File.Delete(temporary); }
        }
        await VerifyAsync(path, component, ct).ConfigureAwait(false);
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
    }
    private static async Task VerifyAsync(string path, VisionProjector component, CancellationToken ct)
    {
        await using var source = File.OpenRead(path);
        if (source.Length != component.Size || !Convert.ToHexString(await SHA256.HashDataAsync(source, ct)).Equals(component.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The cached projector does not match its verified checksum.");
    }

    public Task<string> StageAsync(OllamaVisionArtifact original, VisionProjector component, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        if (Digest(File.ReadAllBytes(ManifestPath(original.Model))) != original.Digest)
            throw new InvalidOperationException("The model changed before candidate creation.");
        var blob = Path.Combine(Root, "blobs", "sha256-" + component.Sha256);
        if (!File.Exists(blob) || new FileInfo(blob).Length != component.Size) throw new InvalidDataException("The runtime did not store the projector blob.");
        var id = Guid.NewGuid().ToString("N"); var candidate = "talavine-vision-" + id + ":candidate";
        var directory = Path.Combine(Repairs, id); Directory.CreateDirectory(directory);
        var baseline = File.ReadAllBytes(ManifestPath(original.Model));
        if (Digest(baseline) != original.Digest) throw new InvalidOperationException("The model changed before backup.");
        File.WriteAllBytes(Path.Combine(directory, "original-manifest.json"), baseline);
        // Cloning the native manifest preserves config, templates, parser, parameters and every language shard.
        // It also avoids importers that reject an embedded template before applying the caller's override.
        var updated = original.Manifest.DeepClone().AsObject();
        updated["layers"]!.AsArray().Add(new JsonObject { ["mediaType"] = "application/vnd.ollama.image.projector",
            ["digest"] = "sha256:" + component.Sha256, ["size"] = component.Size, ["from"] = component.File });
        var payload = Bytes(updated);
        File.WriteAllText(Path.Combine(directory, "intent.json"), JsonSerializer.Serialize(new { original.Model, original.Digest, candidate, candidateDigest = Digest(payload), component }));
        var destination = ManifestPath(candidate); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using (var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(payload); file.Flush(true); }
        return candidate;
    }, ct);

    public Task<bool> ActivateAsync(OllamaVisionArtifact original, string candidate, VisionRepairResult verified, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var directory = Operation(candidate); var baseline = File.ReadAllBytes(Path.Combine(directory, "original-manifest.json"));
        if (Digest(baseline) != original.Digest || !verified.Repaired || verified.OriginalDigest != original.Digest
            || verified.Proof is not { Passed: true, ImagesChecked: 2 } || verified.Proof.Model != candidate)
            throw new InvalidDataException("Activation needs the exact preserved original and a verified two-image candidate.");
        var updated = File.ReadAllBytes(ManifestPath(candidate));
        if (Digest(updated) != verified.ArtifactDigest || verified.Proof.ArtifactDigest != verified.ArtifactDigest)
            throw new InvalidDataException("The candidate changed after verification.");
        var clone = JsonNode.Parse(updated)!.AsObject(); var layers = clone["layers"]!.AsArray();
        var projector = layers.Last()!;
        if (projector["mediaType"]?.GetValue<string>() != "application/vnd.ollama.image.projector"
            || projector["digest"]?.GetValue<string>() != "sha256:" + verified.Projector?.Sha256)
            throw new InvalidDataException("The verified component is not the candidate's only added layer.");
        layers.RemoveAt(layers.Count - 1);
        if (!JsonNode.DeepEquals(clone, JsonNode.Parse(baseline))) throw new InvalidDataException("The candidate changed original model settings or weights.");
        var destination = ManifestPath(original.Model);
        // Same-profile processes share this native writer lock. Fresh baseline and existence checks
        // reject a competing edit or deletion; no API copy can silently recreate a deleted tag.
        Directory.CreateDirectory(Path.Combine(Repairs, "locks"));
        using var writer = new FileStream(Path.Combine(Repairs, "locks", Digest(Encoding.UTF8.GetBytes(original.Model)) + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (!File.Exists(destination) || Digest(File.ReadAllBytes(destination)) != original.Digest) return false;
        File.WriteAllText(Path.Combine(directory, "verified.json"), JsonSerializer.Serialize(verified));
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(updated); file.Flush(true); }
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(destination) || Digest(File.ReadAllBytes(destination)) != original.Digest) return false;
            File.Move(temporary, destination, overwrite: true);
            File.WriteAllText(Path.Combine(directory, "activated.json"), JsonSerializer.Serialize(new { original.Model, verified.ArtifactDigest, ActivatedUtc = DateTimeOffset.UtcNow }));
            return true;
        }
        finally { File.Delete(temporary); }
    }, ct);

    private string Operation(string candidate)
    {
        var id = candidate.Replace("talavine-vision-", "", StringComparison.Ordinal).Replace(":candidate", "", StringComparison.Ordinal);
        if (!Guid.TryParseExact(id, "N", out _) || candidate != "talavine-vision-" + id + ":candidate") throw new InvalidDataException("Unowned repair candidate.");
        var directory = Path.Combine(Repairs, id);
        if (!File.Exists(Path.Combine(directory, "intent.json"))) throw new InvalidDataException("Missing repair intent.");
        return directory;
    }
    public Task RemoveCandidateAsync(string candidate, CancellationToken ct) => Task.Run(() =>
    {
        var directory = Operation(candidate);
        using var intent = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "intent.json")));
        var path = ManifestPath(candidate);
        if (File.Exists(path) && Digest(File.ReadAllBytes(path)) == intent.RootElement.GetProperty("candidateDigest").GetString()) File.Delete(path);
    }, ct);
    private static bool ValidDigest(string value) => value.StartsWith("sha256:", StringComparison.Ordinal) && VisionProjectorDiscovery.Hex(value[7..], 64);
}
