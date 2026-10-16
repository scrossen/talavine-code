using System.Net;
using System.Text.Json;

namespace SwarmCore.Abstractions.Networking;

/// <summary>Google APIs report quota exhaustion as both 403 and 429.
/// An ordinary permission-denied 403 must never be treated as transient quota.</summary>
public static class GoogleApiQuotaPolicy
{
    public static bool IsQuotaResponse(HttpStatusCode status, string? body)
    {
        if (status == HttpStatusCode.TooManyRequests) return true;
        if (status != HttpStatusCode.Forbidden || string.IsNullOrWhiteSpace(body)) return false;
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.ValueKind != JsonValueKind.Object
                || !json.RootElement.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.Object
                || !error.TryGetProperty("errors", out var errors)
                || errors.ValueKind != JsonValueKind.Array) return false;
            return errors.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.Object
                && e.TryGetProperty("reason", out var reason)
                && reason.ValueKind == JsonValueKind.String
                && reason.GetString() is "rateLimitExceeded" or "userRateLimitExceeded" or "quotaExceeded" or "dailyLimitExceeded");
        }
        catch (JsonException) { return false; }
    }
}
