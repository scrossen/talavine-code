using System.Net;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Security.Cryptography;

namespace SwarmCore.LlmRouting.Services;

/// <summary>Opt-in host authority, independent of a provider's advertised model list.</summary>
public interface IInferenceAdmission
{
    void Validate();
    InferenceAdmissionIdentity? Identity => null;
}

/// <summary>Read-only local artifact evidence. This is not a native-tool containment receipt.</summary>
public sealed record DirectTextHttpAdmissionEvidence
{
    public int SchemaVersion { get; init; } = 1;
    public string Transport { get; init; } = "direct-text-http";
    public string Protocol { get; init; } = "ollama-native-v1";
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public required string Endpoint { get; init; }
    public required string ServerVersion { get; init; }
    public required string ModelDigest { get; init; }
    public long ModelBytes { get; init; }
    public LocalInferenceServerIdentity? ServerIdentity { get; init; }
    public required string SourceEvidencePath { get; init; }
    public required string SourceEvidenceSha256 { get; init; }
    public string SourceEvidenceStatus { get; init; } = "PASS_READONLY_DIRECT_TEXT_HTTP_HEALTH";
    public DateTimeOffset VerifiedAtUtc { get; init; }
    public int MaximumContextTokens { get; init; } = 16384;
    public int MaximumOutputTokens { get; init; } = 4096;
    public int MaximumRequestBytes { get; init; } = 1048576;
    public int MaximumResponseBytes { get; init; } = 8388608;
    public int TimeoutSeconds { get; init; } = 180;
}

public sealed class DirectTextHttpAdmissionValidator(string proofPath, string proofSha256,
    string provider, string model, string endpoint, bool requireServerIdentity = true) : IInferenceAdmission
{
    public string EvidencePath => proofPath;
    public string EvidenceSha256 => proofSha256;
    public InferenceAdmissionIdentity Identity => new(provider,model,proofSha256);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public void Validate() => ValidateEvidence();
    public DirectTextHttpAdmissionEvidence ValidateEvidence()
    {
        try { return ValidateEvidenceCore(); }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or ArgumentException)
        { throw Denied("HTTP evidence could not be safely read and validated."); }
    }
    private DirectTextHttpAdmissionEvidence ValidateEvidenceCore()
    {
        if (!Path.IsPathFullyQualified(proofPath) || !File.Exists(proofPath)
            || !SameHash(InferenceAdmissionValidator.HashFile(proofPath), proofSha256)) throw Denied("HTTP evidence is missing or changed.");
        var proof = JsonSerializer.Deserialize<DirectTextHttpAdmissionEvidence>(File.ReadAllBytes(proofPath), Json)
            ?? throw Denied("HTTP evidence is invalid.");
        if (requireServerIdentity && proof.ServerIdentity is null)
            throw Denied("Current OS-backed local server identity is required.");
        var uri = ReviewedEndpoint(endpoint);
        if (proof.SchemaVersion != 1 || proof.Transport != "direct-text-http" || proof.Protocol != "ollama-native-v1"
            || proof.Provider != provider || proof.Model != model || ReviewedEndpoint(proof.Endpoint) != uri
            || string.IsNullOrWhiteSpace(proof.ServerVersion) || proof.ModelBytes <= 0
            || proof.ModelDigest.Length != 64 || proof.ModelDigest.Any(c => !Uri.IsHexDigit(c))
            || proof.SourceEvidenceStatus != "PASS_READONLY_DIRECT_TEXT_HTTP_HEALTH"
            || proof.VerifiedAtUtc > DateTimeOffset.UtcNow.AddMinutes(5) || proof.VerifiedAtUtc < DateTimeOffset.UtcNow.AddDays(-1)
            || proof.MaximumContextTokens is < 1024 or > 32768 || proof.MaximumOutputTokens is < 1 or > 8192
            || proof.MaximumRequestBytes is < 4096 or > 1048576 || proof.MaximumResponseBytes is < 4096 or > 16777216
            || proof.TimeoutSeconds is < 10 or > 600 || !Path.IsPathFullyQualified(proof.SourceEvidencePath)
            || !File.Exists(proof.SourceEvidencePath)
            || !SameHash(InferenceAdmissionValidator.HashFile(proof.SourceEvidencePath), proof.SourceEvidenceSha256))
            throw Denied("The exact local transport, route, artifact or bounded evidence changed.");
        using var source = JsonDocument.Parse(File.ReadAllBytes(proof.SourceEvidencePath));
        var observed = source.RootElement;
        if (!StringIs(observed, "status", proof.SourceEvidenceStatus) || !StringIs(observed, "provider", provider)
            || !StringIs(observed, "model", model) || !StringIs(observed, "endpoint", proof.Endpoint)
            || !StringIs(observed, "serverVersion", proof.ServerVersion) || !StringIs(observed, "modelDigest", proof.ModelDigest)
            || !observed.TryGetProperty("modelBytes", out var size) || size.GetInt64() != proof.ModelBytes
            || !observed.TryGetProperty("observedAtUtc", out var timestamp) || !timestamp.TryGetDateTimeOffset(out var time)
            || time < DateTimeOffset.UtcNow.AddDays(-1) || time > DateTimeOffset.UtcNow.AddMinutes(5))
            throw Denied("The actual read-only server/model health receipt does not bind this route.");
        if (proof.ServerIdentity is not null)
        {
            if (!observed.TryGetProperty("serverIdentity", out var identity)
                || identity.Deserialize<LocalInferenceServerIdentity>(Json) != proof.ServerIdentity)
                throw Denied("The OS-observed server identity receipt does not bind this route.");
            LocalInferenceServerIdentityValidator.Validate(proof.ServerIdentity, proof.Endpoint);
        }
        return proof;
    }
    public void ValidateModelCapabilityEvidence(SwarmCore.Abstractions.LlmRouting.LlmModelCapabilityEvidence evidence)
    {
        var proof = ValidateEvidence();
        using var document = JsonDocument.Parse(File.ReadAllBytes(proof.SourceEvidencePath));
        if (evidence.Provider != proof.Provider || evidence.Model != proof.Model || evidence.Transport != proof.Protocol
            || ReviewedEndpoint(evidence.Endpoint) != ReviewedEndpoint(proof.Endpoint)
            || !SameHash(evidence.ArtifactDigest, proof.ModelDigest)
            || evidence.ObservedAtUtc < DateTimeOffset.UtcNow.AddMinutes(-1)
            || evidence.ObservedAtUtc > DateTimeOffset.UtcNow.AddMinutes(1)
            || !document.RootElement.TryGetProperty("observedCapabilities", out var observed)
            || observed.ValueKind != JsonValueKind.Array
            || observed.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String)
            || !observed.EnumerateArray().Select(v => v.GetString()!).ToHashSet(StringComparer.Ordinal)
                .SetEquals(evidence.DeclaredCapabilities))
            throw Denied("Fresh model capability declarations do not match the exact admitted health/artifact/source receipt.");
    }
    internal static bool StringIs(JsonElement item, string name, string value) => item.TryGetProperty(name, out var found)
        && found.ValueKind == JsonValueKind.String && found.GetString() == value;
    internal static Uri ReviewedEndpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "http"
            || !IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) || !IPAddress.IsLoopback(address)
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.Port <= 0
            || uri.AbsolutePath.Contains('%') || uri.AbsolutePath.Contains("..", StringComparison.Ordinal))
            throw Denied("Only an explicit literal-loopback HTTP endpoint without credentials is allowed.");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }
    private static bool SameHash(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    internal static UnauthorizedAccessException Denied(string reason) => new("Inference admission denied: " + reason);
}

/// <summary>Owns the final physical handler. No inherited proxy, redirect, cookie or auth state.</summary>
public sealed record DirectTextHttpInferenceReceipt(Guid OperationId, string Phase, string Model, string ModelDigest,
    string EvidencePath, string EvidenceSha256, string RequestSha256, int RequestBytes,
    DateTimeOffset StartedAtUtc, DateTimeOffset? FinishedAtUtc = null, int? ResponseStatus = null,
    long? ResponseBytes = null, long? ElapsedMilliseconds = null, string? FailureCategory = null)
{
    /// <summary>Hash of bounded physical response bytes, never the response content.</summary>
    public string? ResponseSha256 { get; init; }
    /// <summary>Exact decoded content from a complete text response (including bounded NDJSON); absent for invalid/incomplete shapes.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ResponseContentSha256 { get; init; }
}

public sealed class AdmittedDirectTextHttpClientFactory(DirectTextHttpAdmissionValidator validator,
    Action<DirectTextHttpInferenceReceipt>? observe = null,
    Func<bool, CancellationToken, ValueTask>? validateInferenceAuthority = null,
    Func<bool, CancellationToken, ValueTask>? reservePhysicalInference = null) : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
    {
        var proof = validator.ValidateEvidence();
        return new HttpClient(new DirectTextHttpAdmissionHandler(validator, PhysicalTransport(), observe,
            validateInferenceAuthority, reservePhysicalInference))
        { Timeout = TimeSpan.FromSeconds(proof.TimeoutSeconds), MaxResponseContentBufferSize = proof.MaximumResponseBytes };
    }
    internal static HttpMessageHandler PhysicalTransport() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(10),
        Credentials = null, PreAuthenticate = false,
    };
}

/// <summary>Quiet-host metadata authority only; no proof means no inference, even on loopback.</summary>
public sealed class ConfinedLoopbackMetadataClientFactory(string endpoint) : IHttpClientFactory
{
    private readonly Uri root = DirectTextHttpAdmissionValidator.ReviewedEndpoint(endpoint);
    public HttpClient CreateClient(string name) => new(new DirectTextHttpAdmissionHandler(root,
        AdmittedDirectTextHttpClientFactory.PhysicalTransport()))
    { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 8388608 };
}

/// <summary>
/// Every provider path crosses this handler, including native typed-tool turns.
/// Tools in JSON are descriptions only: execution remains exclusively host catalog authority.
/// </summary>
public sealed class DirectTextHttpAdmissionHandler : DelegatingHandler
{
    private readonly DirectTextHttpAdmissionValidator? validator;
    private readonly Uri? metadataRoot;
    private readonly Action<DirectTextHttpInferenceReceipt>? observe;
    private readonly Func<bool, CancellationToken, ValueTask>? validateInferenceAuthority;
    private readonly Func<bool, CancellationToken, ValueTask>? reservePhysicalInference;
    public DirectTextHttpAdmissionHandler(DirectTextHttpAdmissionValidator admission, HttpMessageHandler inner,
        Action<DirectTextHttpInferenceReceipt>? observer = null,
        Func<bool, CancellationToken, ValueTask>? validateInferenceAuthority = null,
        Func<bool, CancellationToken, ValueTask>? reservePhysicalInference = null) : base(inner)
    { validator = admission; observe = observer; this.validateInferenceAuthority = validateInferenceAuthority;
        this.reservePhysicalInference = reservePhysicalInference; }
    public DirectTextHttpAdmissionHandler(Uri endpoint, HttpMessageHandler inner) : base(inner)
        => metadataRoot = DirectTextHttpAdmissionValidator.ReviewedEndpoint(endpoint.AbsoluteUri);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var proof = validator?.ValidateEvidence();
        var root = proof is null ? metadataRoot! : DirectTextHttpAdmissionValidator.ReviewedEndpoint(proof.Endpoint);
        ValidateTarget(request, root);
        var method = request.Method;
        var target = request.RequestUri!;
        var path = target.AbsolutePath[root.AbsolutePath.Length..];
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(TimeSpan.FromSeconds(proof?.TimeoutSeconds ?? 10));
        var maximumRequest = proof?.MaximumRequestBytes ?? 1048576;
        byte[]? body = null;
        if (request.Content is not null)
        {
            if (request.Content.Headers.ContentType?.MediaType != "application/json") throw Denied("Only typed JSON is allowed.");
            body = await ReadBoundedAsync(request.Content, maximumRequest, bounded.Token);
        }
        var inference = path == "api/chat" && method == HttpMethod.Post;
        if (inference && proof is null) throw Denied("Metadata authority does not admit inference.");
        if (method == HttpMethod.Get && path is "api/version" or "api/tags" or "api/ps")
        {
            if (body is not null) throw Denied("Metadata GET must have no body.");
        }
        else if (method == HttpMethod.Post && path == "api/show") ValidateShow(body, proof?.Model);
        else if (inference) body = ValidateChat(body, proof!);
        else throw Denied("Model management, embeddings, warmups and arbitrary HTTP have no authority.");

        var declaresTools = false;
        if (inference)
        {
            using var declaration = JsonDocument.Parse(body!);
            declaresTools = declaration.RootElement.TryGetProperty("tools", out var definitions) && definitions.GetArrayLength() > 0;
            await VerifyLiveArtifactAsync(root, proof!, bounded.Token, declaresTools);
        }
        // Freeze the actual physical request after validating the caller-owned
        // object, so mutation during metadata awaits cannot change the launch.
        using var physical = new HttpRequestMessage(method, target);
        if (body is not null) physical.Content = new ByteArrayContent(body)
        { Headers = { ContentType = new("application/json") } };
        if (!inference) return await SendBoundedAsync(physical, proof?.MaximumResponseBytes ?? 8388608, bounded.Token);
        // The common wire owns this check: raw provider/concrete DI paths cannot bypass host authority.
        // A separate operator-only factory may intentionally have no employee callback.
        if (validateInferenceAuthority is not null) await validateInferenceAuthority(declaresTools, bounded.Token);
        // A durable per-physical-call debit belongs only at the final pre-send
        // boundary. The authority callback above runs again after HTTP and must
        // never be used as a counter: that would charge one request twice.
        if (reservePhysicalInference is not null) await reservePhysicalInference(declaresTools, bounded.Token);
        var receipt = new DirectTextHttpInferenceReceipt(Guid.NewGuid(), "dispatched", proof!.Model, proof.ModelDigest,
            validator!.EvidencePath, validator.EvidenceSha256, Convert.ToHexStringLower(SHA256.HashData(body!)),
            body!.Length, DateTimeOffset.UtcNow);
        var elapsed = Stopwatch.StartNew();
        int? responseStatus = null;
        long? responseBytes = null;
        string? responseSha256 = null;
        string? responseContentSha256 = null;
        Observe(receipt);
        try
        {
            var response = await SendBoundedAsync(physical, proof.MaximumResponseBytes, bounded.Token);
            responseStatus = (int)response.StatusCode;
            try
            {
                // SendBoundedAsync already replaced content with its bounded byte buffer.
                // Compute telemetry before the final authority check, with no content exposed.
                var responseBody = await response.Content.ReadAsByteArrayAsync(bounded.Token);
                responseBytes = responseBody.Length;
                responseSha256 = Convert.ToHexStringLower(SHA256.HashData(responseBody));
                responseContentSha256 = DirectTextResponseContentIdentity.Compute(responseBody);
                await VerifyLiveArtifactAsync(root, proof, bounded.Token, declaresTools);
                if (validateInferenceAuthority is not null) await validateInferenceAuthority(declaresTools, bounded.Token);
            }
            catch { response.Dispose(); throw; }
            Observe(receipt with { Phase = "completed", FinishedAtUtc = DateTimeOffset.UtcNow,
                ResponseStatus = responseStatus, ResponseBytes = responseBytes, ResponseSha256 = responseSha256, ResponseContentSha256 = responseContentSha256,
                ElapsedMilliseconds = elapsed.ElapsedMilliseconds });
            return response;
        }
        catch (Exception ex)
        {
            Observe(receipt with { Phase = "failed", FinishedAtUtc = DateTimeOffset.UtcNow,
                ElapsedMilliseconds = elapsed.ElapsedMilliseconds, FailureCategory = ex.GetType().Name,
                ResponseStatus = responseStatus, ResponseBytes = responseBytes, ResponseSha256 = responseSha256 });
            throw;
        }
    }

    private static void ValidateTarget(HttpRequestMessage request, Uri root)
    {
        var uri = request.RequestUri;
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != root.Scheme || uri.Host != root.Host || uri.Port != root.Port
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || !uri.AbsolutePath.StartsWith(root.AbsolutePath, StringComparison.Ordinal) || uri.AbsolutePath.Contains('%')
            || request.Headers.Any()) throw Denied("The final HTTP request escaped its exact endpoint or carried inherited headers.");
    }
    private static void ValidateShow(byte[]? body, string? model)
    {
        using var document = JsonDocument.Parse(body ?? throw Denied("Model metadata requires typed input."));
        var input = document.RootElement;
        if (input.ValueKind != JsonValueKind.Object || input.EnumerateObject().Any(p => p.Name is not ("model" or "verbose"))
            || !input.TryGetProperty("model", out var name) || name.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(name.GetString()) || (model is not null && name.GetString() != model)
            || (input.TryGetProperty("verbose", out var verbose) && verbose.ValueKind != JsonValueKind.False))
            throw Denied("Only exact selected-model read-only metadata is allowed.");
    }
    private static byte[] ValidateChat(byte[]? body, DirectTextHttpAdmissionEvidence proof)
    {
        using var document = JsonDocument.Parse(body ?? throw Denied("Inference requires typed input."));
        var input = document.RootElement;
        if (input.ValueKind != JsonValueKind.Object || input.EnumerateObject().Any(p => p.Name is not
                ("model" or "messages" or "stream" or "tools" or "think" or "truncate" or "options" or "format"))
            || !DirectTextHttpAdmissionValidator.StringIs(input, "model", proof.Model)
            || !input.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array
            || messages.GetArrayLength() is < 1 or > 256
            || !messages.EnumerateArray().Any(m => m.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(content.GetString())))
            throw Denied("Only bounded text inference for the exact selected model is allowed.");
        foreach (var message in messages.EnumerateArray())
            if (message.ValueKind != JsonValueKind.Object || message.EnumerateObject().Any(p => p.Name is not
                    ("role" or "content" or "thinking" or "tool_calls" or "tool_call_id" or "tool_name" or "name"))
                || !message.TryGetProperty("role", out var role) || role.ValueKind != JsonValueKind.String
                || role.GetString() is not ("system" or "user" or "assistant" or "tool")
                || message.EnumerateObject().Any(p => p.Name is "content" or "thinking" or "tool_call_id" or "tool_name" or "name"
                    && p.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                || message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind != JsonValueKind.Array)
                throw Denied("Images, attachments and unknown message surfaces have no text-only authority.");
        if (input.TryGetProperty("tools", out var tools) && tools.ValueKind != JsonValueKind.Array
            || input.TryGetProperty("stream", out var stream) && stream.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || input.TryGetProperty("truncate", out var truncate) && truncate.ValueKind != JsonValueKind.False
            || input.TryGetProperty("format", out var format) && (format.ValueKind != JsonValueKind.String || format.GetString() != "json")
            || input.TryGetProperty("think", out var think) && think.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.String))
            throw Denied("Unreviewed inference surfaces have no authority.");
        var payload = System.Text.Json.Nodes.JsonNode.Parse(body!)!.AsObject();
        if (payload["options"] is not null && payload["options"] is not System.Text.Json.Nodes.JsonObject)
            throw Denied("Inference options must be a typed object.");
        var options = payload["options"] as System.Text.Json.Nodes.JsonObject ?? new();
        foreach (var pair in options)
        {
            if (pair.Value is null || !double.TryParse(pair.Value.ToJsonString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                throw Denied("Inference options must be finite reviewed numeric generation controls.");
            var valid = pair.Key switch
            {
                "num_ctx" => number == Math.Truncate(number) && number >= 1024 && number <= proof.MaximumContextTokens,
                "num_predict" => number == Math.Truncate(number) && number >= 1 && number <= proof.MaximumOutputTokens,
                "temperature" => number is >= 0 and <= 2,
                "top_p" => number is > 0 and <= 1,
                "top_k" => number == Math.Truncate(number) && number is >= 1 and <= 100,
                "repeat_penalty" => number is >= 0 and <= 2,
                "seed" => number == Math.Truncate(number) && number is >= 0 and <= int.MaxValue,
                _ => false,
            };
            if (!valid) throw Denied("Daemon, allocation or out-of-budget options are not admitted.");
        }
        options["num_predict"] ??= proof.MaximumOutputTokens;
        options["num_ctx"] ??= proof.MaximumContextTokens;
        if (payload["options"] is null) payload["options"] = options;
        var frozenBody = Encoding.UTF8.GetBytes(payload.ToJsonString());
        if (frozenBody.Length > proof.MaximumRequestBytes)
            throw Denied("The final bounded-generation payload exceeded its outbound byte budget.");
        return frozenBody;
    }

    private async Task VerifyLiveArtifactAsync(Uri root, DirectTextHttpAdmissionEvidence proof, CancellationToken ct, bool requiresTools = false)
    {
        using var version = await MetadataAsync(root, "api/version", null, proof.MaximumResponseBytes, ct);
        if (!DirectTextHttpAdmissionValidator.StringIs(version.RootElement, "version", proof.ServerVersion)) throw Denied("Daemon identity/version changed.");
        using var tags = await MetadataAsync(root, "api/tags", null, proof.MaximumResponseBytes, ct);
        if (!tags.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array
            || !models.EnumerateArray().Any(item =>
                (DirectTextHttpAdmissionValidator.StringIs(item, "name", proof.Model) || DirectTextHttpAdmissionValidator.StringIs(item, "model", proof.Model))
                && DirectTextHttpAdmissionValidator.StringIs(item, "digest", proof.ModelDigest)
                && item.TryGetProperty("size", out var bytes) && bytes.GetInt64() == proof.ModelBytes))
            throw Denied("The exact installed local artifact is missing or changed.");
        using var show = await MetadataAsync(root, "api/show", JsonSerializer.SerializeToUtf8Bytes(new { model = proof.Model }), proof.MaximumResponseBytes, ct);
        if (show.RootElement.TryGetProperty("remote_host", out var remote) && !string.IsNullOrWhiteSpace(remote.GetString())
            || show.RootElement.TryGetProperty("remote_model", out var remoteModel) && !string.IsNullOrWhiteSpace(remoteModel.GetString())
            || !show.RootElement.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Array
            || capabilities.EnumerateArray().Any(c => c.ValueKind != JsonValueKind.String)
            || !capabilities.EnumerateArray().Any(c => c.GetString() == "completion")
            || requiresTools && !capabilities.EnumerateArray().Any(c => c.GetString() == "tools"))
            throw Denied("The selected model is not proven local text-completion capable.");
        if (requiresTools)
        {
            using var declaredProof = JsonDocument.Parse(File.ReadAllBytes(proof.SourceEvidencePath));
            if (!declaredProof.RootElement.TryGetProperty("observedCapabilities", out var expected)
                || expected.ValueKind != JsonValueKind.Array || expected.EnumerateArray().Any(c => c.ValueKind != JsonValueKind.String)
                || !expected.EnumerateArray().Select(c => c.GetString()!).ToHashSet(StringComparer.Ordinal)
                    .SetEquals(capabilities.EnumerateArray().Select(c => c.GetString()!)))
                throw Denied("The fresh typed-tool model capabilities changed from the admitted source receipt.");
        }
        // Recheck immutable host evidence after network awaits too.
        validator!.ValidateEvidence();
    }
    private async Task<JsonDocument> MetadataAsync(Uri root, string path, byte[]? body, int maximum, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, new Uri(root, path));
        if (body is not null) request.Content = new ByteArrayContent(body) { Headers = { ContentType = new("application/json") } };
        ValidateTarget(request, root);
        using var response = await SendBoundedAsync(request, maximum, ct);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
    }
    private async Task<HttpResponseMessage> SendBoundedAsync(HttpRequestMessage request, int maximum, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct);
        try
        {
            if ((int)response.StatusCode is >= 300 and < 400) throw Denied("Redirects are never followed.");
            var bytes = await ReadBoundedAsync(response.Content, maximum, ct);
            response.Content.Dispose();
            response.Content = new ByteArrayContent(bytes);
            return response;
        }
        catch { response.Dispose(); throw; }
    }
    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken ct)
    {
        if (content.Headers.ContentLength > maximum) throw Denied("HTTP content exceeded its reviewed byte budget.");
        await using var input = await content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int length;
        while ((length = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + length > maximum) throw Denied("HTTP content exceeded its reviewed byte budget.");
            output.Write(buffer, 0, length);
        }
        return output.ToArray();
    }
    private static UnauthorizedAccessException Denied(string reason) => DirectTextHttpAdmissionValidator.Denied(reason);
    private void Observe(DirectTextHttpInferenceReceipt receipt)
    {
        // Telemetry is not authority and must never turn an already-dispatched
        // inference into a retry. No content, header values or model text escape.
        try { observe?.Invoke(receipt); } catch { }
    }
}

/// <summary>Content identity for the existing admitted text wire protocol; never returns model text.</summary>
public static class DirectTextResponseContentIdentity
{
    public static string? Compute(byte[] bytes)
    {
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes);
            var values = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var content = new StringBuilder(); var ended = false;
            foreach (var value in values)
            {
                if (ended) return null;
                using var document = JsonDocument.Parse(value);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count(p => p.Name == "message") != 1
                    || root.EnumerateObject().Count(p => p.Name == "done") != 1 || !root.TryGetProperty("message", out var message)
                    || message.ValueKind != JsonValueKind.Object || message.EnumerateObject().Count(p => p.Name == "content") != 1
                    || !message.TryGetProperty("content", out var field) || field.ValueKind != JsonValueKind.String
                    || root.GetProperty("done").ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
                content.Append(field.GetString()); ended = root.GetProperty("done").GetBoolean();
            }
            return ended ? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content.ToString()))) : null;
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException) { return null; }
    }
}
