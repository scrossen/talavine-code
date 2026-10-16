using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using SwarmCore.Secrets;

namespace SwarmCore.Coding.Forge;

/// <summary>Configuration section <c>Coding:GitHub</c>. Nothing here is user-specific; every value is optional.</summary>
public sealed class GitHubForgeOptions
{
    public const string SectionName = "Coding:GitHub";

    /// <summary>Use the signed-in <c>gh</c> CLI when it is installed and authenticated for the remote's host. Default true.</summary>
    public bool PreferGhCli { get; set; } = true;

    /// <summary>Absolute path of <c>gh</c>; otherwise PATH (and the usual Homebrew locations on macOS).</summary>
    public string? GhExecutable { get; set; }

    /// <summary>
    /// Vault secret name (a GitHub token) used when gh is not available. Applied to github.com and the hosts in
    /// <see cref="EnterpriseHosts"/> only, so a token is never sent to an unrelated remote host.
    /// </summary>
    public string? TokenSecret { get; set; }

    /// <summary>GitHub Enterprise Server hosts the configured <see cref="TokenSecret"/> may be used for.</summary>
    public List<string> EnterpriseHosts { get; set; } = [];

    /// <summary>Timeout for one gh or REST call, in seconds. Default 60.</summary>
    public int ApiTimeoutSeconds { get; set; } = 60;

    /// <summary>Timeout for git push, in seconds. Default 300.</summary>
    public int PushTimeoutSeconds { get; set; } = 300;
}

public enum GitHubCredentialKind { GhCli, VaultToken }

/// <summary>
/// The credential a forge call uses. The token value is internal and never serialized or printed:
/// <see cref="ToString"/> names only the source.
/// </summary>
public sealed class GitHubCredential
{
    private GitHubCredential(GitHubCredentialKind kind, string host, string? ghPath, string? secretName, string? token)
    {
        Kind = kind; Host = host; GhPath = ghPath; SecretName = secretName; Token = token;
    }

    public GitHubCredentialKind Kind { get; }
    public string Host { get; }
    public string? GhPath { get; }
    public string? SecretName { get; }
    internal string? Token { get; }

    public static GitHubCredential FromGh(string host, string ghPath) => new(GitHubCredentialKind.GhCli, host, ghPath, null, null);
    public static GitHubCredential FromVault(string host, string secretName, string token) => new(GitHubCredentialKind.VaultToken, host, null, secretName, token);

    /// <summary>What the model may see: "gh CLI" or the <c>{{secret:name}}</c> reference.</summary>
    public string Description => Kind == GitHubCredentialKind.GhCli ? "gh CLI (signed in)" : SecretReference.Placeholder(SecretName!);

    /// <summary>Removes the token (if any) from <paramref name="text"/>.</summary>
    public string Scrub(string? text)
        => Token is null ? text ?? "" : SecretReference.Scrub(text, [new KeyValuePair<string, string>(SecretName!, Token)]);

    public override string ToString() => $"GitHubCredential({Kind}, {Host}, {Description})";
}

/// <summary>A resolved credential, or why none could be used (a message safe for the model).</summary>
public sealed record GitHubCredentialResolution(GitHubCredential? Credential, string? Error);

/// <summary>Finds gh and tells whether it is signed in for a host. Replaced in tests.</summary>
public interface IGhCli
{
    /// <summary>The gh executable, or null when it is not installed.</summary>
    string? Locate();

    /// <summary>True when <c>gh auth status --hostname host</c> succeeds (no token is read or printed).</summary>
    Task<bool> IsSignedInAsync(string ghPath, string host, CancellationToken ct);
}

/// <summary>Default <see cref="IGhCli"/>: configured path, then PATH, then Homebrew locations; auth status cached five minutes.</summary>
public sealed class GhCli(IOptions<GitHubForgeOptions> options, IForgeProcessRunner runner) : IGhCli
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, (bool SignedIn, DateTime At)> _status = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Environment for every gh child: no prompts, no update check, no colour, no pager.</summary>
    public static IReadOnlyDictionary<string, string?> Environment { get; } = new Dictionary<string, string?>
    {
        ["GH_PROMPT_DISABLED"] = "1",
        ["GH_NO_UPDATE_NOTIFIER"] = "1",
        ["GH_SPINNER_DISABLED"] = "1",
        ["NO_COLOR"] = "1",
        ["GH_PAGER"] = "",
        ["PAGER"] = "",
    };

    public string? Locate()
    {
        var configured = options.Value.GhExecutable;
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.IsPathFullyQualified(configured) && File.Exists(configured) ? configured : null;
        var names = OperatingSystem.IsWindows() ? new[] { "gh.exe" } : ["gh"];
        foreach (var dir in (System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim().Trim('"'), name);
                    if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
            }
        }
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
            foreach (var p in new[] { "/opt/homebrew/bin/gh", "/usr/local/bin/gh" })
                if (File.Exists(p)) return p;
        return null;
    }

    public async Task<bool> IsSignedInAsync(string ghPath, string host, CancellationToken ct)
    {
        var key = ghPath + "|" + host;
        if (_status.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < CacheFor) return cached.SignedIn;
        var result = await runner.RunAsync(new ForgeProcessSpec(ghPath, ["auth", "status", "--hostname", host],
            System.Environment.CurrentDirectory, Environment, TimeSpan.FromSeconds(30)), ct).ConfigureAwait(false);
        var signedIn = result.ExitCode == 0;
        _status[key] = (signedIn, DateTime.UtcNow);
        return signedIn;
    }
}

/// <summary>
/// Chooses the credential for a remote host: an explicit <c>{{secret:name}}</c> from the tool input first, then the
/// signed-in gh CLI (<see cref="GitHubForgeOptions.PreferGhCli"/>), then the configured vault token
/// (<see cref="GitHubForgeOptions.TokenSecret"/>, github.com and listed Enterprise hosts only). A vault secret whose
/// Site names a different host is refused. Without any, the error says how to sign in; no token is ever returned
/// to the caller's output.
/// </summary>
public sealed class GitHubCredentialResolver(IOptions<GitHubForgeOptions> options, IGhCli gh, ISecretVault? vault = null)
{
    public async Task<GitHubCredentialResolution> ResolveAsync(string host, string? explicitCredential, CancellationToken ct)
    {
        var o = options.Value;
        if (!string.IsNullOrWhiteSpace(explicitCredential))
        {
            var names = SecretReference.Names(explicitCredential);
            string name;
            if (names.Count == 1) name = names[0];
            else if (names.Count == 0 && SecretNames.TryNormalize(explicitCredential.Trim(), out var plain)) name = plain;
            else return new(null, "credential must be one {{secret:name}} reference to a stored GitHub token.");
            return await FromVaultAsync(host, name, explicitlyNamed: true, ct).ConfigureAwait(false);
        }

        string? ghNote = null;
        if (o.PreferGhCli)
        {
            var path = gh.Locate();
            if (path is null) ghNote = "the gh CLI is not installed";
            else if (await gh.IsSignedInAsync(path, host, ct).ConfigureAwait(false)) return new(GitHubCredential.FromGh(host, path), null);
            else ghNote = $"gh is not signed in to {host}";
        }
        else ghNote = "gh use is turned off (Coding:GitHub:PreferGhCli=false)";

        if (!string.IsNullOrWhiteSpace(o.TokenSecret))
        {
            var allowed = host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                          || o.EnterpriseHosts.Any(h => h.Trim().Equals(host, StringComparison.OrdinalIgnoreCase));
            if (allowed) return await FromVaultAsync(host, o.TokenSecret.Trim(), explicitlyNamed: false, ct).ConfigureAwait(false);
            ghNote += $"; the configured token secret is not used for {host} (add it to Coding:GitHub:EnterpriseHosts)";
        }

        return new(null,
            $"No GitHub credentials for {host}: {ghNote}, and no token secret applies. Sign in with `gh auth login --hostname {host}`, "
            + "or store a token with /secret <name> and set Coding:GitHub:TokenSecret (or pass credential: \"{{secret:<name>}}\"). "
            + "Never ask the person to paste a token into the conversation.");
    }

    private async Task<GitHubCredentialResolution> FromVaultAsync(string host, string name, bool explicitlyNamed, CancellationToken ct)
    {
        if (vault is null) return new(null, "No credential vault is available in this host, so {{secret:" + name + "}} cannot be used.");
        var entry = await vault.GetEntryAsync(name, ct).ConfigureAwait(false);
        if (entry is null)
        {
            var stored = (await vault.ListAsync(ct).ConfigureAwait(false)).Select(e => e.Name).ToList();
            return new(null, $"No stored secret named '{name}'{(explicitlyNamed ? "" : " (Coding:GitHub:TokenSecret)")}. Stored names: "
                             + (stored.Count == 0 ? "(none)" : string.Join(", ", stored)) + ".");
        }
        if (SecretNames.Host(entry.Site) is { Length: > 0 } siteHost && !SiteMatches(siteHost, host))
            return new(null, $"The secret '{name}' is stored for {siteHost}, not {host}, so it was not sent.");
        var value = await vault.GetValueAsync(name, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(value)) return new(null, $"The secret '{name}' could not be read on this machine.");
        return new(GitHubCredential.FromVault(host, entry.Name, value.Trim()), null);
    }

    private static bool SiteMatches(string siteHost, string host)
        => siteHost.Equals(host, StringComparison.OrdinalIgnoreCase)
           // A token saved for api.github.com is the same credential as github.com.
           || host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && siteHost.Equals("api.github.com", StringComparison.OrdinalIgnoreCase);
}
