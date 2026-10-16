using System.Text.RegularExpressions;

namespace SwarmCore.Coding.Forge;

/// <summary>How a remote is reached: https (credentials from gh or a vault token) or ssh (the person's own ssh keys).</summary>
public enum ForgeTransport { Https, Ssh }

/// <summary>
/// A GitHub (or GitHub Enterprise) repository identified from a git remote URL. Never carries user info: credentials
/// embedded in a remote URL are dropped by <see cref="GitRemoteUrl.TryParse"/>.
/// </summary>
public sealed record ForgeRepository(string Host, string Owner, string Name, ForgeTransport Transport)
{
    /// <summary>True for github.com (REST at api.github.com); any other host is treated as GitHub Enterprise Server.</summary>
    public bool IsGitHubDotCom => Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>REST base: <c>https://api.github.com/</c>, or <c>https://&lt;host&gt;/api/v3/</c> for Enterprise Server.</summary>
    public Uri ApiBaseUrl => IsGitHubDotCom ? new Uri("https://api.github.com/") : new Uri($"https://{Host}/api/v3/");

    /// <summary>The repository's web address, also its identity for mission endpoint authority.</summary>
    public string WebUrl => $"https://{Host}/{Owner}/{Name}";

    /// <summary><c>owner/name</c>.</summary>
    public string FullName => Owner + "/" + Name;

    /// <summary>REST path prefix for this repository (<c>repos/owner/name</c>).</summary>
    public string ApiPath => "repos/" + Uri.EscapeDataString(Owner) + "/" + Uri.EscapeDataString(Name);
}

/// <summary>Parses git remote URLs (https, ssh://, scp-like <c>git@host:owner/repo.git</c>) into a <see cref="ForgeRepository"/>.</summary>
public static partial class GitRemoteUrl
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex SegmentPattern();

    [GeneratedRegex(@"^(?:(?<user>[^@/\s:]+)@)?(?<host>[A-Za-z0-9.-]+):(?<path>(?!/)[^\s]+)$")]
    private static partial Regex ScpLikePattern();

    [GeneratedRegex(@"(?<scheme>[A-Za-z][A-Za-z0-9+.-]*://)[^/@\s]+@")]
    private static partial Regex UserInfoPattern();

    /// <summary>
    /// Parses <paramref name="url"/>. Accepted: <c>https://host[:port]/owner/repo[.git]</c>,
    /// <c>ssh://[user@]host[:port]/owner/repo[.git]</c> and <c>[user@]host:owner/repo[.git]</c>. Any other scheme
    /// (http, git, file, ext, a local path, a <c>transport::address</c> helper) is refused with a reason.
    /// </summary>
    public static bool TryParse(string? url, out ForgeRepository? repository, out string? error)
    {
        repository = null;
        error = null;
        var text = url?.Trim() ?? "";
        if (text.Length == 0) { error = "The remote has no URL."; return false; }
        if (text.Contains("::", StringComparison.Ordinal)) { error = "Remote helper URLs (transport::address) are not supported."; return false; }

        string host, path;
        ForgeTransport transport;
        if (text.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) { error = "The remote URL is not a valid URL."; return false; }
            switch (uri.Scheme.ToLowerInvariant())
            {
                case "https": transport = ForgeTransport.Https; break;
                case "ssh": case "git+ssh": case "ssh+git": transport = ForgeTransport.Ssh; break;
                default:
                    error = $"Remote scheme '{uri.Scheme}' is not supported; use an https or ssh remote.";
                    return false;
            }
            host = uri.Host;
            path = Uri.UnescapeDataString(uri.AbsolutePath);
        }
        else
        {
            var m = ScpLikePattern().Match(text);
            // A Windows drive path (C:\repo or C:/repo) is a local path, not host:path.
            if (!m.Success || m.Groups["host"].Value.Length == 1) { error = "The remote is a local path or an unrecognised URL; use an https or ssh remote."; return false; }
            transport = ForgeTransport.Ssh;
            host = m.Groups["host"].Value;
            path = m.Groups["path"].Value;
        }

        if (string.IsNullOrWhiteSpace(host)) { error = "The remote URL has no host."; return false; }
        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2) { error = "The remote URL does not name an owner/repository."; return false; }
        var owner = segments[0];
        var name = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? segments[1][..^4] : segments[1];
        if (!SegmentPattern().IsMatch(owner) || !SegmentPattern().IsMatch(name) || name is "." or "..")
        {
            error = "The remote URL's owner or repository name has unsupported characters.";
            return false;
        }
        repository = new ForgeRepository(host.ToLowerInvariant(), owner, name, transport);
        return true;
    }

    /// <summary>Replaces user info in any URL inside <paramref name="text"/> (<c>https://user:token@host</c> becomes <c>https://***@host</c>).</summary>
    public static string RedactUserInfo(string? text)
        => string.IsNullOrEmpty(text) ? text ?? "" : UserInfoPattern().Replace(text, m => m.Groups["scheme"].Value + "***@");
}
