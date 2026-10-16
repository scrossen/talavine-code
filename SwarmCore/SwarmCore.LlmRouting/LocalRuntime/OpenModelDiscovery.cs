using System.Text.Json;
using System.Text.RegularExpressions;

namespace SwarmCore.LlmRouting.LocalRuntime;

public sealed record OpenModelCandidate(string Repository, LocalModelClassification Classification,
    long Downloads, DateTimeOffset? UpdatedAt, string? License, bool Gated)
{
    public string Url => "https://huggingface.co/" + Repository;
}
public sealed record OpenModelDownload(string Model, string Quantization, long SizeBytes);
public sealed record OpenModelDetails(OpenModelCandidate Candidate, IReadOnlyList<OpenModelDownload> Downloads);

/// <summary>Live Hub metadata, not scraped prose or model-generated recommendations. Publisher claims remain claims.</summary>
public sealed partial class OpenModelDiscovery(IHttpClientFactory http)
{
    public static IReadOnlyList<OpenModelDownload> Examples { get; } =
    [
        new("hf.co/huihui-ai/Huihui-Qwen3.8-27B-abliterated-GGUF:UD-Q4_K_XL", "UD-Q4_K_XL", 0),
        new("hf.co/HauhauCS/Gemma4-26B-A4B-Uncensored-HauhauCS-Balanced:Q4_K_M", "Q4_K_M", 0),
    ];

    public async Task<IReadOnlyList<OpenModelCandidate>> SearchAsync(string query = "", bool newest = false, CancellationToken ct = default)
    {
        query = query.Trim();
        if (query.Length > 120) throw new InvalidOperationException("Keep the model search under 120 characters.");
        var terms = string.IsNullOrWhiteSpace(query) ? new[] { "abliterated", "uncensored" } : new[] { query };
        var results = await Task.WhenAll(terms.Select(async term =>
        {
            using var json = await ReadAsync("https://huggingface.co/api/models?filter=gguf&search=" + Uri.EscapeDataString(term)
                + "&sort=" + (newest ? "lastModified" : "downloads") + "&direction=-1&limit=30&full=true", ct).ConfigureAwait(false);
            return json.RootElement.EnumerateArray().Select(ParseCandidate).Where(c => c is not null).Select(c => c!).ToArray();
        })).ConfigureAwait(false);
        var candidates = results.SelectMany(r => r).DistinctBy(c => c.Repository);
        return newest ? candidates.OrderByDescending(c => c.UpdatedAt).Take(30).ToArray()
            : candidates.OrderByDescending(c => c.Downloads).Take(30).ToArray();
    }

    public async Task<OpenModelDetails> InspectAsync(string repository, CancellationToken ct = default)
    {
        if (!RepositoryId().IsMatch(repository)) throw new InvalidOperationException("Use a Hugging Face owner/repository ID.");
        using var json = await ReadAsync("https://huggingface.co/api/models/" + repository + "?blobs=true", ct).ConfigureAwait(false);
        var candidate = ParseCandidate(json.RootElement) ?? throw new InvalidOperationException("This repository is not a public text-generation GGUF candidate.");
        var files = json.RootElement.GetProperty("siblings").EnumerateArray()
            .Select(f => (Name: f.GetProperty("rfilename").GetString()!, Size: OllamaSetupClient.Number(f, "size")))
            .Where(f => f.Name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
                && !f.Name.Contains("mmproj", StringComparison.OrdinalIgnoreCase)).ToArray();
        // Tags come from actual files. Group split GGUF shards and include their combined storage size.
        var downloads = files.Select(f => (File: f, Quant: Quantization().Match(f.Name)))
            .Where(f => f.Quant.Success).GroupBy(f => f.Quant.Groups[1].Value, StringComparer.OrdinalIgnoreCase)
            .Select(group => new OpenModelDownload("hf.co/" + repository + ":" + group.Key, group.Key, group.Sum(f => f.File.Size)))
            .OrderBy(d => d.Quantization.Equals("Q4_K_M", StringComparison.OrdinalIgnoreCase) ? 0
                : d.Quantization.Equals("UD-Q4_K_XL", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(d => d.SizeBytes).ToArray();
        return new(candidate, downloads);
    }

    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9._-]*/[a-zA-Z0-9][a-zA-Z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryId();
    [GeneratedRegex(@"(?:^|[.\-_])((?:UD-)?(?:IQ[1-4]_[A-Z0-9_]+|Q[2-8]_[A-Z0-9_]+|BF16|F16|F32))(?:-\d+-of-\d+)?\.gguf$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Quantization();

    private static OpenModelCandidate? ParseCandidate(JsonElement item)
    {
        var id = item.GetProperty("id").GetString()!;
        if (!RepositoryId().IsMatch(id) || id.Contains("..")) return null;
        var tags = OllamaSetupClient.Strings(item, "tags");
        if (!tags.Contains("gguf")) return null;
        var pipeline = item.TryGetProperty("pipeline_tag", out var p) ? p.GetString() : null;
        if (pipeline is not null && pipeline is not ("text-generation" or "image-text-to-text")) return null;
        var behavior = LocalModelBehaviors.Classify(id, tags);
        if (!behavior.ReducedRefusal) return null;
        var gated = item.TryGetProperty("gated", out var g) && g.ValueKind != JsonValueKind.False && g.ValueKind != JsonValueKind.Null;
        var modified = item.TryGetProperty("lastModified", out var d) && d.TryGetDateTimeOffset(out var date) ? date : (DateTimeOffset?)null;
        return new(id, behavior, OllamaSetupClient.Number(item, "downloads"), modified,
            tags.FirstOrDefault(t => t.StartsWith("license:", StringComparison.Ordinal))?[8..], gated);
    }

    private async Task<JsonDocument> ReadAsync(string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var client = http.CreateClient(); client.MaxResponseContentBufferSize = 8 * 1024 * 1024;
        using var response = await client.GetAsync(url, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Hugging Face returned HTTP {(int)response.StatusCode}. Try again later or enter an exact Ollama model ID.");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
    }
}
