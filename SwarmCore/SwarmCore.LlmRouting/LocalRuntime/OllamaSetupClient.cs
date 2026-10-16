using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting.LocalRuntime;

public sealed record OllamaInstalledModel(string Name, string Digest, long SizeBytes, IReadOnlyList<string> Capabilities, bool IsOnDevice = false)
{
    public bool SupportsTools => Capabilities.Contains("tools", StringComparer.OrdinalIgnoreCase);
}
public sealed record OllamaInventory(string Version, IReadOnlyList<OllamaInstalledModel> Models);
public sealed record OllamaPullProgress(string Status, long Completed, long Total)
{
    public double? Percent => Total > 0 ? Math.Clamp(100.0 * Completed / Total, 0, 100) : null;
}
public sealed record OllamaModelTest(bool Passed, string Message, TimeSpan Elapsed);

/// <summary>Ollama's native protocol. No shell, model download on inference, or remote credentials.</summary>
public sealed partial class OllamaSetupClient(IHttpClientFactory http)
{
    public const string DefaultEndpoint = "http://127.0.0.1:11434";

    public static string ValidateEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidOperationException("Use an HTTPS Ollama address, or HTTP on localhost, without credentials in the URL.");
        return endpoint.TrimEnd('/');
    }

    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9._/:\-]{0,511}$", RegexOptions.CultureInvariant)]
    private static partial Regex ModelName();

    public static string NormalizeModel(string model)
    {
        model = model.Trim();
        foreach (var prefix in new[] { "ollama pull ", "ollama run " })
            if (model.StartsWith(prefix, StringComparison.Ordinal)) model = model[prefix.Length..].Trim();
        if (!ModelName().IsMatch(model) || model.Contains("..") || model.Contains("://"))
            throw new InvalidOperationException("Enter an Ollama model ID, hf.co/owner/repository:quantization, or a single ollama pull/run command.");
        return model;
    }

    public async Task<OllamaInventory> InventoryAsync(string endpoint, CancellationToken ct = default)
    {
        endpoint = ValidateEndpoint(endpoint);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        using var client = Client();
        using var version = await GetAsync(client, endpoint + "/api/version", timeout.Token).ConfigureAwait(false);
        using var tags = await GetAsync(client, endpoint + "/api/tags", timeout.Token).ConfigureAwait(false);
        using var slots = new SemaphoreSlim(4);
        var models = await Task.WhenAll(tags.RootElement.GetProperty("models").EnumerateArray().Select(async item =>
        {
            await slots.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
            var name = item.GetProperty("name").GetString()!;
            var capabilities = Strings(item, "capabilities");
            var onDevice = false;
                // Show is also the locality authority: a cloud model can be copied to an arbitrary alias.
                try
                {
                    using var response = await client.PostAsJsonAsync(endpoint + "/api/show", new { model = name }, timeout.Token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    using var show = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false), cancellationToken: timeout.Token).ConfigureAwait(false);
                    capabilities = Strings(show.RootElement, "capabilities");
                    var remoteHost = show.RootElement.TryGetProperty("remote_host", out var remote) ? remote.GetString() : null;
                    var recognized = show.RootElement.TryGetProperty("capabilities", out _) || show.RootElement.TryGetProperty("model_info", out _);
                    onDevice = OllamaModelLocalityPolicy.HasAuthoritativeOnDeviceEvidence(name, recognized, remoteHost);
                }
                catch (HttpRequestException) { }
                return new OllamaInstalledModel(name, item.GetProperty("digest").GetString()!, Number(item, "size"), capabilities, onDevice);
            }
            finally { slots.Release(); }
        })).ConfigureAwait(false);
        return new(version.RootElement.GetProperty("version").GetString()!, models);
    }

    public async IAsyncEnumerable<OllamaPullProgress> PullAsync(string endpoint, string model,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        endpoint = ValidateEndpoint(endpoint); model = NormalizeModel(model);
        using var client = Client();
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint + "/api/pull")
        { Content = JsonContent.Create(new { model, stream = true }) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
        var success = false;
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0) continue;
            if (line.Length > 65536) throw new InvalidDataException("Ollama returned an oversized download status.");
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            if (root.TryGetProperty("error", out var error)) throw new InvalidOperationException("Ollama: " + error.GetString());
            var status = root.TryGetProperty("status", out var s) ? s.GetString() ?? "Downloading" : "Downloading";
            success |= status == "success";
            yield return new(status, Number(root, "completed"), Number(root, "total"));
        }
        if (!success) throw new IOException("The download connection ended before Ollama confirmed success. Retry to resume the download.");
        var inventory = await InventoryAsync(endpoint, ct).ConfigureAwait(false);
        if (!inventory.Models.Any(m => m.Name.Equals(model, StringComparison.OrdinalIgnoreCase) ||
            m.Name.Equals(model + ":latest", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Ollama reported success but the model is not in its installed list. Refresh before using it.");
    }

    /// <summary>A small real tool-call probe, not a coding benchmark or a grant of tool authority.</summary>
    public async Task<OllamaModelTest> TestAsync(string endpoint, string model, CancellationToken ct = default)
    {
        endpoint = ValidateEndpoint(endpoint); model = NormalizeModel(model);
        var before = (await InventoryAsync(endpoint, ct).ConfigureAwait(false)).Models.SingleOrDefault(m => m.Name == model)
            ?? throw new InvalidOperationException("Install this exact model before testing it.");
        if (!before.IsOnDevice) throw new InvalidOperationException("Ollama did not confirm on-device execution for this model. The local test was not sent.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        using var client = Client(); client.Timeout = Timeout.InfiniteTimeSpan;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var response = await client.PostAsJsonAsync(endpoint + "/api/chat", new
        {
            model, stream = false, think = false, keep_alive = "5m",
            messages = new[] { new { role = "user", content = "Call report_ready with ready=true. Do not write prose." } },
            tools = new[] { new { type = "function", function = new { name = "report_ready", description = "Report readiness for this isolated test. No external action is performed.",
                parameters = new { type = "object", properties = new { ready = new { type = "boolean" } }, required = new[] { "ready" } } } } },
            options = new { num_ctx = 4096, num_predict = 128, temperature = 0 }
        }, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
        var root = json.RootElement;
        var passed = root.TryGetProperty("message", out var message) && message.TryGetProperty("tool_calls", out var calls)
            && calls.ValueKind == JsonValueKind.Array && calls.GetArrayLength() == 1
            && calls[0].TryGetProperty("function", out var function) && function.GetProperty("name").GetString() == "report_ready"
            && function.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty("ready", out var ready) && ready.ValueKind == JsonValueKind.True;
        var after = (await InventoryAsync(endpoint, ct).ConfigureAwait(false)).Models.SingleOrDefault(m => m.Name == model);
        if (after?.Digest != before.Digest) throw new InvalidOperationException("The model changed during the test. Test its new version again.");
        return new(passed, passed ? "Passed a real local tool-call test. Coding quality still depends on the task."
            : "The model loaded but did not return the requested tool call. Try another model or update Ollama.", watch.Elapsed);
    }

    private HttpClient Client()
    {
        var client = http.CreateClient(); client.MaxResponseContentBufferSize = 8 * 1024 * 1024; return client;
    }
    private static async Task<JsonDocument> GetAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var response = await client.GetAsync(url, ct).ConfigureAwait(false); response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }
    internal static IReadOnlyList<string> Strings(JsonElement json, string property) => json.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
        ? value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToArray() : [];
    internal static long Number(JsonElement json, string property) => json.TryGetProperty(property, out var value) && value.TryGetInt64(out var number) ? number : 0;
}
