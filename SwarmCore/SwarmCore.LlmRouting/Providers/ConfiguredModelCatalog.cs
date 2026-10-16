using System.Net.Http.Headers;
using System.Text.Json;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.Configuration;

namespace SwarmCore.LlmRouting.Providers;

/// <summary>Discovery for a draft connection, before a host or lane is rebuilt.</summary>
public sealed class ConfiguredModelCatalog(IHttpClientFactory http, ILlmApiKeyProvider keys)
{
    public async Task<IReadOnlyList<string>> DiscoverAsync(ConfiguredModelSource source, CancellationToken ct = default)
    {
        source.Validate(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var client = http.CreateClient();
        client.MaxResponseContentBufferSize = 4 * 1024 * 1024;
        if (source.RequiresApiKey)
        {
            var key = await keys.GetApiKeyAsync(source.Provider).WaitAsync(timeout.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Save an API key for this source first.");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
        var suffix = source.Kind switch { "Ollama" => "/api/tags", "OpenRouter" => "/models/user", _ => "/models" };
        using var response = await client.GetAsync(source.Endpoint.TrimEnd('/') + suffix, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Model discovery returned HTTP {(int)response.StatusCode}. Check the endpoint and key, or enter a model ID.");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
        var root = document.RootElement;
        var list = source.Kind == "Ollama" ? root.GetProperty("models") : root.GetProperty("data");
        return list.EnumerateArray().Select(m => m.GetProperty(source.Kind == "Ollama" ? "name" : "id").GetString())
            .Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }
}
