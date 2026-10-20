using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting.LocalRuntime;

public sealed record OllamaWeight(string Name, string Digest, long Size);
public sealed record OllamaVisionArtifact(string Model, string Digest, JsonObject Manifest, IReadOnlyList<OllamaWeight> Weights);
public sealed record VisionProjector(string Repository, string Revision, string File, string Sha256, long Size)
{
    public string Url => $"https://huggingface.co/{Repository}/resolve/{Revision}/{string.Join('/', File.Split('/').Select(Uri.EscapeDataString))}";
}
public sealed record VisionRepairResult(bool Repaired, string Model, string Message,
    string? OriginalDigest = null, string? ArtifactDigest = null, VisionProjector? Projector = null, LocalVisionProbe? Proof = null);

/// <summary>Hosts own native storage, immutable backups and checked activation. Never used for a remote daemon.</summary>
public interface IOllamaVisionArtifactStore
{
    Task<OllamaVisionArtifact> ReadAsync(string model, string expectedDigest, CancellationToken ct);
    Task<Stream> OpenProjectorAsync(VisionProjector component, Func<Stream, CancellationToken, Task> download, CancellationToken ct);
    Task<string> StageAsync(OllamaVisionArtifact original, VisionProjector component, CancellationToken ct);
    Task<bool> ActivateAsync(OllamaVisionArtifact original, string candidate, VisionRepairResult verified, CancellationToken ct);
    Task RemoveCandidateAsync(string candidate, CancellationToken ct);
}

/// <summary>Public Hub metadata discovers candidates; complete installed weight hashes establish provenance.</summary>
public sealed partial class VisionProjectorDiscovery(IHttpClientFactory http)
{
    public async Task<VisionProjector> DiscoverAsync(OllamaVisionArtifact artifact, CancellationToken ct)
    {
        var queries = new[] { artifact.Model.Split('/').Last().Split(':')[0], Stem(artifact.Weights[0].Name) }.Distinct().ToArray();
        var repositories = new HashSet<string>(StringComparer.Ordinal);
        // hf.co imports carry an exact publisher hint. It is still verified against every weight.
        var tag = artifact.Model.Split(':')[0];
        if (tag.StartsWith("hf.co/", StringComparison.Ordinal)) repositories.Add(tag[6..]);
        foreach (var query in queries)
        {
            using var results = await ReadAsync("https://huggingface.co/api/models?search=" + Uri.EscapeDataString(query) + "&limit=12", ct);
            foreach (var item in results.RootElement.EnumerateArray())
                if (item.TryGetProperty("id", out var id) && Repo().IsMatch(id.GetString() ?? "")) repositories.Add(id.GetString()!);
        }
        var matches = new List<VisionProjector>();
        foreach (var repository in repositories.Order(StringComparer.Ordinal))
        {
            if (!Repo().IsMatch(repository)) continue;
            using var head = await ReadAsync($"https://huggingface.co/api/models/{repository}?blobs=true", ct);
            if (!head.RootElement.TryGetProperty("sha", out var sha) || !Hex(sha.GetString(), 40)) continue;
            var revision = sha.GetString()!;
            // Pin the tree before using any publisher file metadata.
            using var pinned = await ReadAsync($"https://huggingface.co/api/models/{repository}/revision/{revision}?blobs=true", ct);
            if (pinned.RootElement.GetProperty("sha").GetString() != revision) continue;
            var files = pinned.RootElement.GetProperty("siblings").EnumerateArray().ToArray();
            if (!artifact.Weights.All(w => files.Any(f => Hash(f) == w.Digest.Replace("sha256:", "") && Size(f) == w.Size))) continue;
            var projectors = files.Where(f => SafeFile(f.GetProperty("rfilename").GetString()!)
                && Path.GetFileName(f.GetProperty("rfilename").GetString()!).StartsWith("mmproj", StringComparison.OrdinalIgnoreCase)
                && Hex(Hash(f), 64) && Size(f) is > 0 and <= 2L * 1024 * 1024 * 1024).ToArray();
            var stems = artifact.Weights.Select(w => Stem(w.Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var exact = projectors.Where(f => stems.Contains(Stem(f.GetProperty("rfilename").GetString()!), StringComparer.OrdinalIgnoreCase)).ToArray();
            var eligible = exact.Length > 0 ? exact : projectors.Length == 1 ? projectors : [];
            // Precision alternatives can be compatible, but do not guess between different component contents.
            foreach (var file in eligible)
                matches.Add(new(repository, revision, file.GetProperty("rfilename").GetString()!, Hash(file)!, Size(file)));
        }
        var distinct = matches.DistinctBy(m => m.Sha256).ToArray();
        if (distinct.Length != 1) throw new InvalidOperationException(distinct.Length == 0
            ? "No public projector has verified provenance for all installed weights. The model was left unchanged."
            : "Multiple projectors match the installed weights. Compatibility is ambiguous; the model was left unchanged.");
        return distinct[0];
    }

    public static bool Hex(string? value, int count) => value?.Length == count && value.All(Uri.IsHexDigit);
    public static bool SafeFile(string value) => value.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
        && value.Length < 512 && value.Split('/').All(p => p.Length > 0 && p is not ("." or "..")
            && p.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'));
    internal static string Stem(string file) => Quant().Replace(Shard().Replace(Path.GetFileNameWithoutExtension(file), ""), "")
        .Replace("mmproj-", "", StringComparison.OrdinalIgnoreCase).Replace("mmproj_", "", StringComparison.OrdinalIgnoreCase);
    private static string? Hash(JsonElement f) => f.TryGetProperty("lfs", out var lfs) && lfs.TryGetProperty("sha256", out var hash) ? hash.GetString() : null;
    private static long Size(JsonElement f) => f.TryGetProperty("lfs", out var lfs) ? OllamaSetupClient.Number(lfs, "size") : 0;
    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9._-]*/[a-zA-Z0-9][a-zA-Z0-9._-]*$")] private static partial Regex Repo();
    [GeneratedRegex(@"-\d+-of-\d+$", RegexOptions.IgnoreCase)] private static partial Regex Shard();
    [GeneratedRegex(@"[-_.](?:(?:UD-)?(?:IQ\d_[A-Z0-9_]+|Q\d_[A-Z0-9_]+)|BF16|F16|F32)$", RegexOptions.IgnoreCase)] private static partial Regex Quant();
    private async Task<JsonDocument> ReadAsync(string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var client = http.CreateClient(); client.MaxResponseContentBufferSize = 8 * 1024 * 1024;
        using var response = await client.GetAsync(url, timeout.Token); response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
    }
}

/// <summary>Repairs only a verified missing component, under the machine lease, before the original inference resumes.</summary>
public sealed class OllamaVisionRepair(IHttpClientFactory http, IOllamaVisionArtifactStore store, ILocalLlmRuntimeManager runtime)
{
    public async Task<VisionRepairResult> RepairAsync(string endpoint, string model, CancellationToken ct)
    {
        endpoint = OllamaSetupClient.ValidateEndpoint(endpoint); model = OllamaSetupClient.NormalizeModel(model);
        if (!new Uri(endpoint).IsLoopback) throw new InvalidOperationException("Automatic component repair requires a local Ollama daemon.");
        var setup = new OllamaSetupClient(http);
        using var activity = runtime.TrackActivity(new("Ollama", model, LocalLlmWorkloadKind.Vision,
            LocalLlmRuntimeActivityKind.ModelRepair, "Preparing model image support", DateTimeOffset.UtcNow));
        var token = ct;
        var before = (await setup.InventoryAsync(endpoint, token)).Models.Single(m => m.Name == model);
        if (!before.IsOnDevice) throw new InvalidOperationException("The runtime did not confirm on-device execution. No repair was attempted.");
        if (before.Capabilities.Contains("vision")) return new(false, model, "The installed artifact already declares vision; no component was changed.", before.Digest, before.Digest);
        var original = await store.ReadAsync(model, before.Digest, token);
        if (original.Weights.Count == 0) throw new InvalidOperationException("The native model has no verifiable GGUF weights.");
        var component = await new VisionProjectorDiscovery(http).DiscoverAsync(original, token);
        using var projector = await store.OpenProjectorAsync(component, async (target, downloadCt) =>
        {
            using var client = http.CreateClient(); client.Timeout = Timeout.InfiniteTimeSpan;
            using var response = await client.GetAsync(component.Url, HttpCompletionOption.ResponseHeadersRead, downloadCt); response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != component.Size) throw new InvalidDataException("Projector download size changed.");
            using var source = await response.Content.ReadAsStreamAsync(downloadCt);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024]; long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, downloadCt)) != 0)
            {
                total += read; if (total > component.Size) throw new InvalidDataException("Projector exceeded its verified size.");
                hash.AppendData(buffer.AsSpan(0, read)); await target.WriteAsync(buffer.AsMemory(0, read), downloadCt);
            }
            if (total != component.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(component.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Projector checksum did not match its pinned publisher revision.");
        }, token);
        string? candidate = null;
        try
        {
            using (await runtime.AcquireAsync(new("Ollama", model, WorkloadKind: LocalLlmWorkloadKind.Vision), token))
            using (var client = http.CreateClient())
            {
                // Downloading/metadata never occupies inference capacity. Native mutations do.
                var current = (await setup.InventoryAsync(endpoint, token)).Models.SingleOrDefault(m => m.Name == model);
                if (current?.Digest != original.Digest) throw new InvalidOperationException("The original changed during discovery.");
                client.Timeout = Timeout.InfiniteTimeSpan;
                var blobUrl = endpoint + "/api/blobs/sha256:" + component.Sha256;
                using var head = await client.SendAsync(new(HttpMethod.Head, blobUrl), token);
                if (head.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    using var content = new StreamContent(projector); content.Headers.ContentLength = component.Size;
                    using var upload = await client.PostAsync(blobUrl, content, token); upload.EnsureSuccessStatusCode();
                }
                else head.EnsureSuccessStatusCode();
                candidate = await store.StageAsync(original, component, token);
            }
            using var session = await runtime.AcquireExclusiveActivityAsync(new("Ollama", candidate, WorkloadKind: LocalLlmWorkloadKind.Vision),
                new("Ollama", candidate, LocalLlmWorkloadKind.Vision, LocalLlmRuntimeActivityKind.ModelRepair, "Verifying model image support", DateTimeOffset.UtcNow), ct);
            using var active = session.Activate();
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct, session.PreemptionToken);
            token = cancel.Token;
            var proof = await setup.TestVisionAsync(endpoint, candidate, runtime, token);
            if (!proof.Passed) return new(false, model, proof.Message, original.Digest, Projector: component, Proof: proof);
            var result = new VisionRepairResult(true, model, "Installed the verified matching vision component. Two real image tests passed; continuing the original request.",
                original.Digest, proof.ArtifactDigest, component, proof);
            if (!await store.ActivateAsync(original, candidate, result, token))
                return result with { Repaired = false, Message = "The model changed or was deleted during repair. The candidate was not activated." };
            var after = (await setup.InventoryAsync(endpoint, token)).Models.SingleOrDefault(m => m.Name == model);
            if (after?.Digest != proof.ArtifactDigest || !after.Capabilities.Contains("vision"))
                throw new InvalidOperationException("The active model changed after activation; its current state must be inspected before continuing.");
            return result;
        }
        finally
        {
            if (candidate is not null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    using (await runtime.AcquireAsync(new("Ollama", model, WorkloadKind: LocalLlmWorkloadKind.Vision), cleanup.Token))
                        await store.RemoveCandidateAsync(candidate, cleanup.Token);
                }
                catch (OperationCanceledException) { /* The retained owned intent permits later inspection/cleanup without delaying foreground work. */ }
            }
        }
    }
}
