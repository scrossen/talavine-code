using SwarmCore.Agent.InstructionChat;
using SwarmCore.Secrets;
using System.Text.RegularExpressions;

namespace SwarmCore.Coding.Secrets;

/// <summary>Result of moving typed credentials into the vault.</summary>
/// <param name="Text">
/// The message with every detected credential removed: a stored value is replaced by its <c>{{secret:name}}</c>
/// reference, a value the vault could not store by <see cref="SecretMessageScrubber.RedactedPlaceholder"/>.
/// Hosts must forward this text whenever <see cref="CredentialsDetected"/> is true, whether or not storage succeeded.
/// </param>
/// <param name="StoredNames">Vault names written (or reused for an identical value), in replacement order.</param>
/// <param name="StoredValues">
/// The exact literals that were replaced by a reference, so a host can also register them with its own output
/// redaction. Hold only in memory; never log or persist them.
/// </param>
public sealed record SecretScrubResult(string Text, IReadOnlyList<string> StoredNames, IReadOnlyList<string> StoredValues)
{
    /// <summary>
    /// Detected literals the vault could not store (the write threw, returned no entry, or did not persist).
    /// They were replaced by <see cref="SecretMessageScrubber.RedactedPlaceholder"/>; register them with output
    /// redaction; a failed confirmation can follow a completed write, so its storage outcome remains unknown. Hold only in memory.
    /// </summary>
    public IReadOnlyList<string> UnstoredValues { get; init; } = [];

    /// <summary>True when <see cref="Text"/> differs from the input (any credential was replaced or redacted).</summary>
    public bool Changed { get; init; }

    /// <summary>Every detected literal, stored or not.</summary>
    public IReadOnlyList<string> DetectedValues => [.. StoredValues, .. UnstoredValues];

    /// <summary>At least one credential was detected; the caller must use <see cref="Text"/>, never the input.</summary>
    public bool CredentialsDetected => StoredValues.Count + UnstoredValues.Count > 0;
}

/// <summary>How <see cref="SecretMessageScrubber"/> detects and stores.</summary>
public sealed record SecretScrubOptions
{
    /// <summary>
    /// Governed default: strict detection (<see cref="CredentialShapeDetector.Detect"/>: known token
    /// prefixes and explicitly labeled credential-shaped values only) and never overwrite an existing
    /// vault entry (a new value gets a unique name such as <c>github-token-2</c>).
    /// </summary>
    public static SecretScrubOptions Governed { get; } = new();

    /// <summary>SwarmCode's original intake: lenient coding-chat detection, store by name (a newer value replaces the older).</summary>
    public static SecretScrubOptions SwarmCodeIntake { get; } = new() { Lenient = true, OverwriteExisting = true };

    public bool Lenient { get; init; }
    public bool OverwriteExisting { get; init; }
    /// <summary>Host-owned integration labels and the vault entry consumed by their tool.</summary>
    public IReadOnlyList<CredentialIntakeBinding> Bindings { get; init; } = [];
}

public sealed record CredentialIntakeBinding(string SecretName, IReadOnlyList<string> Labels);

/// <summary>
/// The shared intake step for coding hosts: detects credentials typed into a message, stores each in
/// the host's <see cref="ISecretVault"/>, and replaces it with a <c>{{secret:name}}</c> reference before
/// any lane, transcript, or session store sees the text. Talavine's coding turns use the governed
/// options; SwarmCode keeps its original intake options.
/// </summary>
public static class SecretMessageScrubber
{
    // Participating governed intake shares a namespace even across different host vault adapters.
    // This serializes allocation/write/readback/checkpoints in this process; intentional overwrite
    // intake and independent processes/direct vault writers retain their own semantics.
    private static readonly SemaphoreSlim GovernedIntake = new(1, 1);
    public static Task<SecretScrubResult> ScrubAsync(ISecretVault vault, string text, CancellationToken ct = default)
        => ScrubAsync(vault, text, SecretScrubOptions.Governed, ct);

    /// <summary>Text that replaces a detected credential the vault could not store.</summary>
    public const string RedactedPlaceholder = "[redacted]";

    public static async Task<SecretScrubResult> ScrubAsync(ISecretVault vault, string text, SecretScrubOptions options, CancellationToken ct = default,
        Func<int, string?, CancellationToken, Task>? observeItem = null)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(options);
        var detected = Detect(text, options);
        if (detected.Count == 0) return new(text, [], []);

        if (!options.OverwriteExisting) await GovernedIntake.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var names = new List<string>();
            var values = new List<string>();
            var unstored = new List<string>();
            var sanitized = text;
            for (var index = 0; index < detected.Count; index++)
            {
                var credential = detected[index];
                ct.ThrowIfCancellationRequested();
                // A failed write (throws, returns no entry, or does not persist) never leaves the value in the text.
                var stored = await TryStoreAsync(vault, credential.Name, credential.Value, options, ct).ConfigureAwait(false);
                if (observeItem is not null) await observeItem(index, stored, ct).ConfigureAwait(false);
                if (stored is null)
                {
                    unstored.Add(credential.Value);
                    sanitized = sanitized.Replace(credential.Value, RedactedPlaceholder, StringComparison.Ordinal);
                    continue;
                }
                names.Add(stored);
                values.Add(credential.Value);
                sanitized = sanitized.Replace(credential.Value, SecretReference.Placeholder(stored), StringComparison.Ordinal);
            }
            return new(sanitized, names, values) { UnstoredValues = unstored, Changed = !string.Equals(sanitized, text, StringComparison.Ordinal) };
        }
        finally { if (!options.OverwriteExisting) GovernedIntake.Release(); }
    }

    /// <summary>
    /// Redacts every detected credential without storing it (no vault on this host): each becomes
    /// <see cref="RedactedPlaceholder"/> and is reported in <see cref="SecretScrubResult.UnstoredValues"/>.
    /// </summary>
    public static SecretScrubResult Redact(string text, SecretScrubOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var detected = Detect(text, options);
        if (detected.Count == 0) return new(text, [], []);
        var sanitized = text;
        foreach (var credential in detected)
            sanitized = sanitized.Replace(credential.Value, RedactedPlaceholder, StringComparison.Ordinal);
        return new(sanitized, [], []) { UnstoredValues = detected.Select(c => c.Value).ToList(), Changed = !string.Equals(sanitized, text, StringComparison.Ordinal) };
    }

    private static IReadOnlyList<DetectedCredential> Detect(string text, SecretScrubOptions options)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var found = new List<DetectedCredential>();
        foreach (var binding in options.Bindings)
        {
            if (!CredentialIntakeReceipt.SafeName(binding.SecretName) || binding.Labels.Count == 0) continue;
            var labels = string.Join("|", binding.Labels.Where(l => !string.IsNullOrWhiteSpace(l)).Select(Regex.Escape));
            if (labels.Length == 0) continue;
            // Only a registered integration's explicit credential label, followed by a credential-shaped value.
            // Whitespace includes a pasted value on the next line; ordinary uses of "key" remain untouched.
            var pattern = "(?<![A-Za-z0-9_])(?:" + labels + ")\\s+(?:(?:api|access)[ _-]?)?(?:key|token)(?![A-Za-z0-9_])\\s*(?:is\\s*)?[:=]?\\s*[\\\"']?(?<value>[^\\s\\\"',;{}()\\[\\]]{8,300})";
            foreach (Match match in Regex.Matches(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            {
                var value = match.Groups["value"].Value.TrimEnd('.', '!', '?');
                if (CredentialShapeDetector.LooksLikeStrictCredentialValue(value) && !found.Any(c => c.Value == value))
                    found.Add(new(binding.SecretName, value));
            }
        }
        foreach (var credential in options.Lenient ? CredentialShapeDetector.DetectLenient(text) : CredentialShapeDetector.Detect(text))
            if (!found.Any(c => c.Value == credential.Value)) found.Add(credential);
        return found.OrderByDescending(c => c.Value.Length).ToList();
    }

    /// <returns>The vault name now holding the value, or null when it could not be stored.</returns>
    private static async Task<string?> TryStoreAsync(ISecretVault vault, string baseName, string value, SecretScrubOptions options, CancellationToken ct)
    {
        try
        {
            var name = options.OverwriteExisting
                ? baseName
                : await FreeNameAsync(vault, baseName, value, ct).ConfigureAwait(false);
            if (!options.OverwriteExisting && await vault.HasAsync(name, ct).ConfigureAwait(false))
                return CredentialIntakeReceipt.SafeName(name)
                    && string.Equals(await vault.GetValueAsync(name, ct).ConfigureAwait(false), value, StringComparison.Ordinal) ? name : null;
            var entry = await vault.SetAsync(new SecretWriteRequest
            {
                Name = name,
                Value = value,
                Kind = GuessKind(name, value),
            }, ct).ConfigureAwait(false);
            if (entry is null || entry.Name != name || !CredentialIntakeReceipt.SafeName(entry.Name)
                || entry.Name.Contains(value, StringComparison.OrdinalIgnoreCase)) return null;
            // A reference is only safe to hand out when the vault really holds the value under that name.
            return string.Equals(await vault.GetValueAsync(entry.Name, ct).ConfigureAwait(false), value, StringComparison.Ordinal) ? entry.Name : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The first of name, name-2, name-3 ... that is free or already holds exactly this value.</summary>
    private static async Task<string> FreeNameAsync(ISecretVault vault, string baseName, string value, CancellationToken ct)
    {
        for (var suffix = 1; suffix < 1000; suffix++)
        {
            var candidate = suffix == 1 ? baseName : $"{baseName}-{suffix}";
            if (!await vault.HasAsync(candidate, ct).ConfigureAwait(false)) return candidate;
            if (string.Equals(await vault.GetValueAsync(candidate, ct).ConfigureAwait(false), value, StringComparison.Ordinal)) return candidate;
        }
        return $"{baseName}-{Guid.NewGuid():N}";
    }

    public static string GuessKind(string name, string value)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("password") || n.Contains("passwd") || n.Contains("pwd")) return SecretKinds.Password;
        if (n.Contains("token") || value.StartsWith("ghp_", StringComparison.Ordinal) || value.StartsWith("xox", StringComparison.Ordinal) || value.StartsWith("eyJ", StringComparison.Ordinal)) return SecretKinds.Token;
        if (n.Contains("key") || value.StartsWith("sk-", StringComparison.Ordinal) || value.StartsWith("AKIA", StringComparison.Ordinal) || value.StartsWith("AIza", StringComparison.Ordinal)) return SecretKinds.ApiKey;
        return SecretKinds.Generic;
    }
}
