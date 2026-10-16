using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace SwarmCore.Agent.Marketing;

/// <summary>Supported X API only. Fixed HTTPS origin, no cookie/profile import, no raw provider error persistence.</summary>
public sealed class XProspectingClient(HttpClient http, string userAccessToken,
    Func<CancellationToken, Task>? reserveApiCall = null) : ISocialProspectingChannel
{
    private static readonly Uri Origin = new("https://api.x.com/2/");
    public async Task<SocialPerson> GetSelfAsync(CancellationToken ct)
    {
        using var json = await GetAsync("users/me?user.fields=description,protected", ct).ConfigureAwait(false);
        return Person(json.RootElement.GetProperty("data"));
    }
    public async Task<IReadOnlyList<SocialPerson>> SearchAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200) throw new ArgumentException("Invalid X search query.");
        using var json = await GetAsync("users/search?query=" + Uri.EscapeDataString(query) + "&max_results=10&user.fields=description,protected", ct).ConfigureAwait(false);
        return json.RootElement.TryGetProperty("data", out var data)
            ? data.EnumerateArray().Take(10).Select(Person).ToArray() : [];
    }
    public async Task<SocialFollowing> GetFollowingAsync(string accountId, CancellationToken ct)
    {
        RequireId(accountId);
        var ids = new HashSet<string>(StringComparer.Ordinal); string? cursor = null;
        for (var page = 0; page < 3; page++)
        {
            using var json = await GetAsync($"users/{accountId}/following?max_results=1000"
                + (cursor is null ? "" : "&pagination_token=" + Uri.EscapeDataString(cursor)), ct).ConfigureAwait(false);
            if (json.RootElement.TryGetProperty("data", out var data))
                foreach (var row in data.EnumerateArray())
                    if (row.TryGetProperty("id", out var id) && SocialProspectingPlan.ValidId(id.GetString())) ids.Add(id.GetString()!);
            cursor = json.RootElement.TryGetProperty("meta", out var meta) && meta.TryGetProperty("next_token", out var next) ? next.GetString() : null;
            if (string.IsNullOrEmpty(cursor)) return new(ids, true);
            if (cursor.Length > 2048) throw new InvalidOperationException("Invalid X pagination response.");
        }
        return new(ids, false);
    }
    public async Task<SocialFollowOutcome> FollowAsync(string accountId, string targetId, CancellationToken ct)
    {
        RequireId(accountId); RequireId(targetId);
        if (accountId == targetId) return new(SocialFollowState.NotDispatched, "Cannot follow the connected account itself.");
        var dispatched = false;
        try
        {
            // Credentials can be changed outside this client; prove account identity immediately before mutation.
            if ((await GetSelfAsync(ct).ConfigureAwait(false)).Id != accountId)
                return new(SocialFollowState.NotDispatched, "Connected account does not match the campaign.");
            if (reserveApiCall is not null) await reserveApiCall(ct).ConfigureAwait(false);
            using var request = Request(HttpMethod.Post, $"users/{accountId}/following");
            request.Content = JsonContent.Create(new { target_user_id = targetId });
            dispatched = true;
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // Even an error acknowledgement does not authorize an automatic retry of a mutation.
                return new(SocialFollowState.Uncertain, Failure(response.StatusCode));
            }
            using var json = await ReadBoundedAsync(response, ct).ConfigureAwait(false);
            if (json.RootElement.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
                return new(SocialFollowState.Uncertain, "X returned partial errors; reconcile without repeating the follow.");
            if (json.RootElement.TryGetProperty("data", out var data))
            {
                if (data.TryGetProperty("following", out var yes) && yes.ValueKind == JsonValueKind.True)
                    return new(SocialFollowState.Following, "X acknowledged following this user.");
                if (data.TryGetProperty("pending_follow", out var pending) && pending.ValueKind == JsonValueKind.True)
                    return new(SocialFollowState.Pending, "X acknowledged a pending follow request.");
            }
            return new(SocialFollowState.Uncertain, "X did not confirm the follow; reconcile without repeating it.");
        }
        catch (OperationCanceledException) when (!dispatched && ct.IsCancellationRequested)
        {
            // Nothing was sent: the caller observes its own cancellation, not a completed "not dispatched" step.
            throw;
        }
        catch (Exception)
        {
            return new(dispatched ? SocialFollowState.Uncertain : SocialFollowState.NotDispatched,
                dispatched ? "Acknowledgement unavailable; no repeat follow will be sent." : "No follow sent. Check connection, account identity, and API request budget.");
        }
    }
    private async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        if (reserveApiCall is not null) await reserveApiCall(ct).ConfigureAwait(false);
        using var request = Request(HttpMethod.Get, path);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(Failure(response.StatusCode));
        var json = await ReadBoundedAsync(response, ct).ConfigureAwait(false);
        if (json.RootElement.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
        { json.Dispose(); throw new InvalidOperationException("X returned partial data; no new follows are safe from this lookup."); }
        return json;
    }
    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        if (string.IsNullOrWhiteSpace(userAccessToken) || userAccessToken.Any(char.IsControl))
            throw new InvalidOperationException("Connect an X user access token, not a website password or application-only bearer token.");
        var request = new HttpRequestMessage(method, new Uri(Origin, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userAccessToken);
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }
    private static async Task<JsonDocument> ReadBoundedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        const int limit = 1_048_576;
        if (response.Content.Headers.ContentLength > limit) throw new InvalidOperationException("X response exceeded the size limit.");
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var bytes = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (bytes.Length + count > limit) throw new InvalidOperationException("X response exceeded the size limit.");
            bytes.Write(buffer, 0, count);
        }
        return JsonDocument.Parse(bytes.ToArray());
    }
    private static SocialPerson Person(JsonElement data)
    {
        var id = data.GetProperty("id").GetString(); var handle = data.GetProperty("username").GetString();
        if (!SocialProspectingPlan.ValidId(id) || !SocialProspectingPlan.ValidHandle(handle))
            throw new InvalidOperationException("X returned an invalid account identity.");
        var name = data.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var bio = data.TryGetProperty("description", out var b) ? b.GetString() ?? "" : "";
        return new(id!, handle!, name[..Math.Min(name.Length, 100)], bio[..Math.Min(bio.Length, 1000)],
            data.TryGetProperty("protected", out var p) && p.ValueKind == JsonValueKind.True);
    }
    private static void RequireId(string id) { if (!SocialProspectingPlan.ValidId(id)) throw new ArgumentException("Invalid X account id."); }
    private static string Failure(HttpStatusCode code) => code switch
    {
        HttpStatusCode.Unauthorized => "X authorization expired or is invalid. Reconnect the account.",
        HttpStatusCode.Forbidden => "X denied this capability. Check app access and users.read/follows.read/follows.write scopes.",
        HttpStatusCode.TooManyRequests => "X rate limit reached. Pause until the provider allows requests again.",
        HttpStatusCode.PaymentRequired => "X API credits or access are required. No automatic purchase will be made.",
        _ => "X request failed. Retain the recorded state and check the connection before continuing."
    };
}
