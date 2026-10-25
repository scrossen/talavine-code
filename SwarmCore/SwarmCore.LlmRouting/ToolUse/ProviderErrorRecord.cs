using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.Services;

namespace SwarmCore.LlmRouting.ToolUse;

/// <summary>
/// Turns an error record that arrives inside a successful (HTTP 200) body or
/// event stream into a typed provider failure, so a failure reported after the
/// headers were sent is never mistaken for a completed answer. The exception
/// message carries only bounded machine identifiers; the provider's free text
/// can quote user input, tool arguments or credentials and is used only to
/// classify the failure.
/// </summary>
internal static class ProviderErrorRecord
{
    /// <summary>
    /// An error object from an OpenAI-compatible body or stream:
    /// <c>{"error":{"code":..., "type":..., "message":...}}</c>, or a bare
    /// <c>{"error":"..."}</c>. The code may be an HTTP status (OpenRouter) or a
    /// string identifier (OpenAI).
    /// </summary>
    public static LlmProviderHttpException FromOpenAICompatible(string provider, JsonNode? error, string context)
    {
        var details = error as JsonObject;
        var code = details?["code"];
        var type = details?["type"];
        var message = details is null ? Text(error) : Text(details["message"]);
        var status = HttpStatus(code)
                     ?? StatusForIdentifier(Text(code))
                     ?? StatusForIdentifier(Text(type));
        return Create(provider, context, status, $"code={Identifier(code)}; type={Identifier(type)}",
            string.Join(' ', Text(code), Text(type), message));
    }

    /// <summary>
    /// Anthropic's stream <c>error</c> event:
    /// <c>{"type":"error","error":{"type":"overloaded_error","message":...}}</c>.
    /// </summary>
    public static LlmProviderHttpException FromAnthropic(JsonNode? error, string context)
    {
        var details = error as JsonObject;
        var type = details?["type"];
        var status = StatusForIdentifier(Text(type));
        return Create("Claude", context, status, $"type={Identifier(type)}",
            string.Join(' ', Text(type), Text(details?["message"])));
    }

    private static LlmProviderHttpException Create(string provider, string context, int? status,
        string identifiers, string classificationText)
    {
        var category = LlmErrorPolicy.Classify(classificationText, status);
        // Without a reported status the failure still happened upstream after
        // the response began, which is what 502 Bad Gateway describes.
        return new LlmProviderHttpException(
            $"{provider} {context} reported an error: {identifiers}.",
            (HttpStatusCode)(status ?? (int)HttpStatusCode.BadGateway),
            category);
    }

    private static int? HttpStatus(JsonNode? code)
    {
        if (code is not JsonValue value) return null;
        int parsed;
        if (value.TryGetValue<int>(out var number)) parsed = number;
        else if (value.TryGetValue<string>(out var text)
                 && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var fromText)) parsed = fromText;
        else return null;
        return parsed is >= 400 and <= 599 ? parsed : null;
    }

    /// <summary>Documented error identifiers whose HTTP meaning is unambiguous.</summary>
    private static int? StatusForIdentifier(string identifier) => identifier.ToLowerInvariant() switch
    {
        "invalid_request_error" => 400,
        "authentication_error" or "invalid_api_key" => 401,
        "billing_error" or "insufficient_quota" => 402,
        "permission_error" => 403,
        "not_found_error" or "model_not_found" => 404,
        "request_too_large" => 413,
        "rate_limit_error" or "rate_limit_exceeded" => 429,
        "api_error" or "server_error" => 500,
        "timeout_error" => 504,
        "overloaded_error" => 529,
        _ => null,
    };

    private static string Text(JsonNode? value)
        => value is JsonValue scalar ? scalar.ToString() : string.Empty;

    private static string Identifier(JsonNode? value)
    {
        var text = Text(value);
        return text.Length is > 0 and <= 100
               && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')
            ? text
            : "unknown";
    }
}
