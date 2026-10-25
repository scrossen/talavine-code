using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SwarmCore.Agent.AppControl;

namespace SwarmCore.Agent.Communication;

/// <summary>Private same-user discovery, separate from the MCP app catalog: this endpoint accepts only goal messages.</summary>
public static class GoalMailboxDiscovery
{
    public const string Prefix = "code-";
    public static string Directory => Environment.GetEnvironmentVariable("SWARMCORE_GOAL_MESSAGING_DIR") is { Length: > 0 } path
        ? path : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwarmCore", "goal-messaging");

    public static IReadOnlyList<LoopbackDiscoveryDirectory> Directories()
    {
        if (Environment.GetEnvironmentVariable("SWARMCORE_GOAL_MESSAGING_DIR") is { Length: > 0 } path)
            return [new(path)];
        var directories = new List<string> { Directory };
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
        {
            directories.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "SwarmCore", "goal-messaging"));
            directories.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SwarmCore", "goal-messaging"));
        }
        return directories.Distinct(StringComparer.Ordinal).Select(value => new LoopbackDiscoveryDirectory(value)).ToArray();
    }

    public static AppControlEndpoint? Find(string? instanceId = null)
    {
        if (instanceId is not null && (instanceId.Length != 32 || instanceId.Any(character => !Uri.IsHexDigit(character))))
            throw new ArgumentException("An exact Code profile identity is required.");
        var endpoints = Directories().SelectMany(directory => directory.List(requireReadable: true))
            .Where(endpoint => instanceId is null ? endpoint.App.StartsWith(Prefix, StringComparison.Ordinal)
                : endpoint.App == Prefix + instanceId)
            .DistinctBy(endpoint => (endpoint.App, endpoint.ProcessId, endpoint.ProcessStartUtc, endpoint.Url, endpoint.Token)).ToArray();
        if (endpoints.Length > 1) throw new InvalidOperationException("More than one Code goal inbox is running. Select the intended Code profile before connecting.");
        return endpoints.SingleOrDefault();
    }
}

/// <summary>No model or application assembly is needed to exchange messages with a named goal.</summary>
public sealed class GoalMailboxRemoteClient : IGoalMailboxClient, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly Func<string?, AppControlEndpoint?> _find;

    public GoalMailboxRemoteClient() : this(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }, GoalMailboxDiscovery.Find) { }
    public GoalMailboxRemoteClient(HttpMessageHandler handler, Func<string?, AppControlEndpoint?> find)
    {
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        _find = find;
    }

    public async Task<GoalMailboxIdentity?> DiscoverAsync(CancellationToken ct = default)
    {
        var endpoint = _find(null);
        if (endpoint is null) return null;
        using var response = await CallAsync(endpoint, new { operation = "hello" }, ct).ConfigureAwait(false);
        var identity = response.RootElement.GetProperty("identity").Deserialize<GoalMailboxIdentity>(Json)
            ?? throw new InvalidDataException("The Code inbox identity is missing.");
        if (endpoint.App != GoalMailboxDiscovery.Prefix + identity.InstanceId)
            throw new InvalidDataException("The Code inbox does not match its published identity.");
        return identity;
    }

    public async Task<IReadOnlyList<GoalContact>> ListAsync(string instanceId, CancellationToken ct = default)
    {
        using var response = await CallAsync(Find(instanceId), new { operation = "list", instanceId }, ct).ConfigureAwait(false);
        return response.RootElement.GetProperty("contacts").Deserialize<GoalContact[]>(Json) ?? [];
    }

    public async Task<GoalContact> RenameAsync(string instanceId, Guid goalId, string name, string handle,
        long expectedRevision, CancellationToken ct = default)
    {
        using var response = await CallAsync(Find(instanceId), new { operation = "rename", instanceId, goalId, name, handle, expectedRevision }, ct).ConfigureAwait(false);
        return response.RootElement.GetProperty("contact").Deserialize<GoalContact>(Json) ?? throw new InvalidDataException("The Code goal contact is missing.");
    }

    public async Task<GoalMessageThread> ReadAsync(string instanceId, Guid goalId, long afterSequence = 0,
        int take = 200, CancellationToken ct = default)
    {
        using var response = await CallAsync(Find(instanceId), new { operation = "read", instanceId, goalId, afterSequence, take, includeHostStatus = true }, ct).ConfigureAwait(false);
        return response.RootElement.GetProperty("thread").Deserialize<GoalMessageThread>(Json) ?? throw new InvalidDataException("The Code goal conversation is missing.");
    }

    public async Task<GoalInboxMessage> SendAsync(string instanceId, Guid goalId, GoalMessageCommand message, CancellationToken ct = default)
    {
        using var response = await CallAsync(Find(instanceId), new { operation = "send", instanceId, goalId, message }, ct).ConfigureAwait(false);
        return response.RootElement.GetProperty("message").Deserialize<GoalInboxMessage>(Json) ?? throw new InvalidDataException("The Code goal message receipt is missing.");
    }

    private AppControlEndpoint Find(string instanceId) => _find(instanceId)
        ?? throw new InvalidOperationException("This Code goal inbox is offline. Your message can remain saved in Talavine until that exact Code profile returns.");

    private async Task<JsonDocument> CallAsync(AppControlEndpoint endpoint, object input, CancellationToken ct)
    {
        if (!Uri.TryCreate(endpoint.Url, UriKind.Absolute, out var uri) || uri.Scheme != "http" || !string.IsNullOrEmpty(uri.UserInfo)
            || !System.Net.IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) || !System.Net.IPAddress.IsLoopback(address))
            throw new InvalidOperationException("Code goal messages require a verified local loopback endpoint.");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);
        request.Content = JsonContent.Create(input, options: Json);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string? error = null;
            try
            {
                using var failure = JsonDocument.Parse(body);
                if (response.StatusCode == System.Net.HttpStatusCode.Conflict
                    && failure.RootElement.TryGetProperty("code", out var code)
                    && code.ValueKind == JsonValueKind.String && code.GetString() == "goal_closed")
                    throw new GoalMailboxClosedException();
                error = failure.RootElement.GetProperty("error").GetString();
            }
            catch (JsonException) { }
            catch (KeyNotFoundException) { }
            throw new InvalidOperationException(error ?? "Code did not accept the goal message. The saved message ID must be retained for retry.");
        }
        return JsonDocument.Parse(body);
    }

    public void Dispose() => _http.Dispose();
}
