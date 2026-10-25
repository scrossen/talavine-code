using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SwarmCore.Abstractions.Security;

namespace SwarmCore.Agent.Interactions;

/// <summary>
/// One normalization contract for facade, owner adapters, and stores. It
/// bounds display data before any side effect and replaces secret-bearing
/// content with neutral markers while retaining only a non-reversible exact
/// delivery identity.
/// </summary>
public static class InteractionCompletionCanonicalizer
{
    public const string SensitiveMessageMarker = "[sensitive response omitted]";

    public static InteractionRequestCompletion Canonicalize(
        InteractionRequestCompletion completion,
        IReadOnlyList<InteractionFormField>? fields = null)
        => CanonicalizeCore(completion, fields, trustFingerprint: true, validatePublicShape: false);

    /// <summary>Canonicalizes untrusted client input before any owner side effect.</summary>
    public static InteractionRequestCompletion CanonicalizePublic(
        InteractionRequestCompletion completion,
        IReadOnlyList<InteractionFormField>? fields = null)
        => CanonicalizeCore(completion, fields, trustFingerprint: false, validatePublicShape: true);

    private static InteractionRequestCompletion CanonicalizeCore(
        InteractionRequestCompletion completion,
        IReadOnlyList<InteractionFormField>? fields,
        bool trustFingerprint,
        bool validatePublicShape)
    {
        ArgumentNullException.ThrowIfNull(completion);
        ArgumentException.ThrowIfNullOrWhiteSpace(completion.Actor);
        fields ??= [];

        if (validatePublicShape && completion.Values.Count > 20)
            throw new ArgumentException("An interaction completion may contain at most 20 values.", nameof(completion));

        var actor = Bound(completion.Actor, 200)!;
        var choice = Bound(completion.ChoiceId, 160);
        var originalMessage = Bound(completion.Message, 16_000);
        var originalValues = completion.Values
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(kv => Bound(kv.Key, 200) ?? string.Empty,
                kv => Bound(kv.Value, 16_000) ?? string.Empty, StringComparer.Ordinal);
        var fingerprint = !trustFingerprint || string.IsNullOrWhiteSpace(completion.DeliveryFingerprint)
            ? Fingerprint(completion.Kind, actor, choice, originalMessage, originalValues)
            : completion.DeliveryFingerprint.Trim().ToLowerInvariant();
        // A value is sensitive because of what it IS, not only because of what
        // the field is called. A card number, an SSN, or an API key typed into a
        // field named "Notes" is still a credential, and this completion is
        // written to a durable, broadly-readable row. HumanTaskService's
        // handback minimization has always filtered on value content; without
        // the same test here the two writes of one submission disagreed and the
        // secret came to rest in the completion.
        var sensitiveValues = originalValues
            .Where(kv => IsSensitiveValue(kv.Key, kv.Value, fields))
            .Select(kv => kv.Value)
            .Where(value => !string.IsNullOrEmpty(value))
            .ToArray();
        var safeValues = originalValues
            .Where(kv => !IsSensitiveValue(kv.Key, kv.Value, fields))
            .ToDictionary(kv => kv.Key, kv => Bound(kv.Value, 4_000) ?? string.Empty, StringComparer.Ordinal);
        var safeMessage = ContainsSensitiveText(originalMessage)
            || sensitiveValues.Any(value => originalMessage?.Contains(value, StringComparison.Ordinal) == true)
            ? SensitiveMessageMarker
            : Bound(originalMessage, 2_000);

        return completion with
        {
            Actor = actor,
            ChoiceId = choice,
            Values = safeValues,
            Message = safeMessage,
            DeliveryFingerprint = fingerprint,
        };
    }

    public static bool Equivalent(InteractionRequestCompletion? left, InteractionRequestCompletion right)
        => left is not null
           && left.Kind == right.Kind
           && string.Equals(left.Actor, right.Actor, StringComparison.Ordinal)
           && string.Equals(left.ChoiceId, right.ChoiceId, StringComparison.Ordinal)
           && string.Equals(left.Message, right.Message, StringComparison.Ordinal)
           && string.Equals(left.DeliveryFingerprint, right.DeliveryFingerprint, StringComparison.Ordinal)
           && left.Values.OrderBy(kv => kv.Key).SequenceEqual(right.Values.OrderBy(kv => kv.Key));

    /// <summary>
    /// True when either the field is named/labelled as a credential or the
    /// submitted value itself matches a credential shape. Value matching uses
    /// the shared <see cref="CredentialPatterns"/> so this, the desktop
    /// handback guard, and chat redaction cannot fork again.
    /// </summary>
    private static bool IsSensitiveValue(string name, string value, IReadOnlyList<InteractionFormField> fields)
        => IsSensitiveField(name, fields) || CredentialPatterns.Describe(value) is not null;

    public static bool IsSensitiveField(string name, IReadOnlyList<InteractionFormField> fields)
    {
        var field = fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        return field?.IsSecret == true || ContainsSensitiveLabel($"{name} {field?.Label} {field?.Kind}");
    }

    public static bool ContainsSensitiveText(string? text)
        => !string.IsNullOrWhiteSpace(text)
           && (ContainsSensitiveLabel(text)
               || Regex.IsMatch(text, @"\b(?:\d[ -]*?){13,19}\b", RegexOptions.CultureInvariant));

    private static bool ContainsSensitiveLabel(string text)
        => Regex.IsMatch(text,
            "password|passphrase|secret|credential|api[-_ ]?key|access[-_ ]?token|bearer|cvv|card number|\\bpin\\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string Fingerprint(
        InteractionResolutionKind kind,
        string actor,
        string? choice,
        string? message,
        IReadOnlyDictionary<string, string> values)
    {
        var json = JsonSerializer.Serialize(new { kind, actor, choice, message, values });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    public static string? Bound(string? value, int max)
        => string.IsNullOrWhiteSpace(value) ? null
            : value.Trim().Length <= max ? value.Trim() : value.Trim()[..max];
}
