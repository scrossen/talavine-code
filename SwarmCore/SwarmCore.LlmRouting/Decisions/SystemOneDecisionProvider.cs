using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SwarmCore.Abstractions.LlmRouting.Decisions;

namespace SwarmCore.LlmRouting.Decisions;

/// <summary>System One wire protocol, used by hosted services and local Ollama. Never logs state or provider bodies.</summary>
public class SystemOneDecisionProvider(HttpClient http, IDecisionCredentials? credentials = null) : IChoiceDecisionProvider
{
    public virtual string Name => "system-one";
    protected virtual bool LocalOnly => false;

    public async Task<ChoiceDecision> ChooseAsync(DecisionConnection connection, ChoiceDecisionRequest request, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(connection.Endpoint, UriKind.Absolute, out var endpoint)
            || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment)
            || (LocalOnly ? !endpoint.IsLoopback || endpoint.Scheme is not ("http" or "https") : endpoint.Scheme != "https"))
            throw new InvalidOperationException(LocalOnly ? "Ollama decisions require a loopback HTTP(S) endpoint." : "Remote decisions require an HTTPS endpoint.");
        if (string.IsNullOrWhiteSpace(connection.Model) || string.IsNullOrWhiteSpace(request.State)
            || request.Options.Count is < 2 or > 26 || request.Options.Any(o => string.IsNullOrWhiteSpace(o.Id) || string.IsNullOrWhiteSpace(o.Description))
            || request.Options.Select(o => o.Id).Distinct(StringComparer.Ordinal).Count() != request.Options.Count)
            throw new InvalidOperationException("A decision requires a model, state, and 2 to 26 uniquely named options with descriptions.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(connection.TimeoutSeconds, 1, 120)));
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (!string.IsNullOrWhiteSpace(connection.CredentialName))
        {
            var key = credentials is null ? null : await credentials.ResolveAsync(connection.CredentialName, timeout.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("The decision connection credential is unavailable.");
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
        var body = JsonSerializer.SerializeToUtf8Bytes(new { model = connection.Model, state = request.State,
            questions = new { selection = new { type = "choice", instructions = request.Instructions,
                criteria = request.Options.ToDictionary(o => o.Id, o => o.Description) } } });
        if (body.Length > 65536) throw new InvalidOperationException("Decision input exceeds the connection's 64 KiB request limit.");
        message.Content = new ByteArrayContent(body);
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Decision service returned HTTP {(int)response.StatusCode}.");
        await response.Content.LoadIntoBufferAsync(65536, timeout.Token).ConfigureAwait(false);
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false));
        var answer = json.RootElement.GetProperty("answers").GetProperty("selection");
        var choice = answer.GetProperty("choice").GetString();
        // Protocol integrity only: this does not second-guess or rerank the decision.
        if (choice is null || !request.Options.Any(o => o.Id == choice)) throw new InvalidDataException("Decision service returned an unknown choice.");
        double? confidence = answer.TryGetProperty("confidence", out var c) && c.TryGetDouble(out var value)
            && double.IsFinite(value) && value is >= 0 and <= 1 ? value : null;
        return new(choice, confidence);
    }
}

public sealed class OllamaDecisionProvider(HttpClient http) : SystemOneDecisionProvider(http)
{
    public override string Name => "ollama";
    protected override bool LocalOnly => true;
}
