using System.Text.Json;

namespace SwarmCore.Scripting;

/// <summary>
/// The secret values one script run has read, so the host can let the script
/// use them (sign in, call an API) while keeping them out of everything that
/// flows back to the model: output, return value, error text, logs.
/// <para>
/// This is the data-flow half of a credential boundary. The capability half is
/// deliberately open: a user who says "use my login for site X" has authorized
/// the use, and agent-authored code is how that use happens. What no request
/// authorizes is the value itself landing in model context or a transcript.
/// </para>
/// Thread-safe; a script may read secrets from several tasks.
/// </summary>
public sealed class ScriptSecretRegistry
{
    public const string RedactedMarker = "[redacted]";

    /// <summary>Values shorter than this would shred unrelated text if replaced.</summary>
    private const int MinSecretLength = 4;

    private readonly HashSet<string> _values = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly Action<string>? _onRegistered;

    /// <param name="onRegistered">
    /// Optional hook the host uses to mirror each value into its session-wide
    /// redaction (for example the chat transcript scrubber), so persisted turns
    /// and tool receipts never carry it either.
    /// </param>
    public ScriptSecretRegistry(Action<string>? onRegistered = null)
    {
        _onRegistered = onRegistered;
    }

    public bool HasSecrets
    {
        get { lock (_gate) return _values.Count > 0; }
    }

    /// <summary>Register an exact secret value. Null, whitespace and very short values are ignored.</summary>
    public void Register(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return;
        var trimmed = secret.Trim();
        if (trimmed.Length < MinSecretLength) return;

        bool added;
        lock (_gate)
        {
            added = _values.Add(trimmed);
            if (!string.Equals(trimmed, secret, StringComparison.Ordinal))
                _values.Add(secret);
        }

        if (added)
        {
            try
            {
                _onRegistered?.Invoke(trimmed);
            }
            catch
            {
                // Mirroring is a courtesy to the host; the run-local registry is the guarantee.
            }
        }
    }

    /// <summary>
    /// Replace every registered value (raw and JSON-escaped spellings) with
    /// <see cref="RedactedMarker"/>. Returns the input unchanged when nothing is registered.
    /// </summary>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(text))]
    public string? Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        string[] snapshot;
        lock (_gate)
        {
            if (_values.Count == 0) return text;
            snapshot = _values.OrderByDescending(v => v.Length).ToArray();
        }

        var result = text;
        foreach (var value in snapshot)
        {
            result = result.Replace(value, RedactedMarker, StringComparison.Ordinal);
            var escaped = JsonEncodedText.Encode(value).ToString();
            if (!string.Equals(escaped, value, StringComparison.Ordinal))
                result = result.Replace(escaped, RedactedMarker, StringComparison.Ordinal);
        }

        return result;
    }

    /// <summary>
    /// Redact a script's return value. Strings are redacted in place; any other
    /// object is serialized and, only if the serialized form carries a secret,
    /// replaced by its redacted JSON text so the structure survives without the
    /// value. Objects that cannot be serialized are replaced by the marker when
    /// any secret is registered, since they cannot be inspected.
    /// </summary>
    public object? RedactResult(object? result)
    {
        if (result is null || !HasSecrets) return result;
        if (result is string s) return Redact(s);

        string json;
        try
        {
            json = JsonSerializer.Serialize(result, result.GetType());
        }
        catch
        {
            return RedactedMarker;
        }

        var redacted = Redact(json);
        return string.Equals(redacted, json, StringComparison.Ordinal) ? result : redacted;
    }
}

/// <summary>
/// Host adapter that wraps a service a script resolved so the secret values it
/// hands out are recorded in the run's <see cref="ScriptSecretRegistry"/>. One
/// implementation per credential-bearing contract; the wrapper must implement
/// the same contract so typed callers and casts keep working.
/// </summary>
public interface IScriptServiceSecretWrapper
{
    /// <summary>True when <paramref name="serviceType"/> (the requested type, or the instance's runtime interfaces) is one this wrapper handles.</summary>
    bool CanWrap(Type serviceType);

    /// <summary>Return a registering decorator over <paramref name="service"/>.</summary>
    object Wrap(object service, ScriptSecretRegistry secrets);
}
