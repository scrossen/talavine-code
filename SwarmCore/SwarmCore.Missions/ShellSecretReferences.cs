using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using SwarmCore.Services.Abstractions;

namespace SwarmCore.Missions;

/// <summary>
/// Per-command <c>{{secret:name}}</c> resolution against the host's credential store: expands references at launch,
/// fails before launch when a name is missing (listing the stored names), and scrubs every resolved value from
/// captured output so a model can use a key it never sees. Shared by <see cref="ShellExecTool"/>, background shells and
/// <see cref="HttpRequestTool"/> headers, so every tool accepts the same reference spelling and gives the same guidance.
/// </summary>
public sealed partial class ShellSecretReferences(ICredentialStore? store)
{
    [GeneratedRegex(@"\{\{\s*secret\s*:\s*(?<name>[^}\s]+)\s*\}\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretReferencePattern();

    private readonly Dictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);
    // Reference spelling -> the stored name it resolved to (they differ after a relaxed match); host bindings are per stored name.
    private readonly Dictionary<string, string> _storedNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _missing = [];
    private bool _sawReference;

    public async Task<string?> ExpandAsync(string? text, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("{{", StringComparison.Ordinal)) return text;
        var matches = SecretReferencePattern().Matches(text);
        if (matches.Count == 0) return text;
        _sawReference = true;
        var sb = new StringBuilder(text.Length);
        var last = 0;
        foreach (Match m in matches)
        {
            var name = m.Groups["name"].Value;
            if (!_cache.TryGetValue(name, out var value))
            {
                value = store is null ? null : await store.GetAsync(name, ct);
                if (!string.IsNullOrEmpty(value)) _storedNames[name] = name;
                else if (store is not null) value = await TryRelaxedAsync(name, ct);
                _cache[name] = value;
                if (string.IsNullOrEmpty(value) && !_missing.Contains(name, StringComparer.OrdinalIgnoreCase)) _missing.Add(name);
            }
            sb.Append(text, last, m.Index - last);
            sb.Append(string.IsNullOrEmpty(value) ? m.Value : value);
            last = m.Index + m.Length;
        }
        sb.Append(text, last, text.Length - last);
        return sb.ToString();
    }

    /// <summary>Second chance ignoring case and the separators people swap (- _ .); only an unambiguous match resolves.</summary>
    private async Task<string?> TryRelaxedAsync(string name, CancellationToken ct)
    {
        var wanted = Canonical(name);
        if (wanted.Length == 0) return null;
        var candidates = (await store!.ListNamesAsync(ct)).Where(n => Canonical(n) == wanted).ToList();
        if (candidates.Count != 1) return null;
        var value = await store.GetAsync(candidates[0], ct);
        if (!string.IsNullOrEmpty(value)) _storedNames[name] = candidates[0];
        return value;
    }

    private static string Canonical(string name) => new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    public async Task ThrowIfMissingAsync(CancellationToken ct)
    {
        if (!_sawReference) return;
        if (store is null)
            throw new InvalidOperationException("This host has no credential vault, so {{secret:name}} references cannot be resolved. Retry without secret references.");
        if (_missing.Count == 0) return;
        var known = await store.ListNamesAsync(ct);
        // Host-neutral: each host's own instructions say how a person saves a credential there.
        var hint = known.Count == 0
            ? "No credentials are stored yet; ask the user to save the one you need to their credential vault (never to paste it into the chat)."
            : "Stored credential names: " + string.Join(", ", known.Take(50)) + ". Use one of these as {{secret:<that-name>}}, or ask the user to save the missing one to their credential vault.";
        throw new InvalidOperationException($"No stored credential named {string.Join(", ", _missing)}. {hint}");
    }

    /// <summary>
    /// Fails closed unless every credential resolved so far is bound to <paramref name="host"/> (or to a parent domain
    /// of it). A general-purpose request tool calls this before sending, in every kind of turn, so a credential can
    /// never be pointed at a host its owner did not bind it to. Shell commands are not checked here: their
    /// destination is not known before launch.
    /// </summary>
    public async Task ThrowIfNotBoundToHostAsync(string host, CancellationToken ct)
    {
        if (_storedNames.Count == 0 || store is null) return;
        var refused = new List<string>();
        foreach (var stored in _storedNames.Values.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var bound = await store.GetBoundHostsAsync(stored, ct);
            if (SwarmCore.Services.Credentials.CredentialHostBinding.Allows(bound, host)) continue;
            refused.Add(bound.Count == 0
                ? $"{stored} is not bound to any host"
                : $"{stored} is bound to {string.Join(", ", bound.Select(b => SwarmCore.Services.Credentials.CredentialHostBinding.HostOf(b) ?? b))}");
        }
        if (refused.Count == 0) return;
        throw new UnauthorizedAccessException(
            $"Not sent: a credential may only go to the host it is bound to, and '{host}' is not one ({string.Join("; ", refused)}). " +
            "Bind the credential to this host first: register the API as a service whose base URL is on this host and that uses the " +
            "credential, or have the user set the credential's site in their credential vault.");
    }

    /// <summary>Replaces every resolved value in captured output with its reference so the model never sees it.</summary>
    public string Scrub(string text)
    {
        if (text.Length == 0) return text;
        foreach (var (name, value) in _cache.Where(p => !string.IsNullOrEmpty(p.Value) && p.Value!.Length >= 4).OrderByDescending(p => p.Value!.Length))
            text = text.Replace(value!, "{{secret:" + name + "}}", StringComparison.Ordinal);
        return text;
    }
}

/// <summary>The platform shell a command string runs in: <c>cmd.exe /c</c> on Windows, <c>/bin/sh -c</c> elsewhere.</summary>
public static class PlatformShell
{
    /// <summary>Sets <paramref name="start"/>'s executable and arguments to run <paramref name="command"/> through the platform shell.</summary>
    public static void SetCommand(ProcessStartInfo start, string command)
    {
        if (OperatingSystem.IsWindows())
        {
            start.FileName = "cmd.exe";
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add(command);
        }
        else
        {
            start.FileName = "/bin/sh";
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(command);
        }
    }

    /// <summary>
    /// For a direct (non-mission) launch on Windows, hands the command to cmd.exe verbatim as <c>/s /c "command"</c>.
    /// Through <see cref="ProcessStartInfo.ArgumentList"/> .NET escapes inner quotes as <c>\"</c>, which cmd.exe does not
    /// understand, so <c>"C:\Program Files\tool.exe" --flag</c> or <c>echo "a b"</c> would break. The mission launcher
    /// builds the same <c>/s /c</c> line from the argument list itself, so call this only outside mission containment.
    /// </summary>
    public static void UseVerbatimCommandLine(ProcessStartInfo start)
    {
        if (!OperatingSystem.IsWindows() || start.ArgumentList is not ["/c", var command]) return;
        start.ArgumentList.Clear();
        start.Arguments = "/s /c \"" + command + "\"";
    }

    /// <summary>
    /// The shell selects its own SDK. A parent launched by another SDK (for example .NET 11 tests invoking the PATH
    /// .NET 10 CLI) must not force that child's MSBuild tasks to load from the parent's SDK.
    /// </summary>
    public static void RemoveParentSdkOverrides(ProcessStartInfo start)
    {
        start.Environment.Remove("MSBuildSDKsPath");
        start.Environment.Remove("MSBuildExtensionsPath");
    }
}
