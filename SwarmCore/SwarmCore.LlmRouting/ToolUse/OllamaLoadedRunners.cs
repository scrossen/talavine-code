using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SwarmCore.LlmRouting.ToolUse;

/// <summary>
/// Reads the context window each loaded Ollama runner serves (<c>/api/ps</c>
/// context_length) so hosts that already probe a daemon can record real windows
/// for Model Scout without an extra call on a user's request path.
/// </summary>
public static class OllamaLoadedRunners
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Model name (normalized to an explicit tag) to loaded window. Empty when
    /// the daemon is unreachable, reports nothing loaded, or answers in an
    /// unrecognized shape; the caller never learns a guessed window.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, int>> ReadContextWindowsAsync(
        IHttpClientFactory httpClientFactory,
        string endpoint,
        ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(logger);
        if (!Uri.TryCreate(endpoint?.TrimEnd('/') + "/", UriKind.Absolute, out var apiRoot))
            return new Dictionary<string, int>();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            using var http = httpClientFactory.CreateClient();
            using var response = await http.GetAsync(new Uri(apiRoot, "api/ps"), timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return new Dictionary<string, int>();
            return OllamaRuntimeContextWindow.ParseLoadedContextLengths(
                await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            logger.LogDebug(ex, "Ollama at {Endpoint} did not report loaded runners.", endpoint);
            return new Dictionary<string, int>();
        }
    }
}
