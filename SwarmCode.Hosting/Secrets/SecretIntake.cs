using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SwarmCore.Agent.InstructionChat;
using SwarmCore.Coding.Secrets;
using SwarmCore.Secrets;

namespace SwarmCode.Hosting.Secrets;

/// <summary>Result of running a typed message through <see cref="SecretIntake"/>.</summary>
public sealed record SecretIntakeOutcome(
    string Text,
    IReadOnlyList<string> StoredNames,
    bool HandledAsCommand,
    string? Message,
    string? PromptForValueName = null,
    string? PromptForValueSite = null)
{
    public static SecretIntakeOutcome PassThrough(string text) => new(text, [], false, null);
}

/// <summary>
/// Device-local pre-pass that lets the user type keys and passwords into the chat and
/// have them land in the vault instead of the model's context or the transcript.
/// Two paths, both free of model calls:
/// <list type="bullet">
/// <item><c>/secret &lt;name&gt; &lt;value&gt;</c>, <c>/secrets</c>, <c>/forget &lt;name&gt;</c> are commands the host
/// answers itself; the message is never sent to a lane.</item>
/// <item>Ordinary messages are scanned for credential shapes ("my GitHub token is ghp_...",
/// "openai api key: sk-..."). Each value is stored and replaced by <c>{{secret:name}}</c>
/// before the lane, the transcript store, or the session file sees it.</item>
/// </list>
/// The scan is tuned for a coding chat: code fragments such as <c>token = parser.Next()</c>
/// or <c>password = form["password"]</c> are left alone.
/// </summary>
public sealed partial class SecretIntake(ISecretVault vault, ILogger<SecretIntake>? logger = null, IEnumerable<CredentialIntakeBinding>? bindings = null)
{
    private readonly ILogger<SecretIntake> _logger = logger ?? NullLogger<SecretIntake>.Instance;

    [GeneratedRegex(@"^\s*/(?<cmd>secrets?|forget)(?:\s+(?<rest>.*))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex CommandPattern();


    public ISecretVault Vault => vault;

    /// <summary>True when the text is a vault command the host should answer without sending it anywhere.</summary>
    public static bool IsCommand(string? text) => !string.IsNullOrWhiteSpace(text) && CommandPattern().IsMatch(text);

    /// <summary>Runs a vault command. The returned message never contains a secret value.</summary>
    public async Task<SecretIntakeOutcome> HandleCommandAsync(string text, CancellationToken ct = default)
    {
        var m = CommandPattern().Match(text);
        if (!m.Success) return SecretIntakeOutcome.PassThrough(text);
        var cmd = m.Groups["cmd"].Value.ToLowerInvariant();
        var rest = m.Groups["rest"].Success ? m.Groups["rest"].Value.Trim() : "";

        if (cmd == "secrets")
        {
            var entries = await vault.ListAsync(ct).ConfigureAwait(false);
            var body = entries.Count == 0
                ? "No secrets stored yet. Save one with /secret <name> <value>, paste an API key into Settings, or import a password CSV."
                : string.Join("\n", entries.Select(Describe));
            return new SecretIntakeOutcome("", [], true, body);
        }

        if (cmd == "forget")
        {
            if (rest.Length == 0) return new SecretIntakeOutcome("", [], true, "Usage: /forget <name>");
            var removed = await vault.DeleteAsync(rest, ct).ConfigureAwait(false);
            return new SecretIntakeOutcome("", [], true, removed ? $"Forgot {SecretNames.Normalize(rest)}." : $"No secret named {rest}.");
        }

        // /secret [--site <host>] <name> [value]
        if (!TryTakeSite(ref rest, out var site, out var siteProblem))
            return new SecretIntakeOutcome("", [], true, siteProblem);
        if (rest.Length == 0)
            return new SecretIntakeOutcome("", [], true, SecretUsage);
        var split = rest.IndexOfAny([' ', '\t', '\n']);
        var rawName = split < 0 ? rest : rest[..split];
        var value = split < 0 ? "" : rest[(split + 1)..].Trim();
        if (!SecretNames.TryNormalize(rawName, out var name))
            return new SecretIntakeOutcome("", [], true, $"'{rawName}' is not a usable secret name. Use letters, digits, dots, dashes.");
        if (value.Length == 0)
        {
            // "/secret --site api.example.com name" on a saved secret sets where it may be sent.
            if (site is not null && await vault.TrySetSiteAsync(name, site, onlyWhenUnset: false, ct).ConfigureAwait(false))
                return new SecretIntakeOutcome("", [], true, $"{name} may now be sent to {site}. Its value was not changed.");
            return new SecretIntakeOutcome("", [], true, null, PromptForValueName: name, PromptForValueSite: site);
        }

        await vault.SetAsync(new SecretWriteRequest { Name = name, Value = value, Kind = GuessKind(name, value), Site = site }, ct).ConfigureAwait(false);
        return new SecretIntakeOutcome("", [name], true, SavedMessage(name, site));
    }

    private const string SecretUsage =
        "Usage: /secret [--site <host>] <name> <value>  (or /secret <name> to be prompted for the value). " +
        "--site names the only host the secret may be sent to; without it, the first host it is sent to is remembered.";

    /// <summary>The confirmation after a save; never contains the value.</summary>
    public static string SavedMessage(string name, string? site) =>
        $"Saved {name}. Reference it as {SecretReference.Placeholder(name)}; the value is never shown to the model. " +
        (site is null
            ? "It will be bound to the first host it is sent to."
            : $"It may be sent only to {site}.");

    /// <summary>Removes a leading <c>--site host</c> or <c>--site=host</c> from <paramref name="rest"/>.</summary>
    private static bool TryTakeSite(ref string rest, out string? site, out string? problem)
    {
        site = null; problem = null;
        if (!rest.StartsWith("--site", StringComparison.OrdinalIgnoreCase)) return true;
        var afterFlag = rest[6..];
        string raw;
        if (afterFlag.StartsWith('='))
        {
            var end = afterFlag.IndexOfAny([' ', '\t', '\n']);
            raw = end < 0 ? afterFlag[1..] : afterFlag[1..end];
            rest = end < 0 ? "" : afterFlag[end..].Trim();
        }
        else if (afterFlag.Length > 0 && char.IsWhiteSpace(afterFlag[0]))
        {
            var trimmed = afterFlag.TrimStart();
            var end = trimmed.IndexOfAny([' ', '\t', '\n']);
            raw = end < 0 ? trimmed : trimmed[..end];
            rest = end < 0 ? "" : trimmed[end..].Trim();
        }
        else
        {
            return true; // "--sitefoo" is a name, not the flag.
        }

        site = SecretNames.Host(raw);
        if (site is null) problem = $"'{raw}' is not a host. Use a host such as api.example.com.";
        return site is not null;
    }

    /// <summary>Stores a value a host collected out of band (hidden console input, password field).</summary>
    public async Task<SecretEntry> StoreAsync(string name, string value, string? kind = null, string? site = null, string? username = null, CancellationToken ct = default)
        => await vault.SetAsync(new SecretWriteRequest
        {
            Name = name,
            Value = value,
            Kind = kind ?? GuessKind(name, value),
            Site = site,
            Username = username,
        }, ct).ConfigureAwait(false);

    /// <summary>
    /// Scans an ordinary message for credential values, stores each in the vault, and returns
    /// the message with every stored value replaced by its <c>{{secret:name}}</c> reference.
    /// </summary>
    public async Task<SecretIntakeOutcome> ScrubAsync(string text, CancellationToken ct = default, bool overwriteExisting = true)
    {
        if (string.IsNullOrWhiteSpace(text)) return SecretIntakeOutcome.PassThrough(text);
        // Detection and storage are the shared SwarmCore step Talavine's coding turns also use.
        var result = await SecretMessageScrubber.ScrubAsync(vault, text,
            SecretScrubOptions.SwarmCodeIntake with { Bindings = bindings?.ToArray() ?? [], OverwriteExisting = overwriteExisting }, ct).ConfigureAwait(false);
        // Any detected credential is removed from the text, whether or not the vault stored it.
        if (!result.CredentialsDetected) return SecretIntakeOutcome.PassThrough(text);

        // SwarmCode fails closed: a credential the vault could not store stops the message (the runner refuses
        // to send, persist or display it). The exception text never carries the value.
        if (result.UnstoredValues.Count > 0)
        {
            _logger.LogWarning("{Count} typed credential(s) could not be stored in the vault; the message was not sent.", result.UnstoredValues.Count);
            throw new InvalidOperationException("A credential in the message could not be saved to the vault, so the message was not sent.");
        }

        var stored = result.StoredNames;
        var message = stored.Count == 1
            ? $"Saved {stored[0]} to the vault; the model sees {SecretReference.Placeholder(stored[0])} instead of the value."
            : $"Saved {stored.Count} secrets to the vault ({string.Join(", ", stored)}); the model sees {{{{secret:name}}}} references instead of the values.";
        return new SecretIntakeOutcome(result.Text, stored, false, message);
    }

    /// <summary>Shared detector rule (<see cref="CredentialShapeDetector.LooksLikeSecretValue"/>).</summary>
    internal static bool LooksLikeSecretValue(string value) => CredentialShapeDetector.LooksLikeSecretValue(value);

    internal static string RenameForCoding(string extractorName) => CredentialShapeDetector.RenameForVault(extractorName);

    internal static string GuessKind(string name, string value) => SecretMessageScrubber.GuessKind(name, value);

    /// <summary>Metadata line for one entry. Never includes the value.</summary>
    public static string Describe(SecretEntry e)
    {
        var parts = new List<string> { e.Name, $"[{e.Kind}]" };
        if (!string.IsNullOrEmpty(e.Site)) parts.Add(SecretNames.Host(e.Site) ?? e.Site);
        if (!string.IsNullOrEmpty(e.UsernameHint)) parts.Add("user " + e.UsernameHint);
        if (!e.IsEncrypted) parts.Add("(not encrypted: no OS keystore)");
        return string.Join("  ", parts);
    }
}
