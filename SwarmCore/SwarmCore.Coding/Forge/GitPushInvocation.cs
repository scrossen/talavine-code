using System.Text;
using System.Text.RegularExpressions;
using SwarmCore.Agent.Git;

namespace SwarmCore.Coding.Forge;

/// <summary>A push the forge tools perform: one local branch to one remote branch, never a plain force.</summary>
public sealed record GitPushRequest(
    string Remote,
    string LocalBranch,
    string RemoteBranch,
    bool SetUpstream,
    bool ForceWithLease,
    string? ExpectedRemoteSha = null);

/// <summary>
/// Builds the one host-owned git call that may reach the network. Every other host-owned git call keeps
/// <see cref="GitInvocation.HardeningConfig"/> unchanged (<c>credential.helper=</c>, <c>protocol.allow=never</c>).
/// <para>
/// A push keeps the rest of that hardening (no hooks, so no pre-push hook; no fsmonitor; no signing; no submodule
/// recursion) and the scrubbed environment, and replaces the two network settings with: <c>protocol.allow=never</c>
/// plus <c>protocol.https.allow=always</c> and <c>protocol.ssh.allow=always</c> (http, git, file, ext stay refused).
/// The credential goes through git's environment configuration (<c>GIT_CONFIG_COUNT</c>/<c>KEY_n</c>/<c>VALUE_n</c>),
/// never the command line: the helper list is reset, then either the signed-in gh CLI is the only helper
/// (<c>gh auth git-credential</c>) or a vault token is sent as an <c>Authorization</c> header scoped to the remote's
/// https origin. An ssh remote uses the person's own ssh keys and no helper. The push target is the named remote, after
/// <see cref="RefusedConfigQuery"/> has shown that no repository-scoped (or work-tree file) configuration can redirect
/// the transport or the credential.
/// </para>
/// </summary>
public static partial class GitPushInvocation
{
    /// <summary>The push hardening: the shared hardening without its two network lines, plus the push-only protocol policy.</summary>
    public static IReadOnlyList<string> HardeningConfig { get; } = BuildHardening(allowFileProtocol: false);

    private static IReadOnlyList<string> BuildHardening(bool allowFileProtocol)
    {
        var list = GitInvocation.HardeningConfig
            .Where(s => s is not "credential.helper=" and not "protocol.allow=never")
            .ToList();
        list.Add("protocol.allow=never");
        list.Add("protocol.https.allow=always");
        list.Add("protocol.ssh.allow=always");
        if (allowFileProtocol) list.Add("protocol.file.allow=always");
        list.Add("push.gpgSign=false");
        list.Add("push.recurseSubmodules=no");
        return list;
    }

    /// <summary>
    /// Repository configuration that could redirect a push or its credential: ssh/askpass/proxy commands, credential
    /// settings, http settings (proxy, CA, extra headers), URL rewriting, remote helpers and remote proxies. Listed with
    /// scope and origin; <see cref="RefusedKeys"/> keeps those the repository (or a file inside its work tree) defines.
    /// </summary>
    public const string RefusedConfigPattern =
        @"^(core\.(sshcommand|askpass|gitproxy)|credential\..*|http\..*|url\..*|remote\..*\.(proxy|proxyauthmethod|vcs|receivepack|uploadpack|serveroption)|ssh\..*)$";

    /// <summary>Arguments (after git) that list <see cref="RefusedConfigPattern"/> keys with scope and origin, NUL-separated.</summary>
    public static IReadOnlyList<string> RefusedConfigQuery { get; } =
        ["config", "--show-scope", "--show-origin", "--name-only", "-z", "--get-regexp", RefusedConfigPattern];

    /// <summary>
    /// The keys in <paramref name="queryOutput"/> (output of <see cref="RefusedConfigQuery"/>) that a repository controls:
    /// local, worktree or any non-global/system scope except the host's own command line, and any global/system entry
    /// from a file inside <paramref name="workTree"/>.
    /// </summary>
    public static IReadOnlyList<string> RefusedKeys(string queryOutput, string? workTree)
    {
        var refused = new List<string>();
        if (string.IsNullOrEmpty(queryOutput)) return refused;
        var fields = queryOutput.Split('\0');
        for (var i = 0; i + 2 < fields.Length; i += 3)
        {
            var scope = fields[i].Trim('\n', '\r');
            var origin = fields[i + 1];
            var name = fields[i + 2];
            if (name.Length == 0 || scope == "command") continue;
            var repositoryControlled = scope is not ("global" or "system")
                || workTree is not null && origin.StartsWith("file:", StringComparison.Ordinal)
                   && IsWithin(workTree, Path.GetFullPath(origin["file:".Length..], workTree));
            if (repositoryControlled && !refused.Contains(name, StringComparer.OrdinalIgnoreCase)) refused.Add(name);
        }
        return refused;
    }

    [GeneratedRegex(@"^[0-9a-fA-F]{7,64}$")]
    private static partial Regex ShaPattern();

    /// <summary>
    /// A branch name the tools accept: git ref-format rules that matter here plus no leading '-', no '+', ':' or
    /// whitespace (so it can never become an option, a force refspec or a second refspec).
    /// </summary>
    public static bool IsSafeBranchName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255) return false;
        if (name.StartsWith('-') || name.StartsWith('/') || name.EndsWith('/') || name.EndsWith('.') || name.EndsWith(".lock", StringComparison.Ordinal)) return false;
        if (name.Contains("..", StringComparison.Ordinal) || name.Contains("//", StringComparison.Ordinal) || name.Contains("@{", StringComparison.Ordinal) || name == "@") return false;
        foreach (var ch in name)
            if (char.IsControl(ch) || char.IsWhiteSpace(ch) || ch is '+' or ':' or '~' or '^' or '?' or '*' or '[' or '\\') return false;
        return name.Split('/').All(s => s.Length > 0 && !s.StartsWith('.'));
    }

    /// <summary>A remote name the tools accept (letters, digits, '.', '_', '-'; not starting with '-').</summary>
    public static bool IsSafeRemoteName(string? name)
        => !string.IsNullOrWhiteSpace(name) && name.Length <= 100 && !name.StartsWith('-') && !name.StartsWith('.')
           && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    /// <summary>Validates a request; null when it may run.</summary>
    public static string? Validate(GitPushRequest request)
    {
        if (!IsSafeRemoteName(request.Remote)) return "The remote name is not valid.";
        if (!IsSafeBranchName(request.LocalBranch)) return $"'{request.LocalBranch}' is not a valid local branch name.";
        if (!IsSafeBranchName(request.RemoteBranch)) return $"'{request.RemoteBranch}' is not a valid remote branch name.";
        if (request.ExpectedRemoteSha is { Length: > 0 } sha && (!request.ForceWithLease || !ShaPattern().IsMatch(sha)))
            return "expectedRemoteSha must be a commit sha and is used only with forceWithLease.";
        return null;
    }

    /// <summary>The arguments after the git executable. No credential is ever among them.</summary>
    public static IReadOnlyList<string> Arguments(GitPushRequest request, bool allowFileProtocol = false)
    {
        var list = new List<string> { "--no-pager" };
        foreach (var setting in allowFileProtocol ? BuildHardening(true) : HardeningConfig)
        {
            list.Add("-c");
            list.Add(setting);
        }
        list.Add("push");
        list.Add("--porcelain");
        list.Add("--no-verify");
        if (request.SetUpstream) list.Add("--set-upstream");
        if (request.ForceWithLease)
            list.Add("--force-with-lease=refs/heads/" + request.RemoteBranch
                     + (string.IsNullOrEmpty(request.ExpectedRemoteSha) ? "" : ":" + request.ExpectedRemoteSha));
        list.Add(request.Remote);
        list.Add("refs/heads/" + request.LocalBranch + ":refs/heads/" + request.RemoteBranch);
        return list;
    }

    /// <summary>
    /// The environment changes for the push: <see cref="GitInvocation.EnvironmentChanges"/> (drops inherited git
    /// redirection and every <c>GIT_CONFIG*</c>, no terminal prompt) plus the credential configuration as
    /// <c>GIT_CONFIG_COUNT</c>/<c>GIT_CONFIG_KEY_n</c>/<c>GIT_CONFIG_VALUE_n</c>.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> Environment(ForgeRepository repository, GitHubCredential? credential, IEnumerable<string>? inheritedNames = null)
    {
        var env = new Dictionary<string, string?>(GitInvocation.EnvironmentChanges(inheritedNames), StringComparer.OrdinalIgnoreCase);
        var config = new List<KeyValuePair<string, string>> { new("credential.helper", "") }; // reset every configured helper
        if (repository.Transport == ForgeTransport.Https && credential is not null)
        {
            if (!credential.Host.Equals(repository.Host, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The credential's host does not match the remote's host.");
            if (credential.Kind == GitHubCredentialKind.GhCli)
                config.Add(new("credential.helper", "!\"" + credential.GhPath!.Replace('\\', '/') + "\" auth git-credential"));
            else
            {
                var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:" + credential.Token));
                config.Add(new($"http.https://{repository.Host}/.extraheader", "Authorization: Basic " + basic));
            }
        }
        env["GIT_CONFIG_COUNT"] = config.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        for (var i = 0; i < config.Count; i++)
        {
            env["GIT_CONFIG_KEY_" + i] = config[i].Key;
            env["GIT_CONFIG_VALUE_" + i] = config[i].Value;
        }
        return env;
    }

    /// <summary>The full process spec for <paramref name="request"/> (validated first).</summary>
    public static ForgeProcessSpec Build(string gitExecutable, string workingDirectory, GitPushRequest request, ForgeRepository repository,
        GitHubCredential? credential, TimeSpan timeout, bool allowFileProtocol = false)
    {
        if (Validate(request) is { } error) throw new ArgumentException(error, nameof(request));
        return new ForgeProcessSpec(gitExecutable, Arguments(request, allowFileProtocol), workingDirectory,
            Environment(repository, credential), timeout);
    }

    /// <summary>One line of <c>git push --porcelain</c>: flag, source:destination, summary.</summary>
    public sealed record PushedRef(string Flag, string Source, string Destination, string Summary)
    {
        public bool Rejected => Flag == "!";
    }

    public static IReadOnlyList<PushedRef> ParsePorcelain(string stdout)
    {
        var refs = new List<PushedRef>();
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 3 || line[1] != '\t') continue;
            var parts = line[2..].Split('\t', 2);
            var refPair = parts[0].Split(':', 2);
            refs.Add(new PushedRef(line[0].ToString(), refPair[0], refPair.Length > 1 ? refPair[1] : "", parts.Length > 1 ? parts[1] : ""));
        }
        return refs;
    }

    private static bool IsWithin(string root, string path)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var r = Path.TrimEndingDirectorySeparator(root.Replace('/', Path.DirectorySeparatorChar));
        var p = Path.TrimEndingDirectorySeparator(path.Replace('/', Path.DirectorySeparatorChar));
        return string.Equals(r, p, comparison) || p.StartsWith(r + Path.DirectorySeparatorChar, comparison);
    }
}
