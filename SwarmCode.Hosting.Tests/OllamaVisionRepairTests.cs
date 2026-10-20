using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.LocalRuntime;
using SwarmCode.Hosting.Models;

namespace SwarmCode.Hosting.Tests;

public sealed class OllamaVisionRepairTests
{
    [Fact]
    public async Task DiscoveryDownloadInstallAndImageProofPreserveEveryOriginalSetting()
    {
        using var f = new Fixture();
        var result = await f.Repair();
        Assert.True(result.Repaired);
        Assert.Equal(2, result.Proof!.ImagesChecked);
        Assert.Equal(f.ComponentHash, result.Projector!.Sha256);
        Assert.Equal(result.ArtifactDigest, Fixture.Hash(File.ReadAllBytes(f.ModelPath)));
        var updated = JsonNode.Parse(File.ReadAllText(f.ModelPath))!; var layers = updated["layers"]!.AsArray();
        layers.RemoveAt(layers.Count - 1);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(f.Original), updated));
        Assert.Contains(Directory.GetFiles(Path.Combine(f.Profile, "model-repairs"), "original-manifest.json", SearchOption.AllDirectories), p => File.ReadAllText(p) == f.Original);
        Assert.DoesNotContain(Directory.GetFiles(Path.Combine(f.Models, "manifests"), "*", SearchOption.AllDirectories), p => p.Contains("talavine-vision-"));
        Assert.Equal(1, f.Http.Downloads);
        Assert.Equal(0, f.Runtime.Active);
    }

    [Theory]
    [InlineData("wrong-weight")]
    [InlineData("missing-shard")]
    [InlineData("cloud-alias")]
    [InlineData("ambiguous-projector")]
    [InlineData("bad-download")]
    [InlineData("truncated-download")]
    [InlineData("constant-answer")]
    public async Task UnprovenCandidateNeverReplacesTheOriginal(string failure)
    {
        using var f = new Fixture(); f.Http.Failure = failure;
        try { Assert.False((await f.Repair()).Repaired); }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or HttpRequestException) { }
        Assert.Equal(f.Original, File.ReadAllText(f.ModelPath));
        if (failure is "wrong-weight" or "missing-shard" or "ambiguous-projector" or "cloud-alias") Assert.Equal(0, f.Http.Downloads);
        Assert.Equal(0, f.Runtime.Active);
    }

    [Fact]
    public async Task CancelledDownloadLeavesNoPartialComponentOrChangedModel()
    {
        using var f = new Fixture(); using var cancel = new CancellationTokenSource();
        f.Http.OnDownload = cancel.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Repair(cancel.Token));
        Assert.Equal(f.Original, File.ReadAllText(f.ModelPath));
        Assert.Empty(Directory.GetFiles(f.Profile, "*.partial", SearchOption.AllDirectories));
        Assert.Equal(0, f.Runtime.Active);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompetingEditOrDeletionWinsOverAWaitingRepair(bool delete)
    {
        using var f = new Fixture();
        f.Http.AfterImages = () => { if (delete) File.Delete(f.ModelPath); else File.WriteAllText(f.ModelPath, "{\"userEdit\":true}"); };
        var result = await f.Repair();
        Assert.False(result.Repaired);
        if (delete) Assert.False(File.Exists(f.ModelPath)); else Assert.Equal("{\"userEdit\":true}", File.ReadAllText(f.ModelPath));
    }

    [Fact]
    public async Task ChangedCandidateCannotBeActivatedWithAnEarlierPassingReceipt()
    {
        using var f = new Fixture();
        var artifact = await f.Store.ReadAsync(Fixture.Model, Fixture.Hash(Encoding.UTF8.GetBytes(f.Original)), default);
        File.WriteAllBytes(Path.Combine(f.Models, "blobs", "sha256-" + f.ComponentHash), Fixture.Component);
        var component = new VisionProjector("publisher/example", new string('1', 40), "mmproj-Example-VL-F16.gguf", f.ComponentHash, Fixture.Component.Length);
        var candidate = await f.Store.StageAsync(artifact, component, default);
        var path = Path.Combine(f.Models, "manifests/registry.ollama.ai/library", candidate.Split(':')[0], "candidate");
        var digest = Fixture.Hash(File.ReadAllBytes(path)); File.AppendAllText(path, " ");
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Store.ActivateAsync(artifact, candidate,
            new(true, Fixture.Model, "verified", artifact.Digest, digest, component, new(true, candidate, digest, 2, [], "pass")), default));
        Assert.Equal(f.Original, File.ReadAllText(f.ModelPath));
    }

    private sealed class Fixture : IDisposable
    {
        public const string Model = "example-vl:quant";
        public static readonly byte[] Component = Encoding.UTF8.GetBytes("verified synthetic projector bytes");
        public readonly string Root = Path.Combine(Path.GetTempPath(), "vision-repair-tests-" + Guid.NewGuid().ToString("N"));
        public string Models => Path.Combine(Root, "models");
        public string Profile => Path.Combine(Root, "profile");
        public string ModelPath => Path.Combine(Models, "manifests/registry.ollama.ai/library/example-vl/quant");
        public string Original { get; }
        public string ComponentHash => Hash(Component);
        public OllamaVisionArtifactStore Store { get; }
        public Protocol Http { get; }
        public CountingRuntime Runtime { get; } = new();
        public Fixture()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!); Directory.CreateDirectory(Path.Combine(Models, "blobs"));
            Original = new JsonObject { ["schemaVersion"] = 2, ["config"] = new JsonObject { ["digest"] = "config-preserved" },
                ["layers"] = new JsonArray(new JsonObject { ["mediaType"] = "application/vnd.ollama.image.model", ["from"] = "Example-VL-Q4_K_M-00001-of-00002.gguf", ["digest"] = "sha256:" + new string('a', 64), ["size"] = 99 },
                    new JsonObject { ["mediaType"] = "application/vnd.ollama.image.model", ["from"] = "Example-VL-Q4_K_M-00002-of-00002.gguf", ["digest"] = "sha256:" + new string('d', 64), ["size"] = 77 },
                    new JsonObject { ["mediaType"] = "application/vnd.ollama.image.params", ["digest"] = "keep-custom-settings", ["size"] = 2 }) }.ToJsonString();
            File.WriteAllText(ModelPath, Original);
            Store = new(new SwarmCodeDataPaths(Profile), Options.Create(new OllamaVisionStorageOptions { ModelsDirectory = Models }));
            Http = new(this);
        }
        public Task<VisionRepairResult> Repair(CancellationToken ct = default) => new OllamaVisionRepair(new Factory(Http), Store, Runtime).RepairAsync("http://127.0.0.1:11434", Model, ct);
        public void Dispose() { Http.Dispose(); Directory.Delete(Root, true); }
        public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(handler, false); }
    private sealed class Protocol(Fixture fixture) : HttpMessageHandler
    {
        public string? Failure; public int Downloads; public Action? AfterImages; public Action? OnDownload;
        private int _images; private string? _firstAnswer;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath; object data;
            if (request.RequestUri.Host == "huggingface.co")
            {
                if (path.Contains("/resolve/"))
                {
                    Downloads++;
                    OnDownload?.Invoke();
                    return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Failure == "bad-download" ? new byte[Fixture.Component.Length]
                        : Failure == "truncated-download" ? Fixture.Component[..^1] : Fixture.Component) };
                }
                if (path == "/api/models") data = new[] { new { id = "publisher/example" } };
                else
                {
                    object File(string name, string sha, long size) => new { rfilename = name, lfs = new { sha256 = sha, size } };
                    var files = new List<object> { File("Example-VL-Q4_K_M-00001-of-00002.gguf", new string(Failure == "wrong-weight" ? 'b' : 'a', 64), 99), File("mmproj-Example-VL-F16.gguf", fixture.ComponentHash, Fixture.Component.Length) };
                    if (Failure != "missing-shard") files.Add(File("Example-VL-Q4_K_M-00002-of-00002.gguf", new string('d', 64), 77));
                    if (Failure == "ambiguous-projector") files.Add(File("mmproj-Example-VL-BF16.gguf", new string('c', 64), Fixture.Component.Length));
                    data = new { sha = new string('1', 40), siblings = files };
                }
            }
            else switch (path)
            {
                case "/api/version": data = new { version = "test" }; break;
                case "/api/tags":
                    data = new { models = Directory.GetFiles(Path.Combine(fixture.Models, "manifests"), "*", SearchOption.AllDirectories).Select(p => new { name = new DirectoryInfo(Path.GetDirectoryName(p)!).Name + ":" + Path.GetFileName(p), digest = Fixture.Hash(File.ReadAllBytes(p)), size = 100 }) }; break;
                case "/api/show":
                    using (var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)))
                    {
                        var model = body.RootElement.GetProperty("model").GetString()!;
                        var file = Path.Combine(fixture.Models, "manifests/registry.ollama.ai/library", model.Split(':')[0], model.Split(':')[1]);
                        data = new { capabilities = File.ReadAllText(file).Contains("image.projector") ? new[] { "completion", "tools", "vision" } : ["completion", "tools"], remote_host = Failure == "cloud-alias" ? "https://ollama.com" : "" };
                    } break;
                case "/api/chat":
                    Assert.True(fixture.Runtime.Active > 0);
                    using (var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)))
                    {
                        var png = Convert.FromBase64String(body.RootElement.GetProperty("messages")[0].GetProperty("images")[0].GetString()!);
                        var colors = ReadColors(png); var answer = JsonSerializer.Serialize(new { left = colors.Item1, right = colors.Item2 }); _firstAnswer ??= answer;
                        data = new { done = true, message = new { content = Failure == "constant-answer" ? _firstAnswer : answer } };
                        if (++_images == 2) AfterImages?.Invoke();
                    } break;
                default:
                    Assert.StartsWith("/api/blobs/sha256:", path); Assert.True(fixture.Runtime.Active > 0);
                    if (request.Method == HttpMethod.Head) return new(HttpStatusCode.NotFound);
                    await File.WriteAllBytesAsync(Path.Combine(fixture.Models, "blobs", "sha256-" + fixture.ComponentHash), await request.Content!.ReadAsByteArrayAsync(ct), ct);
                    data = new { }; break;
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(data)) };
        }
        private static (string, string) ReadColors(byte[] png)
        {
            using var data = new MemoryStream();
            for (var i = 8; i < png.Length;)
            {
                var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(i));
                if (Encoding.ASCII.GetString(png, i + 4, 4) == "IDAT") data.Write(png, i + 8, length);
                i += length + 12;
            }
            data.Position = 0; using var zip = new ZLibStream(data, CompressionMode.Decompress); using var pixels = new MemoryStream(); zip.CopyTo(pixels);
            var bytes = pixels.ToArray();
            string Color(int offset) => (bytes[offset], bytes[offset + 1], bytes[offset + 2]) switch
                { (255, 0, 0) => "red", (0, 255, 0) => "green", (0, 0, 255) => "blue", (255, 255, 0) => "yellow", _ => "unknown" };
            return (Color(1), Color(1 + 255 * 3));
        }
    }
    private sealed class CountingRuntime : ILocalLlmRuntimeManager
    {
        public int Active;
        public Task<IDisposable> AcquireAsync(LocalLlmRuntimeRequest request, CancellationToken ct = default) { Active++; return Task.FromResult<IDisposable>(new Lease(() => Active--)); }
        public Task<ILocalLlmRuntimeExclusiveSession> AcquireExclusiveActivityAsync(LocalLlmRuntimeRequest request, LocalLlmRuntimeActivity activity, CancellationToken ct = default)
        { Active++; return Task.FromResult<ILocalLlmRuntimeExclusiveSession>(new Session(() => Active--)); }
        public IDisposable TrackActivity(LocalLlmRuntimeActivity activity) => new Lease(() => { });
        public void ApplyExecutionPolicy(LocalLlmRuntimeRequest request, IDictionary<string, object> settings) { }
        public int HighPriorityPending => 0;
        public LocalLlmRuntimeSnapshot GetSnapshot() => throw new NotSupportedException();
        private sealed class Lease(Action dispose) : IDisposable { public void Dispose() => dispose(); }
        private sealed class Session(Action dispose) : ILocalLlmRuntimeExclusiveSession
        { public CancellationToken PreemptionToken => default; public IDisposable Activate() => new Lease(() => { }); public void Dispose() => dispose(); }
    }
}
