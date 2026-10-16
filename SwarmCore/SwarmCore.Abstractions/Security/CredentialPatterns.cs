using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text;

namespace SwarmCore.Abstractions.Security;

/// <summary>
/// One home for "does this text look like a credential?". Two call sites need
/// the same answer for different reasons:
/// <list type="bullet">
///   <item><description><c>HumanTaskService</c> and
///   <c>InteractionCompletionCanonicalizer</c> STRIP a submitted value that
///   looks like a credential, so it never comes to rest in the durable task
///   row.</description></item>
///   <item><description><c>ChatSecretRedactionService</c> REWRITES the match out
///   of persisted chat history.</description></item>
/// </list>
/// They each used to carry their own copy of the patterns, so a shape added to
/// one silently stayed missing from the other.
/// <para>
/// False positives are the real cost here — this runs over ordinary user prose,
/// and shredding a sentence is a visible bug. So every pattern is anchored on
/// something a human does not type by accident: an assignment separator after a
/// credential keyword (<c>password: …</c>, <c>api_key=…</c>), a vendor's
/// distinctive high-entropy prefix (<c>sk-</c>, <c>ghp_</c>, <c>AKIA…</c>), or a
/// PEM block delimiter. A bare keyword ("check the token usage report") never
/// matches on its own, and prefix matches are case-SENSITIVE because the vendors
/// emit them that way.
/// </para>
/// </summary>
public static partial class CredentialPatterns
{
    // ── redaction patterns ──────────────────────────────────────

    /// <summary>
    /// Credential keyword + assignment separator + value. The value ends at
    /// whitespace/comma/semicolon (or the closing quote when quoted).
    /// </summary>
    [GeneratedRegex(
        """(?<key>\b(?:password|passwd|pwd|pw|app[ _-]?password|app[ _-]?pw|secret|token|api[ _-]?key|access[ _-]?key|client[ _-]?secret)\b)\s*(?<sep>[:=])\s*(?:"(?<dq>[^"]+)"|'(?<sq>[^']+)'|(?<bare>[^\s,;]+))""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AssignedCredentialRegex();

    /// <summary>
    /// Vendor-issued tokens with a distinctive, high-entropy prefix. These need
    /// no surrounding syntax — the prefix plus length IS the evidence, which is
    /// what makes them safe to redact out of free prose.
    /// Covers OpenAI/Anthropic (<c>sk-</c>, <c>sk-ant-</c>), GitHub
    /// (<c>ghp_/gho_/ghs_/ghu_/ghr_</c>), Slack (<c>xoxb-</c> and friends), AWS
    /// access key ids (<c>AKIA…</c>), HubSpot (<c>pat-na1-…</c>) and Google
    /// (<c>AIza…</c>).
    /// </summary>
    [GeneratedRegex(
        """(?<![A-Za-z0-9])(?:""" + SwarmCore.Secrets.SecretReferenceSyntax.VendorTokenShape + """)""",
        RegexOptions.CultureInvariant)]
    private static partial Regex VendorTokenRegex();

    /// <summary>
    /// Quoted JSON/appsettings property whose NAME ends in a credential keyword,
    /// with a quoted value: <c>"FTP Login Password": "…"</c>, <c>"BraveAPI": "…"</c>.
    /// <see cref="AssignedCredentialRegex"/> cannot see these — the closing quote
    /// sits between the keyword and the separator — and pasted config sections
    /// are exactly how users hand credentials to the app, so the whole property
    /// name is the evidence here. The name must END with the keyword — that is
    /// what keeps "TokenEndpoint"/"ApiUrl"-style references out — and
    /// URL-shaped values are left alone.
    /// </summary>
    [GeneratedRegex(
        """"(?<key>[^"\r\n]{0,50}?(?:password|passwd|pwd|secret|token|api[ _-]?key|apikey|api))"\s*(?<sep>[:=])\s*"(?!(?:[^"\r\n]*://))(?<val>[^"\r\n]{4,})"""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JsonPropertyCredentialRegex();

    /// <summary>HTTP bearer credential; the scheme word is preserved.</summary>
    [GeneratedRegex(
        """\b(?<scheme>[Bb]earer)\s+(?<token>[A-Za-z0-9._\-+/]{20,}={0,2})""",
        RegexOptions.CultureInvariant)]
    private static partial Regex BearerTokenRegex();

    /// <summary>
    /// PEM private-key block (RSA/EC/OPENSSH/PKCS#8). Matched whole so the
    /// base64 body cannot survive line by line.
    /// </summary>
    [GeneratedRegex(
        """-----BEGIN (?:[A-Z0-9 ]+ )?PRIVATE KEY-----[\s\S]*?-----END (?:[A-Z0-9 ]+ )?PRIVATE KEY-----""",
        RegexOptions.CultureInvariant)]
    private static partial Regex PemPrivateKeyRegex();

    /// <summary>
    /// Replace every credential-shaped run in <paramref name="text"/> with
    /// <paramref name="marker"/>, preserving the surrounding sentence (keyword,
    /// separator, quoting style, and the <c>Bearer</c> scheme word all survive
    /// so the text still reads).
    /// </summary>
    public static string Scrub(string text, string marker)
    {
        if (string.IsNullOrEmpty(text)) return text;

        return ScrubCore(text, marker, 0);
    }

    // ── {{secret:name}} references ──────────────────────────────

    [GeneratedRegex(SwarmCore.Secrets.SecretReferenceSyntax.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretReferenceRegex();

    // A protected reference while the patterns run: control characters no pattern's value class stops short of
    // and no credential shape contains, so a reference is never read as a keyword, separator or value.
    private const char ReferenceGuard = '\u0001';

    [GeneratedRegex("\u0001(?<index>\\d+)\u0001", RegexOptions.CultureInvariant)]
    private static partial Regex ProtectedReferenceRegex();


    /// <summary>
    /// Replaces each well-formed <c>{{secret:name}}</c> reference with spaces of the same length, so a detector
    /// that runs over the result never treats a reference (its <c>secret:</c> keyword or its name) as a credential
    /// and the offsets of everything else are unchanged.
    /// </summary>
    public static string BlankSecretReferences(string text)
        => string.IsNullOrEmpty(text) || !text.Contains("{{", StringComparison.Ordinal) ? text
            : SecretReferenceRegex().Replace(text, m => new string(' ', m.Length));

    /// <summary>True when the assignment value starting at <paramref name="valueStart"/> is a secret reference.</summary>
    private static bool AssignsSecretReference(string text, int valueStart)
    {
        var rest = text.AsSpan(valueStart).TrimStart().TrimStart("\"'").ToString();
        var match = SecretReferenceRegex().Match(rest);
        return match.Success && match.Index == 0;
    }

    /// <summary>A credential value with any protected references kept: only the literal text around them is redacted.</summary>
    private static string RedactValue(string value, string marker)
        => !value.Contains(ReferenceGuard) ? marker
            : string.Concat(ProtectedReferenceRegex().Split(value).Select((piece, i) =>
                // Split keeps the captured index at odd positions; rebuild those as the guard sequence.
                i % 2 == 1 ? $"{ReferenceGuard}{piece}{ReferenceGuard}" : piece.Trim().Length == 0 ? piece : marker));

    /// <summary>
    /// The value of a credential-named property: redacted whole, except that well-formed secret references in
    /// it are kept (a property that only holds a reference stays unchanged).
    /// </summary>
    private static string RedactPropertyValue(string value, string marker)
    {
        if (!value.Contains("{{", StringComparison.Ordinal) || value.Contains(ReferenceGuard)) return marker;
        var references = new List<string>();
        var protectedValue = SecretReferenceRegex().Replace(value, m =>
        {
            references.Add(m.Value);
            return $"{ReferenceGuard}{references.Count - 1}{ReferenceGuard}";
        });
        if (references.Count == 0) return marker;
        return ProtectedReferenceRegex().Replace(RedactValue(protectedValue, marker),
            m => references[int.Parse(m.Groups["index"].Value, System.Globalization.CultureInfo.InvariantCulture)]);
    }

    private static string ScrubCore(string text, string marker, int depth)
    {
        // Tool results and nested serialized receipts are data, not prose. Work on
        // decoded values and let the JSON codec own escaping at every layer.
        if (depth < 16 && TryScrubJson(text, marker, depth, out var structured)) return structured;

        // A {{secret:name}} reference names a vault entry and is never a credential: protect each one while the
        // patterns run so neither its keyword nor its name is redacted. (Text that already carries the guard
        // character is scrubbed unprotected rather than risk restoring the wrong span.)
        var references = new List<string>();
        var protect = text.Contains("{{", StringComparison.Ordinal) && !text.Contains(ReferenceGuard);
        var result = !protect ? text : SecretReferenceRegex().Replace(text, m =>
        {
            references.Add(m.Value);
            return $"{ReferenceGuard}{references.Count - 1}{ReferenceGuard}";
        });

        result = ScrubPatterns(result, marker);
        return references.Count == 0 ? result
            : ProtectedReferenceRegex().Replace(result, m => references[int.Parse(m.Groups["index"].Value, System.Globalization.CultureInfo.InvariantCulture)]);
    }

    private static string ScrubPatterns(string text, string marker)
    {
        // Whole-block shapes first: a PEM body would otherwise be nibbled at by
        // the token patterns and left partially intact.
        var result = PemPrivateKeyRegex().Replace(text, marker);

        // Quoted-property shapes next, so a pasted config section is handled
        // whole before the prose pattern sees its fragments.
        result = JsonPropertyCredentialRegex().Replace(result, match =>
        {
            if (!IsCredentialProperty(match.Groups["key"].Value)) return match.Value;
            var value = match.Groups["val"];
            var offset = value.Index - match.Index;
            // Replace only the value, never rebuild the surrounding property.
            var escapedMarker = JsonSerializer.Serialize(marker)[1..^1];
            return match.Value[..offset] + RedactValue(value.Value, escapedMarker) + match.Value[(offset + value.Length)..];
        });

        result = AssignedCredentialRegex().Replace(result, match =>
        {
            var key = match.Groups["key"].Value;
            var sep = match.Groups["sep"].Value;
            var value = match.Groups["dq"].Success ? match.Groups["dq"] : match.Groups["sq"].Success ? match.Groups["sq"] : match.Groups["bare"];
            // A value holding a protected secret reference keeps the reference (and the text around the value);
            // only the literal text beside it is redacted.
            if (value.Value.Contains(ReferenceGuard))
            {
                var start = value.Index - match.Index;
                return match.Value[..start] + RedactValue(value.Value, marker) + match.Value[(start + value.Length)..];
            }
            // Preserve the quoting style so the sentence still reads naturally.
            if (match.Groups["dq"].Success) return $"{key}{sep}\"{marker}\"";
            if (match.Groups["sq"].Success) return $"{key}{sep}'{marker}'";
            return $"{key}{sep}{marker}";
        });

        result = BearerTokenRegex().Replace(result, match => $"{match.Groups["scheme"].Value} {marker}");
        result = VendorTokenRegex().Replace(result, marker);

        return result;
    }

    private static bool IsCredentialProperty(string name)
    {
        var compact = Regex.Replace(name, "[ _-]", "").ToLowerInvariant();
        // A token is not necessarily an authentication credential (page cursors,
        // observation identities and token counters are ordinary protocol data).
        if (compact.EndsWith("token", StringComparison.Ordinal))
            return compact is not ("pagetoken" or "nextpagetoken" or "previouspagetoken"
                or "semanticpagetoken" or "observationtoken" or "cancellationtoken");
        return JsonPropertyCredentialRegex().IsMatch(JsonSerializer.Serialize(name) + ": \"placeholder\"");
    }

    private static bool TryScrubJson(string text, string marker, int depth, out string result)
    {
        result = text;
        var trimmed = text.AsSpan().TrimStart();
        if (trimmed.IsEmpty || trimmed[0] is not ('{' or '[' or '"')) return false;
        JsonDocument document;
        try { document = JsonDocument.Parse(text); }
        catch (JsonException) { return false; }
        using (document)
        using (var buffer = new MemoryStream())
        {
            var changed = false;
            using (var writer = new Utf8JsonWriter(buffer))
            {
                void Visit(JsonElement node, string? property = null)
                {
                    if (node.ValueKind == JsonValueKind.Object)
                    {
                        writer.WriteStartObject();
                        foreach (var entry in node.EnumerateObject())
                        {
                            writer.WritePropertyName(entry.Name);
                            Visit(entry.Value, entry.Name);
                        }
                        writer.WriteEndObject();
                    }
                    else if (node.ValueKind == JsonValueKind.Array)
                    {
                        writer.WriteStartArray();
                        foreach (var entry in node.EnumerateArray()) Visit(entry);
                        writer.WriteEndArray();
                    }
                    else if (node.ValueKind == JsonValueKind.String)
                    {
                        var original = node.GetString()!;
                        var scrubbed = property is not null && IsCredentialProperty(property)
                            && original.Length > 0 && !original.Contains("://", StringComparison.Ordinal)
                            ? RedactPropertyValue(original, marker) : ScrubCore(original, marker, depth + 1);
                        changed |= scrubbed != original;
                        writer.WriteStringValue(scrubbed);
                    }
                    else node.WriteTo(writer);
                }
                Visit(document.RootElement);
            }
            if (changed) result = Encoding.UTF8.GetString(buffer.ToArray());
        }
        return true;
    }

    // ── detection (reject-on-submit) ────────────────────────────

    /// <summary>
    /// Keyword + separator with no value requirement. Detection-only: a field
    /// submitted as <c>password=</c> is still a credential field even when the
    /// value is empty, and rejecting it costs nothing. Never used for scrubbing
    /// (there is nothing to replace).
    /// </summary>
    [GeneratedRegex(
        """\b(?:password|passwd|pwd|secret|api[ _-]?key|access[ _-]?key|client[ _-]?secret|token)\b\s*[:=]""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialAssignmentPrefixRegex();

    private static readonly Regex CreditCard = new(
        @"\b(?:\d[ -]*?){13,19}\b", RegexOptions.Compiled);

    private static readonly Regex Ssn = new(
        @"\b\d{3}-\d{2}-\d{4}\b", RegexOptions.Compiled);

    /// <summary>
    /// Describe what kind of credential <paramref name="value"/> looks like, or
    /// null when it looks like ordinary data. Card numbers must pass Luhn — a
    /// bare 16-digit run is far more often an order or account number.
    /// </summary>
    public static string? Describe(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        // A {{secret:name}} reference names a vault entry; it is never itself a credential.
        // An assignment whose value is such a reference ("token={{secret:x}}") assigns no credential either.
        var original = value;
        value = BlankSecretReferences(value);

        // Luhn is checked per matched run: the other digits in a submission (an expiry, a CVV,
        // an order or phone number) are not part of the card number.
        if (CreditCard.Matches(value).Any(run => PassesLuhn(run.Value)))
            return "payment card number";

        if (Ssn.IsMatch(value))
            return "SSN";

        if (VendorTokenRegex().IsMatch(value)
            || BearerTokenRegex().IsMatch(value)
            || PemPrivateKeyRegex().IsMatch(value)
            || CredentialAssignmentPrefixRegex().Matches(value).Any(m => !AssignsSecretReference(original, m.Index + m.Length)))
            return "API key or password";

        return null;
    }

    private static bool PassesLuhn(string input)
    {
        var digits = input.Where(char.IsAsciiDigit).Select(c => c - '0').ToArray();
        if (digits.Length is < 13 or > 19) return false;

        int sum = 0;
        bool doubleDigit = false;
        for (int i = digits.Length - 1; i >= 0; i--)
        {
            int d = digits[i];
            if (doubleDigit)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
            doubleDigit = !doubleDigit;
        }
        return sum % 10 == 0;
    }
}
