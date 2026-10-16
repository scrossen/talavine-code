using SwarmCore.Scripting;

namespace SwarmCore.Services.Http;

/// <summary>
/// Keeps an injected credential out of a response that returns to authored code or a model.
/// APIs echo the request back in error bodies (the Authorization header, the <c>?key=</c>
/// URL), so the value is removed in each form it travels in: raw, JSON-escaped, and
/// URL-encoded.
/// </summary>
internal static class CredentialEcho
{
    public static string Scrub(string text, params string?[] injected)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var registry = new ScriptSecretRegistry();
        foreach (var value in injected)
        {
            if (string.IsNullOrEmpty(value)) continue;
            registry.Register(value);
            registry.Register(Uri.EscapeDataString(value));
        }
        return registry.Redact(text);
    }
}
