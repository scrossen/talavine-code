using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SwarmCore.Agent.Runtime;

namespace SwarmCore.Coding.Prompting;

/// <summary>
/// Injects repository instruction files (AGENTS.md, CLAUDE.md, ...) found from the
/// repository root upward (or the workspace's explicit instruction roots), plus a short
/// workspace header (branch, date). The repository is the turn's workspace: the one the
/// running coding turn selected, else the turn's <see cref="ContextInjectionRequest.WorkspaceRoot"/>,
/// else the host's <see cref="ICodingWorkspaceResolver"/> for the conversation. Without a
/// workspace it contributes nothing, so a host can list it for any profile.
/// </summary>
public sealed class RepoInstructionsInjector(
    IOptions<CodingOptions> options,
    IServiceScopeFactory? scopes = null,
    ActiveCodingWorkspaces? activeWorkspaces = null,
    TimeProvider? time = null,
    CodingCredentialIntakeContext? credentialIntake = null) : IContextInjector
{
    public const string InjectorName = "repoInstructions";

    public string Name => InjectorName;

    public async Task<string?> BuildAsync(ContextInjectionRequest request, CancellationToken ct = default)
    {
        var o = options.Value;
        var workspace = await ResolveWorkspaceAsync(request, ct).ConfigureAwait(false);
        var receipt = credentialIntake?.Get(request.ConversationId, request.ProfileId);
        if (workspace is null) return receipt?.Format();
        var root = workspace.FullRoot;
        var sb = new StringBuilder();
        sb.Append("## Workspace\n");
        sb.Append("- Root: ").Append(root).Append('\n');
        sb.Append("- Paths: use paths relative to this root; the file and git tools resolve them here. ")
          .Append("Any other directory you may see reported as your working directory (for example a model CLI's temporary folder) is not the project; never read or write project files there.\n");
        sb.Append("- Date: ").Append((time ?? TimeProvider.System).GetLocalNow().ToString("yyyy-MM-dd")).Append('\n');
        var branch = await TryGitBranchAsync(root, ct).ConfigureAwait(false);
        if (branch is not null) sb.Append("- Git branch: ").Append(branch).Append('\n');

        var files = workspace.InstructionRoots.Count > 0
            ? DiscoverInstructionFiles(workspace.InstructionRoots.Select(r => Path.GetFullPath(r, root)), o.InstructionFileNames)
            : DiscoverInstructionFiles(root, o.InstructionFileNames);
        if (files.Count > 0)
        {
            sb.Append("\n## Repository instructions\n");
            var loaded = new List<(string Relative, string Content)>();
            foreach (var file in files)
            {
                string content;
                try { content = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                loaded.Add((Path.GetRelativePath(root, file).Replace('\\', '/'), content));
            }
            // Emitted outermost first (the repository's own file lands last and wins), but budgeted innermost first:
            // a large file in a parent folder (home, drive root) must not crowd out the repository's own instructions.
            var budget = Math.Max(1000, o.MaxInstructionCharacters);
            var kept = new string?[loaded.Count];
            for (var i = loaded.Count - 1; i >= 0 && budget > 0; i--)
            {
                var content = loaded[i].Content;
                if (content.Length > budget)
                    content = content[..budget] + "\n[truncated]";
                kept[i] = content;
                budget -= content.Length;
            }
            for (var i = 0; i < loaded.Count; i++)
                if (kept[i] is { } content)
                    sb.Append("\n### ").Append(loaded[i].Relative).Append('\n').Append(content.TrimEnd()).Append('\n');
        }
        if (receipt is not null) sb.Append("\n\n").Append(receipt.Format());
        return sb.ToString();
    }

    private async Task<CodingWorkspace?> ResolveWorkspaceAsync(ContextInjectionRequest request, CancellationToken ct)
    {
        if (activeWorkspaces?.Get(request.ConversationId) is { } active) return active;
        if (!string.IsNullOrWhiteSpace(request.WorkspaceRoot)) return new CodingWorkspace { RepositoryRoot = request.WorkspaceRoot };
        if (scopes is null || !options.Value.InstructionsFromWorkspaceResolver) return null;
        using var scope = scopes.CreateScope();
        return scope.ServiceProvider.GetService<ICodingWorkspaceResolver>() is { } resolver
            ? await resolver.ResolveAsync(request.ConversationId, ct).ConfigureAwait(false)
            : null;
    }

    /// <summary>Outermost directory first so the repository root's file lands last and wins on conflicts.</summary>
    internal static List<string> DiscoverInstructionFiles(string root, IReadOnlyList<string> names)
    {
        var dir = new DirectoryInfo(root);
        var chain = new List<string>();
        while (dir is not null) { chain.Add(dir.FullName); dir = dir.Parent; }
        chain.Reverse();
        return DiscoverInstructionFiles(chain, names);
    }

    /// <summary>Instruction files in exactly the given directories, in the given order.</summary>
    internal static List<string> DiscoverInstructionFiles(IEnumerable<string> directories, IReadOnlyList<string> names)
    {
        var found = new List<string>();
        foreach (var d in directories)
            foreach (var name in names)
            {
                var path = Path.Combine(d, name);
                if (File.Exists(path) && !found.Contains(path, StringComparer.OrdinalIgnoreCase)) found.Add(path);
            }
        return found;
    }

    private static async Task<string?> TryGitBranchAsync(string root, CancellationToken ct)
    {
        try
        {
            var psi = Tools.GitProcess.CreateStartInfo(root, ["rev-parse", "--abbrev-ref", "HEAD"]);
            using var p = Process.Start(psi);
            if (p is null) return null;
            try { p.StandardInput.Close(); } catch { /* best effort */ }
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var output = await p.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return p.ExitCode == 0 ? output.Trim() : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }
}

/// <summary>
/// SwarmCode's injector list: its single coding profile gets the repository-instruction
/// injector. A host with its own profiles lists <see cref="RepoInstructionsInjector.InjectorName"/>
/// in its own <see cref="IInjectorListProvider"/> for the profiles that code.
/// </summary>
public sealed class CodingInjectorListProvider(IOptions<CodingOptions>? options = null) : IInjectorListProvider
{
    private static readonly string[] Names = [RepoInstructionsInjector.InjectorName];

    public CodingInjectorListProvider() : this(null) { }

    /// <summary>The repository instructions first, then <see cref="CodingOptions.AdditionalInjectorNames"/> in order.</summary>
    public IReadOnlyList<string> GetForProfile(Guid profileId)
    {
        var extra = options?.Value.AdditionalInjectorNames;
        if (extra is not { Count: > 0 }) return Names;
        return Names.Concat(extra.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
