using SwarmCore.Agent.Catalog;
using SwarmCore.Services.Abstractions;
using SwarmCore.Services.Adapters;

namespace SwarmCore.Services.AgentTools;

/// <summary>Stable ids for the service-catalog meta-tools.</summary>
public static class ServiceCatalogToolIds
{
    public const string Search = "catalog.search_services";
    public const string Call = "catalog.call_service";
    public const string Add = "catalog.add_service";
    public const string OAuthStartDeviceFlow = "catalog.oauth_start_device_flow";
    public const string OAuthCompleteDeviceFlow = "catalog.oauth_complete_device_flow";
}

/// <summary>
/// Host-owned exposure and invocation policy for reusable service tools. Capability checks run
/// both while constructing the tool list and again against the reloaded definition immediately
/// before a call, so a revoked grant or changed service cannot ride stale model context.
/// </summary>
public sealed class ServiceCatalogToolPolicy
{
    public bool IncludeCall { get; init; } = true;
    public bool IncludeAdd { get; init; } = true;
    public Func<ServiceDefinition, CancellationToken, ValueTask<string?>>? AuthorizeCallAsync { get; init; }
    public Func<ServiceKind, ServiceSideEffect, IReadOnlyDictionary<string, string>, bool, CancellationToken, ValueTask<string?>>?
        AuthorizeAddAsync { get; init; }
}

/// <summary>
/// Any tool-use agent's window onto its own local <see cref="IServiceCatalog"/>: find a
/// capability by what it's for, call it, or register a brand-new one (an HTTP API + key,
/// a C# function, or an LLM route). Host-agnostic — depends only on <see cref="IServiceCatalog"/>,
/// so every app's chat/agent surfaces can register these tools against their own local catalog.
/// (Originally authored as Vibes-only <c>VibeServiceTools</c>; promoted here so Talavine
/// and CRM can give their own chats the same "author a reusable tool on demand" capability.)
/// </summary>
public static class ServiceCatalogTools
{
    /// <summary>A field/value pair — kept local rather than sharing a host's DTO, since this is the only shape these tools need.</summary>
    public sealed class FieldValueDto { public string Field { get; set; } = ""; public string Value { get; set; } = ""; }

    public sealed class SearchInput { public string Query { get; set; } = ""; public string? Kind { get; set; } public int? Limit { get; set; } }
    public sealed class ServiceInfoDto { public string Name { get; set; } = ""; public string Kind { get; set; } = ""; public string Description { get; set; } = ""; public string WhenToUse { get; set; } = ""; public bool Authenticated { get; set; } }
    public sealed class SearchOutput
    {
        public List<ServiceInfoDto> Services { get; set; } = new();
        public string? SearchHint { get; set; }
    }

    public sealed class CallInput { public string Name { get; set; } = ""; public List<FieldValueDto> Args { get; set; } = new(); }
    public sealed class CallOutput : IToolExecutionEvidence
    {
        public bool Ok { get; set; }
        public string? Result { get; set; }
        public string? Error { get; set; }
        bool IToolExecutionEvidence.Succeeded => Ok;
        bool IToolExecutionEvidence.LocalProcessCompleted => false;
    }

    public sealed class AddInput
    {
        public string Name { get; set; } = "";
        /// <summary>http, csharp, or llm.</summary>
        public string Kind { get; set; } = "http";
        public string Description { get; set; } = "";
        public string WhenToUse { get; set; } = "";
        /// <summary>Kind config as key/value pairs. http: baseUrl, path, query, headers, authScheme, authName. csharp: source. llm: provider, model, system.</summary>
        public List<FieldValueDto> Config { get; set; } = new();
        /// <summary>Optional existing credential handle to attach to the service.</summary>
        public string? CredentialRef { get; set; }
        /// <summary>Optional secret/API key — stored apart from the definition and never shown to you again.</summary>
        public string? Secret { get; set; }
        public string? SideEffect { get; set; }
    }
    public sealed class AddOutput : IToolExecutionEvidence
    {
        public bool Ok { get; set; }
        public string Name { get; set; } = "";
        public string? Error { get; set; }
        bool IToolExecutionEvidence.Succeeded => Ok;
        bool IToolExecutionEvidence.LocalProcessCompleted => false;
    }

    public sealed class OAuthStartDeviceFlowInput
    {
        public string CredentialRef { get; set; } = "";
        public string DeviceCodeEndpoint { get; set; } = "";
        public string TokenEndpoint { get; set; } = "";
        public string ClientId { get; set; } = "";
        public string Scope { get; set; } = "";
        public List<FieldValueDto> ExtraDeviceCodeFields { get; set; } = new();
        public List<FieldValueDto> ExtraTokenFields { get; set; } = new();
    }

    public sealed class OAuthStartDeviceFlowOutput
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
        public string CredentialRef { get; set; } = "";
        public string UserCode { get; set; } = "";
        public string VerificationUri { get; set; } = "";
        public string? VerificationUriComplete { get; set; }
        public string Message { get; set; } = "";
        public DateTimeOffset? ExpiresAt { get; set; }
        public int IntervalSeconds { get; set; }
    }

    public sealed class OAuthCompleteDeviceFlowInput
    {
        public string CredentialRef { get; set; } = "";
        public int? MaxWaitSeconds { get; set; }
    }

    public sealed class OAuthCompleteDeviceFlowOutput
    {
        public bool Ok { get; set; }
        public bool Pending { get; set; }
        public string? Error { get; set; }
        public string CredentialRef { get; set; } = "";
        public string? TokenType { get; set; }
        public string? Scope { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
        public bool HasRefreshToken { get; set; }
    }

    public static IReadOnlyList<ToolDescriptor> Build(
        IServiceCatalog catalog,
        ICredentialStore? credentials = null,
        IServiceHttpClient? http = null,
        ServiceCatalogToolPolicy? policy = null)
    {
        policy ??= new ServiceCatalogToolPolicy();
        var search = ToolDescriptor.Create<SearchInput, SearchOutput>(
            ServiceCatalogToolIds.Search, "catalog",
            "Find a capability ('service') for a need, including host builtins, MCP tools, HTTP APIs, LLMs, and C# functions. Describe what you want and OMIT kind unless you know its implementation kind. kind is an optional strict filter: builtin, mcp, http, llm, or csharp. A capability that works with a website is not necessarily an HTTP service. Returns matching services with their descriptions. Search BEFORE writing a new tool; one may already exist.",
            ToolCostClass.Cheap, ToolSideEffect.ReadOnly,
            keywords: new[] { "service", "find", "capability", "api", "search", "discover" },
            invoke: async (input, ct) =>
            {
                var kind = ParseKind(input.Kind);
                var hits = await catalog.SearchAsync(input.Query ?? "", kind, input.Limit ?? 8, ct).ConfigureAwait(false);
                string? hint = null;
                if (hits.Count == 0 && kind is not null)
                {
                    // Preserve the requested filter, but do not let an implementation-kind
                    // guess masquerade as proof that the entire capability inventory is empty.
                    var unfiltered = await catalog.SearchAsync(input.Query ?? "", null, 8, ct).ConfigureAwait(false);
                    hint = unfiltered.Count > 0
                        ? "No services matched the requested kind filter. An unfiltered search returned enabled services of kinds: "
                          + string.Join(", ", unfiltered.Select(s => s.Kind.ToString().ToLowerInvariant()).Distinct())
                          + ". Retry without kind before concluding that this capability is unavailable. Discovery does not authorize invocation."
                        : "No enabled services were found even without the kind filter.";
                }
                return new SearchOutput
                {
                    SearchHint = hint,
                    Services = hits.Select(s => new ServiceInfoDto
                    {
                        Name = s.Name, Kind = s.Kind.ToString().ToLowerInvariant(),
                        Description = s.Description, WhenToUse = s.WhenToUse,
                        Authenticated = !string.IsNullOrEmpty(s.CredentialRef),
                    }).ToList(),
                };
            });

        var call = ToolDescriptor.Create<CallInput, CallOutput>(
            ServiceCatalogToolIds.Call, "catalog",
            "Call a service from the catalog by name, passing arguments as field/value pairs (e.g. for an HTTP service, the path/query placeholders). Values are text: encode array/object arguments as valid JSON text according to the service input schema, not as space-separated words. Returns the service's result. The service's credentials are applied automatically.",
            ToolCostClass.External, ToolSideEffect.ExternalSend,
            keywords: new[] { "call", "invoke", "service", "run" },
            invoke: async (input, ct) =>
            {
                var service = await catalog.GetAsync(input.Name, ct).ConfigureAwait(false);
                if (service is null)
                    return new CallOutput { Ok = false, Error = $"No service named '{input.Name}'." };
                if (policy.AuthorizeCallAsync is not null)
                {
                    var denial = await policy.AuthorizeCallAsync(service, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(denial))
                        return new CallOutput { Ok = false, Error = denial };
                }
                if (DuplicateField(input.Args, StringComparer.Ordinal) is { } repeatedArg)
                    return new CallOutput { Ok = false, Error = $"Argument '{repeatedArg}' was given more than once; pass each field once." };
                var args = input.Args.ToDictionary(a => a.Field, a => (object?)a.Value);
                var r = await catalog.CallAuthorizedSnapshotAsync(service, args, ct).ConfigureAwait(false);
                return new CallOutput { Ok = r.Ok, Result = r.Output, Error = r.Error };
            });

        var add = ToolDescriptor.Create<AddInput, AddOutput>(
            ServiceCatalogToolIds.Add, "catalog",
            "Register a NEW reusable service so you (and future runs) can call it. kind=http needs config baseUrl + path (use {arg} placeholders) and optionally authScheme(bearer|header|query)+authName and a Secret or CredentialRef. kind=csharp needs config source (a C# body with Input, Web, Files, Progress, and Cancellation, ending in return …). Use Files.ReadAllBytesAsync/ReadAllTextAsync and its write/list/copy/move/delete methods for observable objective-scoped file work; never copy binary base64 through model text. C# can pass either string or byte[] bodies to Web.SendWithCredential(credentialRef, authScheme, method, url, body, headers, authName), so Files bytes go directly to HTTP and secrets never enter source. kind=llm needs config provider/model. Give a clear description and whenToUse so it can be found later.",
            ToolCostClass.Moderate, ToolSideEffect.WritesLocal,
            keywords: new[] { "add service", "register", "new capability", "create api", "define" },
            invoke: async (input, ct) =>
            {
                var kind = ParseKind(input.Kind);
                if (kind is null)
                    return new AddOutput { Ok = false, Name = input.Name, Error = $"Unknown service kind '{input.Kind}'." };
                if (DuplicateField(input.Config, StringComparer.OrdinalIgnoreCase) is { } repeatedConfig)
                    return new AddOutput { Ok = false, Name = input.Name, Error = $"Config field '{repeatedConfig}' was given more than once; pass each field once." };
                var config = input.Config.ToDictionary(a => a.Field, a => a.Value, StringComparer.OrdinalIgnoreCase);
                var sideEffect = ParseSideEffect(input.SideEffect, kind.Value);

                if (policy.AuthorizeAddAsync is not null)
                {
                    var hasCredentialMaterial = !string.IsNullOrWhiteSpace(input.CredentialRef)
                        || !string.IsNullOrWhiteSpace(input.Secret);
                    var denial = await policy.AuthorizeAddAsync(
                        kind.Value, sideEffect, config, hasCredentialMaterial, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(denial))
                        return new AddOutput { Ok = false, Name = input.Name, Error = denial };
                }

                if (kind == ServiceKind.CSharp)
                {
                    if (!config.TryGetValue("source", out var src) || string.IsNullOrWhiteSpace(src))
                    {
                        return new AddOutput
                        {
                            Ok = false,
                            Name = input.Name,
                            Error = "C# services require a non-empty config field named 'source'."
                        };
                    }

                    var (ok, error) = CSharpServiceAdapter.Validate(src);
                    if (!ok) return new AddOutput { Ok = false, Name = input.Name, Error = "C# didn't compile: " + error };
                }

                var svc = new ServiceDefinition
                {
                    Name = input.Name,
                    Kind = kind.Value,
                    Description = input.Description,
                    WhenToUse = input.WhenToUse,
                    Config = config,
                    CredentialRef = string.IsNullOrWhiteSpace(input.CredentialRef) ? null : input.CredentialRef.Trim(),
                    SideEffect = sideEffect,
                    CreatedBy = "agent",
                };
                await catalog.AddAsync(svc, string.IsNullOrWhiteSpace(input.Secret) ? null : input.Secret, ct).ConfigureAwait(false);
                return new AddOutput { Ok = true, Name = svc.Name };
            });

        var tools = new List<ToolDescriptor> { search };
        if (policy.IncludeCall) tools.Add(call);
        if (policy.IncludeAdd) tools.Add(add);

        if (credentials is not null && http is not null)
        {
            tools.Add(ToolDescriptor.Create<OAuthStartDeviceFlowInput, OAuthStartDeviceFlowOutput>(
                ServiceCatalogToolIds.OAuthStartDeviceFlow, "catalog",
                "Start a generic OAuth 2.0 device-code login and store the pending grant under a credential handle. Use when a reusable service needs delegated user account access. Return the verification URL/code to the user via ask_human or normal UI; do not ask for passwords or tokens.",
                ToolCostClass.External, ToolSideEffect.ExternalSend,
                keywords: new[] { "oauth", "login", "device code", "credential", "auth", "account" },
                invoke: (input, ct) => StartOAuthDeviceFlowAsync(input, credentials, http, ct)));

            tools.Add(ToolDescriptor.Create<OAuthCompleteDeviceFlowInput, OAuthCompleteDeviceFlowOutput>(
                ServiceCatalogToolIds.OAuthCompleteDeviceFlow, "catalog",
                "Complete a previously started OAuth 2.0 device-code login after the user signs in. Stores the token response under the same credential handle and returns only metadata, never the token. C# services can use the handle with Web.SendWithCredential.",
                ToolCostClass.External, ToolSideEffect.ExternalSend,
                keywords: new[] { "oauth", "login", "device code", "credential", "token", "complete" },
                invoke: (input, ct) => CompleteOAuthDeviceFlowAsync(input, credentials, http, ct)));
        }

        return tools;
    }

    private static string? DuplicateField(IEnumerable<FieldValueDto> values, StringComparer comparer)
        => values.GroupBy(v => v.Field, comparer).FirstOrDefault(g => g.Count() > 1)?.Key;

    private static ServiceKind? ParseKind(string? kind) => (kind ?? "").Trim().ToLowerInvariant() switch
    {
        "http" or "rest" or "api" => ServiceKind.Http,
        "csharp" or "c#" or "code" => ServiceKind.CSharp,
        "llm" or "model" or "ai" => ServiceKind.Llm,
        "mcp" => ServiceKind.Mcp,
        "builtin" => ServiceKind.Builtin,
        _ => null,
    };

    private static ServiceSideEffect ParseSideEffect(string? s, ServiceKind kind) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "readonly" or "read" => ServiceSideEffect.ReadOnly,
        "writeslocal" or "write" => ServiceSideEffect.WritesLocal,
        "externalsend" or "external" => ServiceSideEffect.ExternalSend,
        "destructive" => ServiceSideEffect.Destructive,
        _ => kind == ServiceKind.Http ? ServiceSideEffect.ExternalSend : ServiceSideEffect.ReadOnly,
    };

    private sealed class PendingOAuthDeviceFlow
    {
        public string CredentialRef { get; set; } = "";
        public string TokenEndpoint { get; set; } = "";
        public string ClientId { get; set; } = "";
        public string DeviceCode { get; set; } = "";
        public string Scope { get; set; } = "";
        public Dictionary<string, string> ExtraTokenFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public DateTimeOffset ExpiresAt { get; set; }
        public int IntervalSeconds { get; set; } = 5;
    }

    private static async Task<OAuthStartDeviceFlowOutput> StartOAuthDeviceFlowAsync(
        OAuthStartDeviceFlowInput input,
        ICredentialStore credentials,
        IServiceHttpClient http,
        CancellationToken ct)
    {
        var credentialRef = NormalizeCredentialRef(input.CredentialRef);
        if (credentialRef.Length == 0)
            return new OAuthStartDeviceFlowOutput { Ok = false, Error = "credentialRef is required." };
        if (string.IsNullOrWhiteSpace(input.DeviceCodeEndpoint) || string.IsNullOrWhiteSpace(input.TokenEndpoint))
            return new OAuthStartDeviceFlowOutput { Ok = false, CredentialRef = credentialRef, Error = "deviceCodeEndpoint and tokenEndpoint are required." };
        if (string.IsNullOrWhiteSpace(input.ClientId))
            return new OAuthStartDeviceFlowOutput { Ok = false, CredentialRef = credentialRef, Error = "clientId is required." };
        if (string.IsNullOrWhiteSpace(input.Scope))
            return new OAuthStartDeviceFlowOutput { Ok = false, CredentialRef = credentialRef, Error = "scope is required." };

        var form = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["client_id"] = input.ClientId.Trim(),
            ["scope"] = input.Scope.Trim(),
        };
        foreach (var extra in input.ExtraDeviceCodeFields)
        {
            if (!string.IsNullOrWhiteSpace(extra.Field))
                form[extra.Field.Trim()] = extra.Value ?? "";
        }

        var response = await PostFormAsync(http, input.DeviceCodeEndpoint.Trim(), form, ct).ConfigureAwait(false);
        if (!response.Ok)
        {
            return new OAuthStartDeviceFlowOutput
            {
                Ok = false,
                CredentialRef = credentialRef,
                Error = $"OAuth device-code request failed: HTTP {response.Status}: {Truncate(response.Body, 500)}"
            };
        }

        using var doc = System.Text.Json.JsonDocument.Parse(response.Body);
        var root = doc.RootElement;
        var deviceCode = JsonString(root, "device_code");
        var userCode = JsonString(root, "user_code");
        var verificationUri = JsonString(root, "verification_uri")
            ?? JsonString(root, "verification_url");
        if (string.IsNullOrWhiteSpace(deviceCode) || string.IsNullOrWhiteSpace(userCode) || string.IsNullOrWhiteSpace(verificationUri))
        {
            return new OAuthStartDeviceFlowOutput
            {
                Ok = false,
                CredentialRef = credentialRef,
                Error = "OAuth provider response did not include device_code, user_code, and verification_uri."
            };
        }

        var interval = Math.Clamp(JsonInt(root, "interval") ?? 5, 1, 30);
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(JsonInt(root, "expires_in") ?? 900, 60, 3600));
        var pending = new PendingOAuthDeviceFlow
        {
            CredentialRef = credentialRef,
            TokenEndpoint = input.TokenEndpoint.Trim(),
            ClientId = input.ClientId.Trim(),
            DeviceCode = deviceCode!,
            Scope = input.Scope.Trim(),
            // Last value wins, as for the device-code fields above.
            ExtraTokenFields = input.ExtraTokenFields
                .Where(v => !string.IsNullOrWhiteSpace(v.Field))
                .GroupBy(v => v.Field.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last().Value ?? "", StringComparer.OrdinalIgnoreCase),
            ExpiresAt = expiresAt,
            IntervalSeconds = interval,
        };

        await credentials.SetAsync(PendingOAuthKey(credentialRef),
            System.Text.Json.JsonSerializer.Serialize(pending), ct).ConfigureAwait(false);

        return new OAuthStartDeviceFlowOutput
        {
            Ok = true,
            CredentialRef = credentialRef,
            UserCode = userCode!,
            VerificationUri = verificationUri!,
            VerificationUriComplete = JsonString(root, "verification_uri_complete"),
            Message = JsonString(root, "message") ?? $"Open {verificationUri} and enter code {userCode}.",
            ExpiresAt = expiresAt,
            IntervalSeconds = interval,
        };
    }

    private static async Task<OAuthCompleteDeviceFlowOutput> CompleteOAuthDeviceFlowAsync(
        OAuthCompleteDeviceFlowInput input,
        ICredentialStore credentials,
        IServiceHttpClient http,
        CancellationToken ct)
    {
        var credentialRef = NormalizeCredentialRef(input.CredentialRef);
        if (credentialRef.Length == 0)
            return new OAuthCompleteDeviceFlowOutput { Ok = false, Error = "credentialRef is required." };

        var pendingJson = await credentials.GetAsync(PendingOAuthKey(credentialRef), ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(pendingJson))
        {
            return new OAuthCompleteDeviceFlowOutput
            {
                Ok = false,
                CredentialRef = credentialRef,
                Error = $"No pending OAuth device flow exists for credentialRef '{credentialRef}'."
            };
        }

        var pending = System.Text.Json.JsonSerializer.Deserialize<PendingOAuthDeviceFlow>(pendingJson);
        if (pending is null || string.IsNullOrWhiteSpace(pending.DeviceCode))
        {
            return new OAuthCompleteDeviceFlowOutput { Ok = false, CredentialRef = credentialRef, Error = "Pending OAuth device flow is invalid." };
        }

        if (DateTimeOffset.UtcNow > pending.ExpiresAt)
        {
            await credentials.DeleteAsync(PendingOAuthKey(credentialRef), ct).ConfigureAwait(false);
            return new OAuthCompleteDeviceFlowOutput { Ok = false, CredentialRef = credentialRef, Error = "OAuth device code expired. Start a new login." };
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(input.MaxWaitSeconds ?? 0, 0, 120));
        var interval = Math.Clamp(pending.IntervalSeconds, 1, 30);

        while (true)
        {
            var form = new Dictionary<string, string>(pending.ExtraTokenFields, StringComparer.OrdinalIgnoreCase)
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["client_id"] = pending.ClientId,
                ["device_code"] = pending.DeviceCode,
            };

            var response = await PostFormAsync(http, pending.TokenEndpoint, form, ct).ConfigureAwait(false);
            if (response.Ok)
            {
                using var doc = System.Text.Json.JsonDocument.Parse(response.Body);
                var root = doc.RootElement;
                if (string.IsNullOrWhiteSpace(JsonString(root, "access_token")))
                {
                    return new OAuthCompleteDeviceFlowOutput
                    {
                        Ok = false,
                        CredentialRef = credentialRef,
                        Error = "OAuth token response did not include access_token."
                    };
                }

                await credentials.SetAsync(credentialRef, response.Body, ct).ConfigureAwait(false);
                await credentials.DeleteAsync(PendingOAuthKey(credentialRef), ct).ConfigureAwait(false);

                var expiresAt = JsonInt(root, "expires_in") is { } seconds
                    ? DateTimeOffset.UtcNow.AddSeconds(seconds)
                    : (DateTimeOffset?)null;
                return new OAuthCompleteDeviceFlowOutput
                {
                    Ok = true,
                    CredentialRef = credentialRef,
                    TokenType = JsonString(root, "token_type"),
                    Scope = JsonString(root, "scope"),
                    ExpiresAt = expiresAt,
                    HasRefreshToken = !string.IsNullOrWhiteSpace(JsonString(root, "refresh_token")),
                };
            }

            var (error, description) = TryReadOAuthError(response.Body);
            if (string.Equals(error, "authorization_pending", StringComparison.OrdinalIgnoreCase))
            {
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    return new OAuthCompleteDeviceFlowOutput
                    {
                        Ok = false,
                        Pending = true,
                        CredentialRef = credentialRef,
                        Error = "Authorization is still pending. Ask the user to finish signing in, then call this tool again."
                    };
                }
            }
            else if (string.Equals(error, "slow_down", StringComparison.OrdinalIgnoreCase))
            {
                interval = Math.Min(interval + 5, 30);
            }
            else
            {
                return new OAuthCompleteDeviceFlowOutput
                {
                    Ok = false,
                    CredentialRef = credentialRef,
                    Error = $"OAuth token request failed: {error ?? "HTTP " + response.Status} {description}".Trim()
                };
            }

            await Task.Delay(TimeSpan.FromSeconds(interval), ct).ConfigureAwait(false);
        }
    }

    private static async Task<ServiceHttpResponse> PostFormAsync(
        IServiceHttpClient http,
        string url,
        IReadOnlyDictionary<string, string> fields,
        CancellationToken ct)
    {
        var body = string.Join("&", fields
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key))
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value ?? "")}"));
        return await http.SendAsync(
            new ServiceHttpRequest(
                "POST",
                url,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Content-Type"] = "application/x-www-form-urlencoded",
                    ["Accept"] = "application/json",
                },
                body),
            ct).ConfigureAwait(false);
    }

    private static string PendingOAuthKey(string credentialRef) => "oauth-pending." + credentialRef;

    private static string NormalizeCredentialRef(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var chars = value.Trim()
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? char.ToLowerInvariant(ch) : '-')
            .ToArray();
        return new string(chars).Trim('-');
    }

    private static string? JsonString(System.Text.Json.JsonElement element, string name)
        => element.ValueKind == System.Text.Json.JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? JsonInt(System.Text.Json.JsonElement element, string name)
    {
        if (element.ValueKind != System.Text.Json.JsonValueKind.Object
            || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        if (value.ValueKind == System.Text.Json.JsonValueKind.String && int.TryParse(value.GetString(), out var parsed))
            return parsed;
        return null;
    }

    private static (string? Error, string? Description) TryReadOAuthError(string body)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var root = doc.RootElement;
            return (JsonString(root, "error"), JsonString(root, "error_description"));
        }
        catch
        {
            return (null, Truncate(body, 500));
        }
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "...";
}
