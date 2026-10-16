using Microsoft.Extensions.Options;
using SwarmCore.Agent.Git;
using SwarmCore.Coding.Tools;

namespace SwarmCore.Coding.Forge;

/// <summary>The repository a forge tool works on: remote name, parsed GitHub identity, the current branch, the work tree.</summary>
public sealed record ForgeRemote(string RemoteName, ForgeRepository Repository, ForgeRepository PushRepository, string? CurrentBranch, string? WorkTree);

/// <summary>A connected forge for a remote, or the reason it could not be connected.</summary>
public sealed record ForgeConnection(ForgeRemote? Remote, IGitHubForge? Forge, GitHubCredential? Credential, string? Error)
{
    public static ForgeConnection Fail(string error, ForgeRemote? remote = null) => new(remote, null, null, error);
}

/// <summary>What a push did. <see cref="Attempted"/> is false when it was refused before git started.</summary>
public sealed record GitPushOutcome(bool Succeeded, bool Attempted, string? Error, string? Summary, IReadOnlyList<GitPushInvocation.PushedRef> Refs, string? Credential);

/// <summary>
/// Shared plumbing for the forge tools (catalog tools and the native-lane bridge): reads the remote and branch with the
/// hardened git, resolves the credential, builds the REST forge, and runs the push.
/// </summary>
public sealed class GitHubWorkflow(
    IOptions<GitHubForgeOptions> options,
    GitHubCredentialResolver credentials,
    IGitHubApiTransportFactory transports,
    IForgeProcessRunner runner)
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Test seam: lets a test push to a local bare repository over the file protocol. Never set in a host.</summary>
    internal bool AllowFileProtocolForTests { get; init; }

    /// <summary>Resolves the remote (explicit, else the current branch's upstream remote, else origin) and parses its URLs.</summary>
    public async Task<(ForgeRemote? Remote, string? Error)> ReadRemoteAsync(string directory, string? remote, CancellationToken ct)
    {
        var branch = await GitAsync(directory, ["symbolic-ref", "--short", "-q", "HEAD"], ct).ConfigureAwait(false);
        var currentBranch = branch.ExitCode == 0 && branch.Stdout.Trim() is { Length: > 0 } b ? b : null;
        var top = await GitAsync(directory, ["rev-parse", "--show-toplevel"], ct).ConfigureAwait(false);
        if (top.ExitCode != 0) return (null, "Not a git repository: " + GitProcess.ErrorText(top.ExitCode, top.Stdout, top.Stderr));
        var workTree = Path.GetFullPath(top.Stdout.Trim());

        var name = remote?.Trim();
        if (string.IsNullOrEmpty(name) && currentBranch is not null)
        {
            var upstream = await GitAsync(directory, ["config", "--get", $"branch.{currentBranch}.remote"], ct).ConfigureAwait(false);
            if (upstream.ExitCode == 0 && upstream.Stdout.Trim() is { Length: > 0 } u && GitPushInvocation.IsSafeRemoteName(u)) name = u;
        }
        if (string.IsNullOrEmpty(name)) name = "origin";
        if (!GitPushInvocation.IsSafeRemoteName(name)) return (null, "The remote name is not valid.");

        var url = await GitAsync(directory, ["config", "--get", $"remote.{name}.url"], ct).ConfigureAwait(false);
        if (url.ExitCode != 0 || string.IsNullOrWhiteSpace(url.Stdout)) return (null, $"The repository has no remote named '{name}'.");
        if (!GitRemoteUrl.TryParse(url.Stdout.Trim(), out var repository, out var error))
            return (null, $"Remote '{name}': {error}");
        var pushRepository = repository!;
        var pushUrl = await GitAsync(directory, ["config", "--get", $"remote.{name}.pushurl"], ct).ConfigureAwait(false);
        if (pushUrl.ExitCode == 0 && pushUrl.Stdout.Trim() is { Length: > 0 } pu)
        {
            if (!GitRemoteUrl.TryParse(pu, out var parsedPush, out var pushError)) return (null, $"Remote '{name}' push URL: {pushError}");
            pushRepository = parsedPush!;
        }
        return (new ForgeRemote(name, repository!, pushRepository, currentBranch, workTree), null);
    }

    /// <summary>Reads the remote and connects a forge with the resolved credential.</summary>
    public async Task<ForgeConnection> ConnectAsync(string directory, string? remote, string? credential, CancellationToken ct)
    {
        var (forgeRemote, error) = await ReadRemoteAsync(directory, remote, ct).ConfigureAwait(false);
        if (forgeRemote is null) return ForgeConnection.Fail(error!);
        var resolved = await credentials.ResolveAsync(forgeRemote.Repository.Host, credential, ct).ConfigureAwait(false);
        if (resolved.Credential is null) return ForgeConnection.Fail(resolved.Error!, forgeRemote);
        return new ForgeConnection(forgeRemote, new GitHubForge(transports.Create(resolved.Credential, directory)), resolved.Credential, null);
    }

    /// <summary>Validates, refuses repository-controlled transport/credential configuration, resolves the credential and pushes.</summary>
    public async Task<GitPushOutcome> PushAsync(string directory, ForgeRemote remote, GitPushRequest request, string? credential, CancellationToken ct)
    {
        if (GitPushInvocation.Validate(request) is { } invalid) return Refused(invalid);
        var query = await GitAsync(directory, [.. GitPushInvocation.RefusedConfigQuery], ct).ConfigureAwait(false);
        if (query.ExitCode is not (0 or 1))
            return Refused("git could not list the repository's transport configuration, so nothing was pushed: " + GitProcess.ErrorText(query.ExitCode, query.Stdout, query.Stderr));
        var refused = GitPushInvocation.RefusedKeys(query.Stdout, remote.WorkTree);
        if (refused.Count > 0)
            return Refused("The repository's own git configuration sets " + string.Join(", ", refused)
                           + ", which could redirect a push or its credential, so nothing was pushed. Move these settings to your global git configuration or remove them, then push again.");

        GitHubCredential? resolved = null;
        if (remote.PushRepository.Transport == ForgeTransport.Https)
        {
            var resolution = await credentials.ResolveAsync(remote.PushRepository.Host, credential, ct).ConfigureAwait(false);
            if (resolution.Credential is null) return Refused(resolution.Error!);
            resolved = resolution.Credential;
        }

        string git;
        try { git = GitExecutable.Resolve(); }
        catch (GitNotRunnableException e) { return Refused(e.Message); }
        var timeout = TimeSpan.FromSeconds(Math.Clamp(options.Value.PushTimeoutSeconds, 10, 3600));
        var spec = GitPushInvocation.Build(git, directory, request, remote.PushRepository, resolved, timeout, AllowFileProtocolForTests);
        var result = await runner.RunAsync(spec, ct).ConfigureAwait(false);
        string Clean(string text) => GitRemoteUrl.RedactUserInfo(resolved?.Scrub(text) ?? text).Trim();
        var stdout = Clean(result.Stdout);
        var stderr = Clean(result.Stderr);
        var refs = GitPushInvocation.ParsePorcelain(stdout);
        var ok = result.ExitCode == 0 && refs.All(r => !r.Rejected);
        var summary = refs.Count > 0
            ? string.Join("; ", refs.Select(r => $"{r.Source} -> {r.Destination}: {(r.Summary.Length > 0 ? r.Summary : FlagText(r.Flag))}"))
            : null;
        return new GitPushOutcome(ok, result.ExitCode != -1, ok ? null : (stderr.Length > 0 ? stderr : stdout.Length > 0 ? stdout : $"git push exited with code {result.ExitCode}."),
            summary, refs, resolved?.Description ?? (remote.PushRepository.Transport == ForgeTransport.Ssh ? "ssh keys" : null));
    }

    private static GitPushOutcome Refused(string error) => new(false, false, error, null, [], null);

    private static string FlagText(string flag) => flag switch
    {
        "*" => "new branch",
        " " => "fast-forward",
        "+" => "forced update",
        "=" => "up to date",
        "!" => "rejected",
        "-" => "deleted",
        _ => "pushed",
    };

    private static async Task<(int ExitCode, string Stdout, string Stderr)> GitAsync(string directory, string[] args, CancellationToken ct)
        => await GitProcess.RunAsync(directory, args, GitTimeout, ct).ConfigureAwait(false);
}
