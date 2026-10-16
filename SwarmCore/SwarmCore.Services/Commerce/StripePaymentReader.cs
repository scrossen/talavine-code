using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SwarmCore.Services.Commerce;

/// <summary>
/// Read-only Stripe reader. It accepts only restricted keys (<c>rk_live_</c>/<c>rk_test_</c>), issues
/// only GET requests, pins the API version so field shapes stay stable, and never echoes Stripe's
/// own error text (which can quote part of the key) back to callers.
/// </summary>
public sealed class StripePaymentReader : IPaymentProviderReader
{
    public const string ProviderId = "stripe";

    /// <summary>Pinned so subscription periods and charge/invoice links keep the shape parsed below.</summary>
    public const string ApiVersion = "2024-06-20";

    private static readonly Uri DefaultBase = new("https://api.stripe.com/");
    private const int PageSize = 100;
    private readonly HttpClient _http;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public StripePaymentReader(HttpClient http) : this(http, Task.Delay) { }

    internal StripePaymentReader(HttpClient http, Func<TimeSpan, CancellationToken, Task> delay)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _http.BaseAddress ??= DefaultBase;
        _delay = delay;
    }

    public string Provider => ProviderId;

    public PaymentKeyCheck CheckKey(string? apiKey)
    {
        var key = apiKey?.Trim() ?? "";
        if (key.Length == 0) return new(false, false, "Paste a Stripe restricted key.", null);
        if (key.Any(char.IsWhiteSpace)) return new(false, false, "The key contains spaces. Copy it again from Stripe.", null);
        var hint = key.Length >= 4 ? key[^4..] : null;
        if (key.StartsWith("rk_live_", StringComparison.Ordinal)) return new(true, true, null, hint);
        if (key.StartsWith("rk_test_", StringComparison.Ordinal)) return new(true, false, null, hint);
        if (key.StartsWith("sk_", StringComparison.Ordinal))
            return new(false, key.StartsWith("sk_live_", StringComparison.Ordinal),
                "That is a secret key, which can change your Stripe account. Create a restricted key with read-only access instead.", hint);
        if (key.StartsWith("pk_", StringComparison.Ordinal))
            return new(false, false, "That is a publishable key. Create a restricted key with read-only access instead.", hint);
        return new(false, false, "That doesn't look like a Stripe restricted key (it should start with rk_live_ or rk_test_).", hint);
    }

    public async Task<PaymentAccountInfo> VerifyAsync(string apiKey, CancellationToken ct)
    {
        var check = RequireAcceptedKey(apiKey);
        using (var probe = await GetJsonAsync(apiKey, "v1/customers?limit=1", "customers", ct).ConfigureAwait(false))
        {
            // A list object carries livemode on each item, not on the list itself; an account
            // with no customers yet has nothing to compare, which is not a mismatch.
            var root = probe.RootElement;
            var sample = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0
                ? data[0]
                : root;
            if (sample.ValueKind == JsonValueKind.Object
                && sample.TryGetProperty("livemode", out var livemode) && livemode.ValueKind is JsonValueKind.True or JsonValueKind.False
                && livemode.GetBoolean() != check.IsLiveMode)
                throw new PaymentProviderException("Stripe reported a different mode than the key. Copy the key again.", PaymentProviderFailure.UnexpectedResponse);
        }

        // The account itself is optional: a key scoped to customers, invoices, charges and
        // subscriptions is enough, so a refused account read just leaves the name blank.
        try
        {
            using var account = await GetJsonAsync(apiKey, "v1/account", "account details", ct).ConfigureAwait(false);
            var root = account.RootElement;
            var name = Str(Prop(Prop(root, "settings"), "dashboard"), "display_name")
                ?? Str(Prop(root, "business_profile"), "name");
            return new(Str(root, "id"), Bound(name, 200), check.IsLiveMode);
        }
        catch (PaymentProviderException exception) when (exception.Failure == PaymentProviderFailure.MissingPermission)
        {
            return new(null, null, check.IsLiveMode);
        }
    }

    public async Task<PaymentProviderSnapshot> ReadSnapshotAsync(string apiKey, PaymentSnapshotLimits limits, CancellationToken ct)
    {
        RequireAcceptedKey(apiKey);
        ArgumentNullException.ThrowIfNull(limits);
        var max = Math.Clamp(limits.MaxPerKind, 1, 50_000);
        var customers = await ListAsync(apiKey, "v1/customers", "customers", max, ParseCustomer, ct).ConfigureAwait(false);
        var invoices = await ListAsync(apiKey, "v1/invoices", "invoices", max, ParseInvoice, ct).ConfigureAwait(false);
        var charges = await ListAsync(apiKey, "v1/charges", "payments (charges)", max, ParseCharge, ct).ConfigureAwait(false);
        var subscriptions = await ListAsync(apiKey, "v1/subscriptions?status=all", "subscriptions", max, ParseSubscription, ct).ConfigureAwait(false);
        return new(customers, invoices, charges, subscriptions, DateTime.UtcNow);
    }

    private PaymentKeyCheck RequireAcceptedKey(string apiKey)
    {
        var check = CheckKey(apiKey);
        if (!check.Accepted) throw new PaymentProviderException(check.Problem ?? "The key was not accepted.", PaymentProviderFailure.InvalidKey);
        return check;
    }

    private async Task<PaymentRecordList<T>> ListAsync<T>(string apiKey, string path, string resource, int max,
        Func<JsonElement, T?> parse, CancellationToken ct) where T : class
    {
        var items = new List<T>();
        string? after = null;
        while (true)
        {
            var separator = path.Contains('?') ? '&' : '?';
            var url = $"{path}{separator}limit={PageSize}" + (after is null ? "" : $"&starting_after={Uri.EscapeDataString(after)}");
            using var page = await GetJsonAsync(apiKey, url, resource, ct).ConfigureAwait(false);
            var root = page.RootElement;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new PaymentProviderException($"Stripe returned an unexpected list of {resource}.", PaymentProviderFailure.UnexpectedResponse);
            string? lastId = null;
            foreach (var element in data.EnumerateArray())
            {
                lastId = Str(element, "id") ?? lastId;
                var item = parse(element);
                if (item is null) continue;
                if (items.Count >= max) return new(items, true);
                items.Add(item);
            }
            var hasMore = root.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True;
            if (!hasMore) return new(items, false);
            if (items.Count >= max) return new(items, true);
            if (lastId is null || lastId == after)
                throw new PaymentProviderException($"Stripe paging for {resource} did not advance.", PaymentProviderFailure.UnexpectedResponse);
            after = lastId;
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string apiKey, string relativeUrl, string resource, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
            request.Headers.TryAddWithoutValidation("Stripe-Version", ApiVersion);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                throw new PaymentProviderException("Stripe could not be reached. Try again shortly.", PaymentProviderFailure.Unavailable);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new PaymentProviderException("Stripe took too long to answer. Try again shortly.", PaymentProviderFailure.Unavailable);
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 2)
                {
                    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 * (attempt + 1));
                    await _delay(wait > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : wait, ct).ConfigureAwait(false);
                    continue;
                }

                switch (response.StatusCode)
                {
                    case HttpStatusCode.Unauthorized:
                        throw new PaymentProviderException("Stripe didn't accept this key. It may have been deleted or rolled; create a new restricted key.", PaymentProviderFailure.InvalidKey);
                    case HttpStatusCode.Forbidden:
                        throw new PaymentProviderException($"This key can't read {resource}. In Stripe, give the restricted key Read access to {resource}.", PaymentProviderFailure.MissingPermission);
                    case HttpStatusCode.TooManyRequests:
                        throw new PaymentProviderException("Stripe is limiting requests right now. The next sync will try again.", PaymentProviderFailure.RateLimited);
                }
                if ((int)response.StatusCode >= 500)
                    throw new PaymentProviderException("Stripe is having trouble right now. The next sync will try again.", PaymentProviderFailure.Unavailable);
                if (!response.IsSuccessStatusCode)
                    throw new PaymentProviderException($"Stripe refused the request for {resource} ({(int)response.StatusCode}).", PaymentProviderFailure.UnexpectedResponse);

                await response.Content.LoadIntoBufferAsync(8 * 1024 * 1024, ct).ConfigureAwait(false);
                var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                try
                {
                    return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                }
                catch (JsonException)
                {
                    throw new PaymentProviderException($"Stripe returned unreadable {resource}.", PaymentProviderFailure.UnexpectedResponse);
                }
            }
        }
    }

    private static PaymentCustomerRecord? ParseCustomer(JsonElement e)
    {
        var id = Str(e, "id");
        if (id is null || Bool(e, "deleted")) return null;
        return new(Bound(id, 100)!, Bound(Str(e, "email"), 255), Bound(Str(e, "name"), 200), Bound(Str(e, "currency"), 10),
            Bool(e, "delinquent"), Time(e, "created") ?? DateTime.UnixEpoch);
    }

    private static PaymentInvoiceRecord? ParseInvoice(JsonElement e)
    {
        var id = Str(e, "id");
        if (id is null) return null;
        return new(Bound(id, 100)!, Bound(Ref(e, "customer"), 100), Bound(Str(e, "number"), 100), Bound(Str(e, "status"), 30),
            Bound(Str(e, "currency"), 10) ?? "",
            Long(e, "total") ?? 0, Long(e, "amount_due") ?? 0, Long(e, "amount_paid") ?? 0, Long(e, "amount_remaining") ?? 0,
            Time(e, "created") ?? DateTime.UnixEpoch, Time(e, "due_date"), Time(Prop(e, "status_transitions"), "paid_at"));
    }

    private static PaymentChargeRecord? ParseCharge(JsonElement e)
    {
        var id = Str(e, "id");
        if (id is null) return null;
        return new(Bound(id, 100)!, Bound(Ref(e, "customer"), 100), Bound(Ref(e, "invoice"), 100), Bound(Str(e, "status"), 30) ?? "unknown",
            Bound(Str(e, "currency"), 10) ?? "", Long(e, "amount") ?? 0, Long(e, "amount_refunded") ?? 0,
            Bool(e, "paid"), Bool(e, "refunded"), Bound(Str(e, "description"), 300), Time(e, "created") ?? DateTime.UnixEpoch);
    }

    private static PaymentSubscriptionRecord? ParseSubscription(JsonElement e)
    {
        var id = Str(e, "id");
        if (id is null) return null;
        long? amount = null;
        string? currency = Str(e, "currency"), interval = null, planName = null;
        int? intervalCount = null;
        if (Prop(e, "items") is { } items && items.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            long total = 0;
            var complete = data.GetArrayLength() > 0;
            foreach (var item in data.EnumerateArray())
            {
                var price = Prop(item, "price");
                var unit = Long(price, "unit_amount");
                var quantity = Long(item, "quantity") ?? 1;
                if (unit is null) complete = false; else total += unit.Value * quantity;
                currency ??= Str(price, "currency");
                interval ??= Str(Prop(price, "recurring"), "interval");
                intervalCount ??= (int?)Long(Prop(price, "recurring"), "interval_count");
                planName ??= Str(price, "nickname");
            }
            if (complete) amount = total;
        }
        planName ??= Str(Prop(e, "plan"), "nickname");
        return new(Bound(id, 100)!, Bound(Ref(e, "customer"), 100), Bound(Str(e, "status"), 30) ?? "unknown", Bound(currency, 10),
            amount, Bound(interval, 20), intervalCount, Bound(planName, 200), Bool(e, "cancel_at_period_end"),
            Time(e, "created") ?? DateTime.UnixEpoch, Time(e, "current_period_end"), Time(e, "canceled_at"));
    }

    private static JsonElement? Prop(JsonElement? e, string name)
        => e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    private static string? Str(JsonElement? e, string name)
        => Prop(e, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    /// <summary>An id-or-expanded-object reference.</summary>
    private static string? Ref(JsonElement e, string name)
        => Prop(e, name) is { } v ? v.ValueKind == JsonValueKind.String ? v.GetString() : Str(v, "id") : null;

    private static long? Long(JsonElement? e, string name)
        => Prop(e, name) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt64(out var n) ? n : null;

    private static bool Bool(JsonElement e, string name)
        => Prop(e, name) is { ValueKind: JsonValueKind.True };

    private static DateTime? Time(JsonElement? e, string name)
        => Long(e, name) is { } seconds && seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime : null;

    private static string? Bound(string? value, int max)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    /// <summary>A dashboard link for a mirrored object; it requires the owner's own Stripe sign-in.</summary>
    public static string DashboardUrl(string kind, string externalId, bool liveMode)
    {
        var segment = kind switch
        {
            "customer" => "customers",
            "invoice" => "invoices",
            "payment" => "payments",
            "subscription" => "subscriptions",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        return string.Create(CultureInfo.InvariantCulture,
            $"https://dashboard.stripe.com/{(liveMode ? "" : "test/")}{segment}/{Uri.EscapeDataString(externalId)}");
    }
}
