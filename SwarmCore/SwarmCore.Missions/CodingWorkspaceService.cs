using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SwarmCore.Abstractions.CodingTasks;
using SwarmCore.Agent.Git;

namespace SwarmCore.Missions;

/// <summary>Deterministic, recoverable host setup. Never stages, commits, resets, or removes work.</summary>
public sealed class CodingWorkspaceService : ICodingWorkspaceService
{
    public async Task<CodingWorkspaceIdentity> PrepareAsync(CodingWorkspaceRequest request, CancellationToken ct = default)
    {
        if (request.ValidatePreparation is { } validateStart) await validateStart(ct);
        if (request.RunId == Guid.Empty) throw new ArgumentException("A persisted run identity is required.");
        // Package-virtualized storage gives Git and this host different views of the same path.
        if (CodingTaskWorkspaceRoots.IsUnderPackageCache(request.WorkspaceRoot)
            || Path.IsPathFullyQualified(request.WorkspaceRoot) && CodingTaskWorkspaceRoots.IsUnderPackageCache(Physical(request.WorkspaceRoot)))
            throw new InvalidOperationException(CodingTaskWorkspaceRoots.PackageCacheRefusal(request.WorkspaceRoot));
        var repo = Physical(request.RepositoryPath);
        await MissionPrimitiveFence.AuthorizeAsync(new(SwarmCore.Agent.Continual.MissionResourceKind.Directory, repo),
            SwarmCore.Agent.Continual.MissionEffect.Inspect, ct);
        var top = Physical((await GitAsync(repo, ["rev-parse", "--show-toplevel"], ct)).Trim());
        if (!Same(repo, top)) throw new ArgumentException("RepositoryPath must be the repository root.");
        if (Within(repo, Physical(request.WorkspaceRoot))) throw new ArgumentException("WorkspaceRoot must be a host-owned directory outside the source checkout.");
        var branch = "codex/coding-" + request.RunId.ToString("N");
        var path = request.UseExistingCheckout ? repo : Physical(Path.Combine(request.WorkspaceRoot, request.RunId.ToString("N")));
        if (request.AdmittedPaths.Any(p => Path.IsPathRooted(p) || p.Split(['/', '\\']).Contains("..") || !Within(path, Path.GetFullPath(Path.Combine(path, p)))))
            throw new ArgumentException("Admitted paths must stay inside the workspace.");
        var manifestPath = Path.Combine(request.WorkspaceRoot, request.RunId.ToString("N") + ".workspace.json");
        var persisted = request.ExistingIdentity;
        if (persisted is null && File.Exists(manifestPath))
            persisted = JsonSerializer.Deserialize<CodingWorkspaceIdentity>(await File.ReadAllTextAsync(manifestPath, ct))
                ?? throw new InvalidOperationException("Workspace manifest is invalid.");
        if (persisted is { } existing)
        {
            if (existing.RunId != request.RunId || !Same(existing.RepositoryPath, repo) || !Same(existing.WorkspacePath, path)
                || existing.UseExistingCheckout != request.UseExistingCheckout
                || !existing.AdmittedPaths.SequenceEqual(request.AdmittedPaths))
                throw new InvalidOperationException("Persisted workspace does not match the requested run.");
            await ValidateIdentityAsync(existing, ct);
            return existing;
        }
        var baseline = (await GitAsync(repo, ["rev-parse", "--verify", "--end-of-options", (request.BaselineRef ?? "HEAD") + "^{commit}"], ct)).Trim();
        var initialStatus = await GitAsync(repo, ["status", "--porcelain=v1", "-z", "--untracked-files=all"], ct);
        if (request.UseExistingCheckout)
        {
            if ((await GitAsync(repo, ["rev-parse", "HEAD"], ct)).Trim() != baseline)
                throw new InvalidOperationException("Existing checkout does not match the selected baseline.");
            branch = (await GitAsync(repo, ["symbolic-ref", "--quiet", "--short", "HEAD"], ct, allowFailure: true)).Trim();
        }
        else
        {
            // Git registration is host-owned setup, not model-executed repository content.
            if (request.ValidatePreparation is { } validateRegistration) await validateRegistration(ct);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (Directory.Exists(path))
            {
                // A prior setup may have completed before its identity CAS. Recover only this run's exact worktree.
                if ((await GitAsync(path, ["symbolic-ref", "--short", "HEAD"], ct)).Trim() != branch
                    || (await GitAsync(path, ["rev-parse", "HEAD"], ct)).Trim() != baseline)
                    throw new InvalidOperationException("Existing run directory is not the expected worktree.");
                await ValidateCommonDirectoryAsync(repo, path, ct);
            }
            else await GitAsync(repo, ["worktree", "add", "--no-checkout", "-b", branch, path, baseline], ct);
            // Both a recovered and a new worktree materialize the baseline; files already present must match it.
            await MaterializeBaselineAsync();

            async Task MaterializeBaselineAsync()
            {
                // Materialize raw Git objects. Checkout filters/hooks are repository code and
                // must not execute in this host-owned preparation boundary.
                var indexPath = (await GitAsync(path, ["rev-parse", "--path-format=absolute", "--git-path", "index"], ct)).Trim();
                if (!File.Exists(indexPath)) await GitAsync(path, ["read-tree", baseline], ct);
                var entries = (await GitAsync(path, ["ls-tree", "-r", "-z", baseline], ct)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                foreach (var entry in entries)
                {
                    if (request.ValidatePreparation is { } validateMaterialization) await validateMaterialization(ct);
                    var split = entry.IndexOf('\t'); var header = entry[..split].Split(' '); var relative = entry[(split + 1)..];
                    if (header[0] is not ("100644" or "100755"))
                        throw new InvalidOperationException("Workspace contains an unsupported link or submodule; setup remains available for inspection.");
                    var target = Path.GetFullPath(Path.Combine(path, relative));
                    if (!Within(path, target) || !Same(target, Physical(target))) throw new InvalidOperationException("Tracked path escapes workspace.");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    var contents = await GitBytesAsync(path, ["cat-file", "blob", header[2]], ct);
                    if (File.Exists(target))
                    {
                        if (!(await File.ReadAllBytesAsync(target, ct)).AsSpan().SequenceEqual(contents))
                            throw new InvalidOperationException("Incomplete setup contains changed files; preserved for inspection.");
                    }
                    else await File.WriteAllBytesAsync(target, contents, ct);
                    if (!OperatingSystem.IsWindows() && header[0] == "100755")
                        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
            }
        }
        var identity = new CodingWorkspaceIdentity(request.RunId, repo, path, branch, baseline, initialStatus, "")
        { UseExistingCheckout = request.UseExistingCheckout, AdmittedPaths = request.AdmittedPaths.ToArray() };
        var projectPaths = EnumerateFiles(path, [], new List<string>()).Where(file => Path.GetExtension(file) is ".csproj" or ".fsproj" or ".vbproj");
        identity = identity with { GeneratedOutputDirectories = projectPaths.SelectMany(file => new[]
            { Path.Combine(Path.GetDirectoryName(file) ?? "", "bin"), Path.Combine(Path.GetDirectoryName(file) ?? "", "obj") })
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() };
        if (request.ValidatePreparation is { } validateCompletion) await validateCompletion(ct);
        // Kernel file grants require an existing object. Create only explicitly admitted
        // new targets and host-owned generated directories, never a broad parent grant.
        foreach (var output in identity.GeneratedOutputDirectories)
        {
            var target = Path.Combine(path, output);
            if (request.AuthorizePreparationWrite is { } authorize) await authorize(target, true, ct);
            Directory.CreateDirectory(target);
        }
        foreach (var relative in identity.AdmittedPaths)
        {
            var target = Path.GetFullPath(Path.Combine(path, relative));
            if (!Same(target, Physical(target))) throw new InvalidOperationException("Admitted target changed its physical path.");
            if (relative.EndsWith('/') || relative.EndsWith('\\'))
            {
                if (!Directory.Exists(target))
                {
                    if (request.AuthorizePreparationWrite is { } authorize) await authorize(target, true, ct);
                    Directory.CreateDirectory(target);
                }
            }
            else if (!Path.Exists(target))
            {
                if (request.AuthorizePreparationWrite is { } authorize) await authorize(target, false, ct);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var created = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
        }
        var captured = await CaptureAsync(identity, ct);
        if (captured.BoundaryViolations.Count > 0) throw new InvalidOperationException("Workspace contains paths outside the admitted source boundary.");
        identity = identity with { BaselineTreeDigest = captured.TreeDigest, BaselineFiles = captured.FileDigests };
        Directory.CreateDirectory(request.WorkspaceRoot);
        var temporary = manifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(identity), ct);
        File.Move(temporary, manifestPath, overwrite: false);
        return identity;
    }

    public async Task<CodingWorkspaceCapture> CaptureAsync(CodingWorkspaceIdentity identity, CancellationToken ct = default)
    {
        await ValidateIdentityAsync(identity, ct);
        // The executor/verifier supplies the persisted host identity; it may be running
        // outside (or under a different parent) ambient tool turn. Public tools retain
        // their own current mission fence; this reader does not infer a new grant.
        var violations = new List<string>();
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var relative in EnumerateFiles(identity.WorkspacePath, identity.GeneratedOutputDirectories, violations))
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(Path.Combine(identity.WorkspacePath, relative));
            var digest = Convert.ToHexString(await HashFileAsync(path, ct));
            var mode = !OperatingSystem.IsWindows() ? ((int)File.GetUnixFileMode(path)).ToString() : "";
            files[relative] = mode + ":" + digest;
        }
        var changed = files.Keys.Union(identity.BaselineFiles.Keys, StringComparer.Ordinal)
            .Where(file => !files.TryGetValue(file, out var now) || !identity.BaselineFiles.TryGetValue(file, out var prior) || now != prior)
            .Order(StringComparer.Ordinal).ToArray();
        if (identity.BaselineTreeDigest.Length > 0 && identity.AdmittedPaths.Count > 0)
            violations.AddRange(changed.Where(file => !identity.AdmittedPaths.Any(p =>
                Within(Path.GetFullPath(Path.Combine(identity.WorkspacePath, p)), Path.GetFullPath(Path.Combine(identity.WorkspacePath, file))))));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (file, digest) in files)
            hash.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { file, digest }) + "\n"));
        var diff = changed.Length == 0 ? "" : await GitAsync(identity.WorkspacePath,
            ["diff", "--no-ext-diff", "--no-textconv", "--binary", identity.BaseCommit, "--", .. changed], ct);
        var tracked = (await GitAsync(identity.WorkspacePath, ["ls-files", "--cached", "-z"], ct)).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        foreach (var file in files.Keys.Where(file => changed.Contains(file, StringComparer.Ordinal) && !tracked.Contains(file)))
            diff += await GitAsync(identity.WorkspacePath, ["diff", "--no-ext-diff", "--no-textconv", "--no-index", "--binary", "--", "/dev/null", file], ct, allowFailure: true);
        return new(Convert.ToHexString(hash.GetHashAndReset()), diff, changed)
        { BoundaryViolations = violations.Distinct(StringComparer.Ordinal).ToArray(), FileDigests = files };
    }
    private static IReadOnlyList<string> EnumerateFiles(string root, IReadOnlyList<string> generated, List<string> violations)
    {
        var files = new List<string>(); var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
                if (relative == ".git") continue;
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0 || !Same(entry, Physical(entry)))
                { violations.Add(relative); continue; }
                if (generated.Any(output => Within(Path.GetFullPath(Path.Combine(root, output)), entry))) continue;
                if (Directory.Exists(entry)) pending.Push(entry); else files.Add(relative);
            }
        }
        return files.Order(StringComparer.Ordinal).ToArray();
    }
    private static async Task<byte[]> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        return await SHA256.HashDataAsync(stream, ct);
    }
    private static async Task ValidateIdentityAsync(CodingWorkspaceIdentity identity, CancellationToken ct)
    {
        if (!Same(identity.WorkspacePath, Physical(identity.WorkspacePath)) || !Same(identity.RepositoryPath, Physical(identity.RepositoryPath)))
            throw new InvalidOperationException("Workspace physical identity changed.");
        if (!Same(Physical((await GitAsync(identity.WorkspacePath, ["rev-parse", "--show-toplevel"], ct)).Trim()), identity.WorkspacePath))
            throw new InvalidOperationException("Workspace repository identity changed.");
        await ValidateCommonDirectoryAsync(identity.RepositoryPath, identity.WorkspacePath, ct);
    }
    private static async Task ValidateCommonDirectoryAsync(string repo, string work, CancellationToken ct)
    {
        var a = Physical((await GitAsync(repo, ["rev-parse", "--path-format=absolute", "--git-common-dir"], ct)).Trim());
        var b = Physical((await GitAsync(work, ["rev-parse", "--path-format=absolute", "--git-common-dir"], ct)).Trim());
        if (!Same(a, b)) throw new InvalidOperationException("Workspace belongs to another repository.");
    }
    private static string Physical(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Workspace paths must be absolute.");
        return SandboxPathResolver.ResolvePhysicalPath(path);
    }
    private static bool Same(string a, string b) => string.Equals(a.TrimEnd(Path.DirectorySeparatorChar), b.TrimEnd(Path.DirectorySeparatorChar),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static bool Within(string root, string path) => Same(root, path) || path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static async Task<string> GitAsync(string cwd, IReadOnlyList<string> arguments, CancellationToken ct, bool allowFailure = false)
        => Encoding.UTF8.GetString(await GitBytesAsync(cwd, arguments, ct, allowFailure));
    private static async Task<byte[]> GitBytesAsync(string cwd, IReadOnlyList<string> arguments, CancellationToken ct, bool allowFailure = false,
        bool noMatchIsSuccess = false)
    {
        ct.ThrowIfCancellationRequested();
        // Repository code (hooks, fsmonitor, filter/diff drivers the repository defines, signing) must not run in this
        // host-owned boundary: the shared builder resolves git, hardens the call and blanks the repository's drivers.
        IReadOnlyList<string> prefix = OperatingSystem.IsWindows() ? ["-c", "core.longpaths=true"] : [];
        IReadOnlyList<string>? drivers = null;
        if (GitInvocation.RunsRepositoryDrivers(arguments))
        {
            // Exit 1 means no driver is configured; any other failure fails the operation (fail closed).
            var listed = Encoding.UTF8.GetString(await GitBytesAsync(cwd, [.. prefix, .. GitInvocation.RepositoryDriverQuery], ct, allowFailure: false, noMatchIsSuccess: true));
            drivers = GitInvocation.RepositoryDriverOverrides(listed, cwd);
        }
        var start = GitInvocation.CreateStartInfo(cwd, [.. prefix, .. arguments], drivers);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Git could not start.");
        try { process.StandardInput.Close(); } catch { /* best effort */ }
        using var bytes = new MemoryStream();
        var output = process.StandardOutput.BaseStream.CopyToAsync(bytes); var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromMinutes(2), ct); }
        catch
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(output, error);
            throw;
        }
        await Task.WhenAll(output, error);
        if (process.ExitCode != 0 && !allowFailure && !(noMatchIsSuccess && process.ExitCode == 1))
        {
            var detail = (await error).Trim();
            if (detail.Length > 2_000) detail = detail[..2_000] + "…";
            throw new InvalidOperationException(
                "Git workspace operation failed (exit " + process.ExitCode + ")."
                + (detail.Length == 0 ? "" : " " + detail));
        }
        return bytes.ToArray();
    }
}
