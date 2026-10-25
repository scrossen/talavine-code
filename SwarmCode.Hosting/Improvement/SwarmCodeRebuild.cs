using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SwarmCore.Coding.SelfImprovement;

namespace SwarmCode.Hosting.Improvement;

/// <summary>What "build and relaunch" would do from where SwarmCode is running, or why it cannot.</summary>
public sealed record RebuildPlan(
    bool Possible,
    string Message,
    string? RepositoryRoot,
    string? ExecutablePath,
    string Layout,
    string BuildCommand,
    IReadOnlyList<string> BuildArguments)
{
    public string Describe() => Possible ? BuildCommand + " " + string.Join(' ', BuildArguments.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a)) : Message;
    public string? InstallBundlePath { get; init; }
    public string? StagingArtifactsPath { get; init; }
}

/// <summary>One verified item's merge into the source checkout.</summary>
public sealed record MergeOutcome(SelfImprovementItem Item, bool Merged, string Message);

/// <summary>Progress of the last build-and-relaunch, written by the helper script beside the backlog.</summary>
public sealed record RelaunchStatus(DateTime StartedUtc, IReadOnlyList<string> Merged, string Command, int? BuildExitCode, DateTime? FinishedUtc, string LogPath)
{
    public bool InProgress => BuildExitCode is null && FinishedUtc is null;
    public IReadOnlyList<InstalledRepairBinding> RepairedItems { get; init; } = [];
    public string? ArtifactRoot { get; init; }
    public IReadOnlyDictionary<string, string>? ArtifactHashes { get; init; }
    public int? ExitedProcessId { get; init; }
    public string? AttemptId { get; init; }
    public bool AutomaticGoalRepair { get; init; }
    public string? ProfileRoot { get; init; }
    public string? SourceRoot { get; init; }
    public IReadOnlyDictionary<string, string>? PreviousArtifactHashes { get; init; }
    public DateTime? FallbackVerifiedAtUtc { get; init; }
    public string? FailurePhase { get; init; }
    public DateTime? StartupAcknowledgedAtUtc { get; init; }
    public int? StartedProcessId { get; init; }
}

public sealed record InstalledRepairBinding(string Id, string Commit, string EvidenceDigest)
{
    public long ItemRevision { get; init; }
    public DateTime? LastAttemptUtc { get; init; }
    public GoalRepairLink? GoalRepair { get; init; }
}

/// <summary>
/// Closes the loop for a developer: merges the verified fixes into the source checkout, then hands off to a small
/// detached platform helper that waits for this process to exit, rebuilds the Debug app into the folder it is running
/// from (the same command the VS Code task uses, or the repository's Windows build script for the <c>.build</c>
/// layout) and starts the new build. The app itself only merges, writes the helper, starts it and quits.
/// </summary>
public sealed class SwarmCodeRebuild(SwarmCodeDataPaths paths, Func<SelfImprovementOptions> options, ISelfImprovementBacklog backlog,
    ILogger? logger = null, string? executablePath = null, string? baseDirectory = null)
{
    public const string ScriptName = "relaunch.ps1";
    public const string MacScriptName = "relaunch.sh";
    public const string StatusName = "relaunch.json";
    public const string LogName = "relaunch.log";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private InstalledRepairBinding[] _mergedBindings = [];
    private bool _automaticSelection;
    private bool _automaticGoalRepair;

    private string ExecutablePath => executablePath ?? Environment.ProcessPath ?? "";
    private string BaseDirectory => baseDirectory ?? AppContext.BaseDirectory;

    public string ScriptPath => Path.Combine(paths.SelfImprovementDirectory, ScriptName);
    public string StatusPath => Path.Combine(paths.SelfImprovementDirectory, StatusName);
    public string LogPath => Path.Combine(paths.SelfImprovementDirectory, LogName);

    /// <summary>Works out whether a rebuild from here is possible and which command does it. Never throws.</summary>
    public RebuildPlan Plan(bool isWindows = true)
    {
        var repo = SwarmCodeSourceRepository.Resolve(options().RepositoryRoot, BaseDirectory);
        if (repo is null) return Impossible("The Talavine Code source repository was not found, so there is nothing to rebuild from.");
        var exe = ExecutablePath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return Impossible("The running executable could not be located.");
        var baseDir = Path.GetFullPath(BaseDirectory);
        if (!isWindows)
        {
            var bundle = Path.Combine(repo, "SwarmCode.App", "bin", "Debug", "net11.0-macos", "osx-arm64", "Talavine Code.app");
            var expected = Path.Combine(bundle, "Contents", "MacOS", "SwarmCode.App");
            var build = Path.Combine(repo, "scripts", "build-swarmcode-macos.sh");
            if (!File.Exists(build)) return Impossible("The native macOS build script is missing from this source checkout.");
            if (!string.Equals(Path.GetFullPath(exe), expected, StringComparison.Ordinal)
                || !IsUnder(baseDir, Path.Combine(bundle, "Contents")))
            {
                var macOs = Path.GetDirectoryName(Path.GetFullPath(exe));
                var contents = macOs is null ? null : Path.GetDirectoryName(macOs);
                var installed = contents is null ? null : Path.GetDirectoryName(contents);
                if (string.IsNullOrWhiteSpace(options().RepositoryRoot) || !SwarmCodeSourceRepository.IsSwarmCodeRepository(repo)
                    || Path.GetFileName(exe) != "SwarmCode.App" || Path.GetFileName(macOs) != "MacOS"
                    || Path.GetFileName(contents) != "Contents" || installed is null || !installed.EndsWith(".app", StringComparison.Ordinal)
                    || !File.Exists(Path.Combine(contents!, "Info.plist")) || !IsUnder(baseDir, contents!)
                    || new DirectoryInfo(installed).LinkTarget is not null)
                    return Impossible("Build and relaunch requires the native macOS Debug bundle or an installed native bundle with an explicitly configured source checkout.");
                return new(true, "Builds and verifies an isolated native macOS artifact, backs up this installed bundle, replaces it after exit and reopens the same profile.",
                    repo, exe, "macos-appkit", "/bin/bash", [build])
                {
                    InstallBundlePath = installed,
                    StagingArtifactsPath = Path.Combine(paths.SelfImprovementDirectory, "artifacts", Guid.NewGuid().ToString("N")),
                };
            }
            return new(true, "Rebuilds, signs and verifies the native macOS app, then reopens it with the same profile.",
                repo, exe, "macos-appkit", "/bin/bash", [build]);
        }
        if (!IsUnder(baseDir, repo))
            return Impossible($"Talavine Code is running from {baseDir}, outside the source checkout {repo}, so a rebuild would not replace this build.");
        var rid = baseDir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).FirstOrDefault(s => s.StartsWith("win-", StringComparison.OrdinalIgnoreCase)) ?? "win-x64";

        if (IsUnder(baseDir, Path.Combine(repo, "SwarmCode.App", "bin")))
            return new RebuildPlan(true, "Rebuilds with dotnet build, the same command the VS Code \"Talavine Code App (Windows)\" task uses.", repo, exe, "bin", "dotnet",
            [
                "build", Path.Combine(repo, "SwarmCode.App", "SwarmCode.App.csproj"), "-f", "net11.0-windows10.0.19041.0", "-c", "Debug",
                "/property:GenerateFullPaths=true", "/property:UseXamlPreCompilation=false", "/property:XamlPreCompilationEnabled=false", "/property:MauiEnableXamlCTask=false",
                "/consoleloggerparameters:NoSummary",
            ]);
        if (IsUnder(baseDir, Path.Combine(repo, ".build")))
            return new RebuildPlan(true, "Rebuilds with scripts/Build-TalavineWindowsDebug.ps1 (native MSBuild) into the .build folder this build runs from.", repo, exe, "script", "pwsh",
                ["-NoProfile", "-File", Path.Combine(repo, "scripts", "Build-TalavineWindowsDebug.ps1"), "-App", "SwarmCode", "-RuntimeIdentifier", rid]);
        return Impossible($"Talavine Code is running from {baseDir}, which is not a known build output of the checkout (SwarmCode.App/bin or .build).");

        static RebuildPlan Impossible(string why) => new(false, why, null, null, "none", "", []);
    }

    /// <summary>Verified items whose branch still exists and can be shipped.</summary>
    public async Task<IReadOnlyList<SelfImprovementItem>> ShippableAsync(CancellationToken ct = default)
        => (await backlog.ListAsync(ct).ConfigureAwait(false)).Where(i => i.Stage == SelfImprovementStage.Verified && !string.IsNullOrWhiteSpace(i.Branch)).OrderBy(i => i.LastAttemptUtc).ToList();

    /// <summary>
    /// Merges every shippable item's branch into the checkout, one after another. A merge git refuses (uncommitted
    /// changes in its way, a conflict) leaves that item Verified with the reason in its note and the checkout as it was;
    /// a merged item becomes Merged and its worktree is removed when clean.
    /// </summary>
    public async Task<IReadOnlyList<MergeOutcome>> MergeVerifiedAsync(string repositoryRoot, CancellationToken ct = default,
        IReadOnlySet<string>? itemIds = null)
    {
        _automaticSelection = itemIds is not null;
        var outcomes = new List<MergeOutcome>();
        var candidates = itemIds is null ? await ShippableAsync(ct).ConfigureAwait(false)
            : (await backlog.ListAsync(ct).ConfigureAwait(false)).Where(i => itemIds.Contains(i.Id)
                && i.Stage is SelfImprovementStage.Verified or SelfImprovementStage.Merged).ToList();
        foreach (var item in candidates)
        {
            if (itemIds is not null && !itemIds.Contains(item.Id)) continue;
            ct.ThrowIfCancellationRequested();
            if (itemIds is not null && await SelfImprovementGit.HasChangesAsync(repositoryRoot, ct).ConfigureAwait(false))
            {
                outcomes.Add(new(item, false, "Installation postponed: the source checkout has uncommitted work."));
                continue;
            }
            if (item.Stage == SelfImprovementStage.Merged)
            {
                var present = item.Commit is not null && item.Commit.Equals(await SelfImprovementGit.HeadAsync(repositoryRoot, ct).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase);
                outcomes.Add(new(item, present, present ? "Verified repair is already merged; continuing its installation." : "The merged repair is not in the current source checkout."));
                continue;
            }
            var (merged, message) = itemIds is null
                ? await SelfImprovementGit.MergeAsync(repositoryRoot, item.Branch!, "Merge " + item.Branch + ": " + item.Title, ct).ConfigureAwait(false)
                : await SelfImprovementGit.TryApplyVerifiedAsync(repositoryRoot, item.Branch!, item.Commit ?? "", ct).ConfigureAwait(false);
            if (merged && item.WorktreePath is { } worktree)
            {
                var problem = await SelfImprovementGit.RemoveWorktreeAsync(repositoryRoot, worktree, item.Branch!, force: false, ct).ConfigureAwait(false);
                if (problem is not null) { message += " Worktree kept: " + problem; logger?.LogInformation("Kept worktree {Path}: {Problem}", worktree, problem); }
            }
            await backlog.UpdateAsync(item.Id, fresh => merged
                ? fresh with { Stage = SelfImprovementStage.Merged, Note = "Merged into the source checkout. " + message, WorktreePath = null }
                : fresh with { Note = message + " Merge it yourself, or resolve and retry." }, ct: ct).ConfigureAwait(false);
            outcomes.Add(new MergeOutcome(item, merged, message));
        }
        var bindings = new List<InstalledRepairBinding>();
        foreach (var outcome in outcomes.Where(o => o.Merged && o.Item.Commit is not null && !string.IsNullOrWhiteSpace(o.Item.Evidence)))
        {
            var current = await backlog.GetAsync(outcome.Item.Id, ct).ConfigureAwait(false);
            if (current is { Stage: SelfImprovementStage.Merged } && current.Commit == outcome.Item.Commit
                && RelaunchInstalledFixVerifier.Binding(current).EvidenceDigest == RelaunchInstalledFixVerifier.Binding(outcome.Item).EvidenceDigest)
                bindings.Add(RelaunchInstalledFixVerifier.Binding(current));
        }
        _mergedBindings = bindings.ToArray();
        _automaticGoalRepair = _automaticSelection && bindings.Count > 0 && bindings.All(b => b.GoalRepair is not null);
        return outcomes;
    }

    /// <summary>
    /// Writes the detached helper: wait for this process, build, record the result, start the executable again. The
    /// build's output goes to <see cref="LogPath"/>; <see cref="StatusPath"/> says how it went.
    /// </summary>
    public string WriteRelaunchScript(RebuildPlan plan, int processId, IReadOnlyList<string> merged, int waitSeconds = 180)
    {
        if (!plan.Possible || plan.RepositoryRoot is null || plan.ExecutablePath is null) throw new InvalidOperationException(plan.Message);
        if (processId < 1) throw new ArgumentOutOfRangeException(nameof(processId));
        if (waitSeconds < 1 || waitSeconds > 180) throw new ArgumentOutOfRangeException(nameof(waitSeconds));
        Directory.CreateDirectory(paths.SelfImprovementDirectory);
        var started = DateTime.UtcNow;
        var artifactRoot = plan.Layout == "macos-appkit"
            ? Path.GetDirectoryName(Path.GetDirectoryName(plan.ExecutablePath))! : Path.GetDirectoryName(plan.ExecutablePath)!;
        File.WriteAllText(StatusPath, JsonSerializer.Serialize(new RelaunchStatus(started, merged, plan.Describe(), null, null, LogPath)
        {
            RepairedItems = _mergedBindings, ArtifactRoot = artifactRoot, ExitedProcessId = processId,
            AttemptId = Guid.NewGuid().ToString("N"), ProfileRoot = Path.GetFullPath(paths.RootDirectory), SourceRoot = Path.GetFullPath(plan.RepositoryRoot),
            AutomaticGoalRepair = _automaticGoalRepair && plan.InstallBundlePath is not null,
            PreviousArtifactHashes = _automaticGoalRepair && plan.InstallBundlePath is not null
                ? InstalledArtifactSnapshot.Capture(artifactRoot) : null,
        }, Json));
        if (plan.Layout == "macos-appkit") return WriteMacRelaunchScript(plan, processId, waitSeconds);
        var exeDir = Path.GetDirectoryName(plan.ExecutablePath) ?? plan.RepositoryRoot;
        var script = new StringBuilder();
        script.AppendLine("$ErrorActionPreference = 'Continue'");
        script.AppendLine($"$log = {Quote(LogPath)}");
        script.AppendLine($"$status = {Quote(StatusPath)}");
        script.AppendLine("function Note($text) { \"[$(Get-Date -Format o)] $text\" | Out-File -FilePath $log -Append -Encoding utf8 }");
        script.AppendLine($"Note 'Waiting for Talavine Code (pid {processId}) to exit.'");
        script.AppendLine($"try {{ Wait-Process -Id {processId} -Timeout {waitSeconds} -ErrorAction SilentlyContinue }} catch {{ }}");
        script.AppendLine($"if (Get-Process -Id {processId} -ErrorAction SilentlyContinue) {{ Note 'App is still running; rebuild cancelled.'; $s = Get-Content -Raw $status | ConvertFrom-Json; $s.buildExitCode = 125; $s.finishedUtc = (Get-Date).ToUniversalTime().ToString('o'); $s | ConvertTo-Json -Depth 5 | Set-Content -Path $status -Encoding utf8; exit 125 }}");
        script.AppendLine("Start-Sleep -Seconds 2");
        script.AppendLine($"Set-Location {Quote(plan.RepositoryRoot)}");
        if (_mergedBindings.Length > 0)
        {
            script.AppendLine("function Stop-RepairSource($message) { Note $message; $s = Get-Content -Raw $status | ConvertFrom-Json; $s.buildExitCode = 124; $s.finishedUtc = (Get-Date).ToUniversalTime().ToString('o'); $s | ConvertTo-Json -Depth 8 | Set-Content -Path $status -Encoding utf8; exit 124 }");
            script.AppendLine("$pendingSource = @(& git status --porcelain --untracked-files=all); if ($LASTEXITCODE -ne 0 -or $pendingSource.Count -ne 0) { Note 'Source checkout has uncommitted work; installation postponed.'; $s = Get-Content -Raw $status | ConvertFrom-Json; $s.buildExitCode = 124; $s.finishedUtc = (Get-Date).ToUniversalTime().ToString('o'); $s | ConvertTo-Json -Depth 8 | Set-Content -Path $status -Encoding utf8; exit 124 }");
            script.AppendLine("$repairHead = (& git rev-parse --verify HEAD); if ($LASTEXITCODE -ne 0) { Stop-RepairSource 'Source HEAD could not be read.' }; $repairHead = $repairHead.Trim()");
            if (_automaticSelection)
                script.AppendLine($"if ($repairHead -ne {Quote(_mergedBindings[^1].Commit)}) {{ Stop-RepairSource 'Source HEAD is not the exact verified repair commit.' }}");
        }
        script.AppendLine($"Note 'Building: {plan.Describe().Replace("'", "''")}'");
        script.AppendLine($"& {Quote(plan.BuildCommand)} {string.Join(' ', plan.BuildArguments.Select(Quote))} 2>&1 | Out-File -FilePath $log -Append -Encoding utf8");
        script.AppendLine("$exit = $LASTEXITCODE");
        script.AppendLine("Note \"Build exit code $exit\"");
        script.AppendLine("try { $s = Get-Content -Raw $status | ConvertFrom-Json; $s | Add-Member -NotePropertyName buildExitCode -NotePropertyValue $exit -Force; $s | Add-Member -NotePropertyName finishedUtc -NotePropertyValue ((Get-Date).ToUniversalTime().ToString('o')) -Force; $s | ConvertTo-Json -Depth 5 | Set-Content -Path $status -Encoding utf8 } catch { Note \"Could not update status: $_\" }");
        script.AppendLine("if ($exit -ne 0) { Note 'Build failed; app was not relaunched.'; exit $exit }");
        if (_mergedBindings.Length > 0)
            script.AppendLine("$postBuildHead = (& git rev-parse --verify HEAD); $headExit = $LASTEXITCODE; $pendingSource = @(& git status --porcelain --untracked-files=all); if ($LASTEXITCODE -ne 0 -or $headExit -ne 0 -or $postBuildHead.Trim() -ne $repairHead -or $pendingSource.Count -ne 0) { Note 'Source changed during rebuild; installation postponed.'; $s = Get-Content -Raw $status | ConvertFrom-Json; $s.buildExitCode = 124; $s.finishedUtc = (Get-Date).ToUniversalTime().ToString('o'); $s | ConvertTo-Json -Depth 8 | Set-Content -Path $status -Encoding utf8; exit 124 }");
        script.AppendLine("$s = Get-Content -Raw $status | ConvertFrom-Json; $hashes = @{}; Get-ChildItem -LiteralPath $s.artifactRoot -File -Recurse | ForEach-Object { $relative = [IO.Path]::GetRelativePath($s.artifactRoot, $_.FullName).Replace('\\', '/'); $hashes[$relative] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }; $s | Add-Member -NotePropertyName artifactHashes -NotePropertyValue $hashes -Force; $s | ConvertTo-Json -Depth 8 | Set-Content -Path $status -Encoding utf8");
        script.AppendLine($"Note 'Starting {plan.ExecutablePath.Replace("'", "''")}'");
        script.AppendLine($"Start-Process -FilePath {Quote(plan.ExecutablePath)} -WorkingDirectory {Quote(exeDir)}");
        File.WriteAllText(ScriptPath, script.ToString());
        return ScriptPath;
    }

    /// <summary>Starts the helper detached (it must outlive this process); returns the helper's process id.</summary>
    public int StartRelaunch(string scriptPath)
    {
        using var helper = StartRelaunchTracked(scriptPath);
        return helper.Id;
    }

    /// <summary>Retains the actual launched process handle so activation can fence new turns until that helper exits.</summary>
    public Process StartRelaunchTracked(string scriptPath)
    {
        if (Path.GetFileName(scriptPath) == MacScriptName)
        {
            var detached = new ProcessStartInfo("/usr/bin/nohup")
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? paths.RootDirectory,
            };
            detached.ArgumentList.Add("/bin/bash");
            detached.ArgumentList.Add(scriptPath);
            detached.Environment[SwarmCodeDataPaths.HomeEnvironmentVariable] = paths.RootDirectory;
            return Process.Start(detached) ?? throw new InvalidOperationException("The native macOS relaunch helper could not be started.");
        }
        var shell = FindPowerShell();
        var info = new ProcessStartInfo(shell)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? paths.RootDirectory,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath }) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new InvalidOperationException("The relaunch helper could not be started.");
    }

    private string WriteMacRelaunchScript(RebuildPlan plan, int processId, int waitSeconds)
    {
        var scriptPath = Path.Combine(paths.SelfImprovementDirectory, MacScriptName);
        var script = new StringBuilder();
        script.AppendLine("#!/bin/bash");
        script.AppendLine("set -u");
        script.AppendLine($"log={ShellQuote(LogPath)}");
        script.AppendLine($"status={ShellQuote(StatusPath)}");
        script.AppendLine("exec >>\"$log\" 2>&1 < /dev/null");
        script.AppendLine($"export SWARMCODE_HOME={ShellQuote(paths.RootDirectory)}");
        script.AppendLine("export CONFIGURATION=Debug");
        script.AppendLine("finish() {");
        script.AppendLine("  python3 - \"$status\" \"$1\" <<'SWARMCODE_STATUS_PY'");
        script.AppendLine("import datetime, json, os, pathlib, sys, tempfile");
        script.AppendLine("path = pathlib.Path(sys.argv[1])");
        script.AppendLine("data = json.loads(path.read_text(encoding='utf-8-sig'))");
        script.AppendLine("data.update(buildExitCode=int(sys.argv[2]), finishedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat())");
        script.AppendLine("with tempfile.NamedTemporaryFile(mode='w', dir=path.parent, delete=False) as output:");
        script.AppendLine("    json.dump(data, output); output.flush(); os.fsync(output.fileno()); temporary = output.name");
        script.AppendLine("os.replace(temporary, path)");
        script.AppendLine("SWARMCODE_STATUS_PY");
        script.AppendLine("}");
        var automaticFallback = _automaticGoalRepair && plan.InstallBundlePath is not null;
        if (automaticFallback)
        {
            script.AppendLine($"target={ShellQuote(plan.InstallBundlePath!)}");
            script.AppendLine($"old_exe={ShellQuote(plan.ExecutablePath!)}");
            script.AppendLine("phase=waiting; fallback_allowed=0; backup=''");
            script.AppendLine("target_not_running() {");
            script.AppendLine("  python3 - \"$old_exe\" <<'SWARMCODE_TARGET_IDLE_PY'");
            script.AppendLine(TargetProcessInventory);
            script.AppendLine("SWARMCODE_TARGET_IDLE_PY");
            script.AppendLine("}");
            script.AppendLine("verify_previous() {");
            script.AppendLine("  /usr/bin/codesign --verify --deep --strict \"$1/..\" || return 1");
            script.AppendLine("  python3 - \"$status\" \"$1\" <<'SWARMCODE_PREVIOUS_PY'");
            script.AppendLine("import hashlib,json,pathlib,sys");
            script.AppendLine("data=json.loads(pathlib.Path(sys.argv[1]).read_text()); root=pathlib.Path(sys.argv[2]); expected=data.get('previousArtifactHashes')");
            script.AppendLine("if not data.get('automaticGoalRepair') or not expected or root.is_symlink() or root.parent.is_symlink(): sys.exit(1)");
            script.AppendLine("actual={str(p.relative_to(root)):hashlib.file_digest(p.open('rb'),'sha256').hexdigest() for p in root.rglob('*') if p.is_file()}");
            script.AppendLine("sys.exit(0 if actual == expected else 1)");
            script.AppendLine("SWARMCODE_PREVIOUS_PY");
            script.AppendLine("}");
            script.AppendLine("recover_previous() {");
            script.AppendLine("  failure=$?; trap - EXIT");
            script.AppendLine("  [[ $failure -ne 0 && $fallback_allowed -eq 1 ]] || return \"$failure\"");
            script.AppendLine("  finish \"$failure\" || return \"$failure\"");
            script.AppendLine("  if [[ \"$phase\" == launch ]]; then");
            script.AppendLine("    target_not_running || { echo 'Candidate may still be running; restoration withheld.'; return \"$failure\"; }");
            script.AppendLine("  fi");
            script.AppendLine("  if [[ -n \"$backup\" && -d \"$backup\" ]]; then");
            script.AppendLine("    verify_previous \"$backup/Contents\" || { echo 'Previous backup could not be verified; no fallback app was started.'; return \"$failure\"; }");
            script.AppendLine("    failed=\"${target}.repair-failed-$$\"");
            script.AppendLine("    [[ ! -e \"$failed\" ]] || return \"$failure\"");
            script.AppendLine("    if [[ -e \"$target\" ]]; then mv \"$target\" \"$failed\" || return \"$failure\"; fi");
            script.AppendLine("    mv \"$backup\" \"$target\" || { echo 'Previous app restoration failed; no fallback app was started.'; return \"$failure\"; }");
            script.AppendLine("  fi");
            script.AppendLine("  verify_previous \"$target/Contents\" && [[ -x \"$old_exe\" ]] || { echo 'Previous installed app could not be verified; no fallback app was started.'; return \"$failure\"; }");
            script.AppendLine("  python3 - \"$status\" \"$phase\" <<'SWARMCODE_FALLBACK_PY'");
            script.AppendLine("import datetime,json,os,pathlib,sys,tempfile");
            script.AppendLine("path=pathlib.Path(sys.argv[1]); data=json.loads(path.read_text()); data.update(fallbackVerifiedAtUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(), failurePhase=sys.argv[2])");
            script.AppendLine("with tempfile.NamedTemporaryFile(mode='w',dir=path.parent,delete=False) as output:");
            script.AppendLine("    json.dump(data,output); output.flush(); os.fsync(output.fileno()); temporary=output.name");
            script.AppendLine("os.replace(temporary,path)");
            script.AppendLine("SWARMCODE_FALLBACK_PY");
            script.AppendLine("  [[ $? -eq 0 ]] || return \"$failure\"");
            script.AppendLine("  target_not_running || { echo 'The previous app is already running, or its process inventory could not be checked; no duplicate launch was attempted.'; return \"$failure\"; }");
            script.AppendLine("  cd \"$(dirname \"$old_exe\")\" || return \"$failure\"");
            script.AppendLine("  \"$old_exe\" >>\"$log\" 2>&1 < /dev/null &");
            script.AppendLine("  echo \"Restarted the previous verified app (pid $!) after failed automatic installation.\"");
            script.AppendLine("  return \"$failure\"");
            script.AppendLine("}");
            script.AppendLine("trap recover_previous EXIT");
        }
        script.AppendLine($"echo 'Waiting for Talavine Code (pid {processId}) to exit.'");
        script.AppendLine($"for ((attempt=0; attempt<{waitSeconds}; attempt++)); do");
        script.AppendLine($"  kill -0 {processId} 2>/dev/null || break");
        script.AppendLine("  sleep 1");
        script.AppendLine("done");
        script.AppendLine($"if kill -0 {processId} 2>/dev/null; then echo 'App is still running; rebuild cancelled.'; finish 125; exit 125; fi");
        if (automaticFallback) script.AppendLine("fallback_allowed=1; phase=source_preflight");
        script.AppendLine($"cd {ShellQuote(plan.RepositoryRoot!)} || {{ finish 126; exit 126; }}");
        if (_mergedBindings.Length > 0)
        {
            script.AppendLine("[[ -z \"$(git status --porcelain --untracked-files=all)\" ]] || { echo 'Source checkout has uncommitted work; installation postponed.'; finish 124; exit 124; }");
            script.AppendLine("repair_head=$(git rev-parse --verify HEAD) || { finish 124; exit 124; }");
            if (_automaticSelection)
                script.AppendLine($"[[ \"$repair_head\" == {ShellQuote(_mergedBindings[^1].Commit)} ]] || {{ echo 'Source HEAD is not the exact verified repair commit.'; finish 124; exit 124; }}");
            foreach (var binding in _mergedBindings)
                script.AppendLine($"git merge-base --is-ancestor {ShellQuote(binding.Commit)} HEAD || {{ echo 'Repair is absent from source HEAD.'; finish 124; exit 124; }}");
        }
        if (plan.StagingArtifactsPath is { } artifacts)
            script.AppendLine($"export SWARMCODE_ARTIFACTS_PATH={ShellQuote(artifacts)}");
        if (plan.InstallBundlePath is { } versionedTarget)
        {
            // The source project's development default is not the installed version. Keep
            // automatic repairs moving forward instead of silently rebuilding version1.0.0.
            script.AppendLine($"installed_version=$(/usr/libexec/PlistBuddy -c 'Print CFBundleShortVersionString' {ShellQuote(Path.Combine(versionedTarget, "Contents", "Info.plist"))}) || {{ finish 126; exit 126; }}");
            script.AppendLine("[[ \"$installed_version\" =~ ^([0-9]+)\\.([0-9]+)\\.([0-9]+)$ ]] || { echo 'Installed version is invalid; replacement refused.'; finish 126; exit 126; }");
            script.AppendLine("export VERSION=\"$((10#${BASH_REMATCH[1]})).$((10#${BASH_REMATCH[2]})).$((10#${BASH_REMATCH[3]} + 1))\"");
        }
        if (automaticFallback) script.AppendLine("phase=build");
        script.AppendLine($"{ShellQuote(plan.BuildCommand)} {string.Join(' ', plan.BuildArguments.Select(ShellQuote))}");
        script.AppendLine("result=$?");
        if (automaticFallback) script.AppendLine("if [[ $result -ne 0 ]]; then finish \"$result\" || exit 126; fi");
        else script.AppendLine("finish \"$result\" || exit 126");
        script.AppendLine("if [[ $result -ne 0 ]]; then echo 'Build failed; app was not relaunched.'; exit \"$result\"; fi");
        if (_mergedBindings.Length > 0)
            script.AppendLine("[[ \"$(git rev-parse --verify HEAD)\" == \"$repair_head\" && -z \"$(git status --porcelain --untracked-files=all)\" ]] || { echo 'Source changed during rebuild; installation postponed.'; finish 124; exit 124; }");
        if (plan.InstallBundlePath is { } target)
        {
            if (automaticFallback) script.AppendLine("phase=candidate_verification");
            script.AppendLine($"target={ShellQuote(target)}");
            script.AppendLine($"staged=$(find {ShellQuote(plan.StagingArtifactsPath!)} -type d -name 'Talavine Code.app' -prune -print)");
            script.AppendLine("[[ -n \"$staged\" && \"$staged\" != *$'\\n'* ]] || { echo 'Expected one rebuilt app bundle.'; finish 126; exit 126; }");
            script.AppendLine("/usr/bin/codesign --verify --deep --strict \"$target\" && /usr/bin/codesign --verify --deep --strict \"$staged\" || { finish 126; exit 126; }");
            script.AppendLine("old_id=$(/usr/libexec/PlistBuddy -c 'Print CFBundleIdentifier' \"$target/Contents/Info.plist\"); new_id=$(/usr/libexec/PlistBuddy -c 'Print CFBundleIdentifier' \"$staged/Contents/Info.plist\")");
            script.AppendLine("old_team=$(/usr/bin/codesign -dv --verbose=4 \"$target\" 2>&1 | sed -n 's/^TeamIdentifier=//p'); new_team=$(/usr/bin/codesign -dv --verbose=4 \"$staged\" 2>&1 | sed -n 's/^TeamIdentifier=//p')");
            script.AppendLine("[[ -n \"$old_id\" && \"$old_id\" == \"$new_id\" && -n \"$old_team\" && \"$old_team\" != not\\ set && \"$old_team\" == \"$new_team\" ]] || { echo 'Signed bundle identity changed; installation refused.'; finish 126; exit 126; }");
            script.AppendLine("replacement_version=$(/usr/libexec/PlistBuddy -c 'Print CFBundleShortVersionString' \"$target/Contents/Info.plist\"); staged_version=$(/usr/libexec/PlistBuddy -c 'Print CFBundleShortVersionString' \"$staged/Contents/Info.plist\")");
            script.AppendLine("[[ \"$replacement_version\" == \"$installed_version\" && \"$staged_version\" == \"$VERSION\" ]] || { echo 'Installed version changed or candidate version differs; installation postponed.'; finish 124; exit 124; }");
            script.AppendLine("candidate=\"${target}.repair-next-$$\"; backup=\"${target}.repair-backup-$(date -u +%Y%m%dT%H%M%SZ)-$$\"");
            script.AppendLine("[[ ! -e \"$candidate\" && ! -e \"$backup\" ]] || { finish 126; exit 126; }");
            script.AppendLine("/usr/bin/ditto \"$staged\" \"$candidate\" && /usr/bin/codesign --verify --deep --strict \"$candidate\" || { echo 'Candidate copy failed; installed app retained.'; finish 126; exit 126; }");
            if (automaticFallback)
                script.AppendLine("target_not_running || { echo 'The app was reopened during build; replacement postponed.'; finish 124; exit 124; }");
            script.AppendLine("mv \"$target\" \"$backup\" || { finish 126; exit 126; }");
            if (automaticFallback) script.AppendLine("phase=installation");
            script.AppendLine("if ! mv \"$candidate\" \"$target\"; then mv \"$backup\" \"$target\"; finish 126; exit 126; fi");
            script.AppendLine("if ! /usr/bin/codesign --verify --deep --strict \"$target\"; then mv \"$target\" \"$candidate\"; mv \"$backup\" \"$target\"; finish 126; exit 126; fi");
            script.AppendLine("echo \"Retained previous installed bundle: $backup\"");
        }
        if (automaticFallback) script.AppendLine("phase=artifact_receipt");
        script.AppendLine("python3 - \"$status\" <<'SWARMCODE_ARTIFACT_PY'");
        script.AppendLine("import hashlib, json, os, pathlib, sys, tempfile");
        script.AppendLine("path = pathlib.Path(sys.argv[1]); data = json.loads(path.read_text(encoding='utf-8-sig')); root = pathlib.Path(data['artifactRoot'])");
        script.AppendLine("data['artifactHashes'] = {str(p.relative_to(root)): hashlib.file_digest(p.open('rb'), 'sha256').hexdigest() for p in root.rglob('*') if p.is_file()}");
        script.AppendLine("with tempfile.NamedTemporaryFile(mode='w', dir=path.parent, delete=False) as output:");
        script.AppendLine("    json.dump(data, output); output.flush(); os.fsync(output.fileno()); temporary = output.name");
        script.AppendLine("os.replace(temporary, path)");
        script.AppendLine("SWARMCODE_ARTIFACT_PY");
        script.AppendLine("if [[ $? -ne 0 ]]; then echo 'Artifact verification receipt failed; app was not relaunched.'; finish 126; exit 126; fi");
        script.AppendLine($"if [[ ! -x {ShellQuote(plan.ExecutablePath!)} ]]; then echo 'Rebuilt executable is missing; app was not relaunched.'; finish 127; exit 127; fi");
        if (automaticFallback) script.AppendLine("phase=launch");
        script.AppendLine($"cd {ShellQuote(Path.GetDirectoryName(plan.ExecutablePath!)!)} || exit 126");
        if (automaticFallback)
        {
            // The host acknowledgment binds loaded modules to this attempt. A launch command alone
            // is insufficient: an immediate crash or failed host initialization must roll back.
            script.AppendLine($"python3 - \"$status\" {ShellQuote(plan.ExecutablePath!)} <<'SWARMCODE_STARTUP_PY'");
            script.AppendLine(MacStartupSupervisor);
            script.AppendLine("SWARMCODE_STARTUP_PY");
            script.AppendLine("launch_result=$?; if [[ $launch_result -eq 129 ]]; then fallback_allowed=0; echo 'Candidate could not be stopped; restoration withheld.'; finish 129; exit 129; fi");
            script.AppendLine("[[ $launch_result -eq 0 ]] || { echo 'New host did not acknowledge startup; restoring the previous app.'; finish 128; exit 128; }");
        }
        else
        {
            script.AppendLine($"{ShellQuote(plan.ExecutablePath!)} >>\"$log\" 2>&1 < /dev/null &");
            script.AppendLine("echo \"Started native Talavine Code (pid $!).\"");
        }
        File.WriteAllText(scriptPath, script.ToString());
        return scriptPath;
    }

    internal const string TargetProcessInventory = """
    import subprocess,sys
    try: lines=subprocess.check_output(['/bin/ps','-axo','pid=,comm='],text=True).splitlines()
    except Exception: sys.exit(2)
    sys.exit(1 if any(len(parts:=line.strip().split(None,1))==2 and parts[1]==sys.argv[1] for line in lines) else 0)
    """;

    internal const string MacStartupSupervisor = """
    import datetime,json,os,pathlib,subprocess,sys,tempfile,time
    path=pathlib.Path(sys.argv[1]); data=json.loads(path.read_text()); ack=path.parent/('startup-'+data['attemptId']+'.json')
    child=subprocess.Popen([sys.argv[2]],stdin=subprocess.DEVNULL,start_new_session=True)
    deadline=time.monotonic()+45
    while child.poll() is None and time.monotonic()<deadline:
        try:
            ready=json.loads(ack.read_text())
            if ready.get('attemptId')==data['attemptId'] and ready.get('processId')==child.pid and ready.get('artifactHashes')==data.get('artifactHashes'):
                data.update(startupAcknowledgedAtUtc=ready['acknowledgedAtUtc'],startedProcessId=child.pid,buildExitCode=0,finishedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat())
                with tempfile.NamedTemporaryFile(mode='w',dir=path.parent,delete=False) as output:
                    json.dump(data,output); output.flush(); os.fsync(output.fileno()); temporary=output.name
                os.replace(temporary,path); sys.exit(0)
        except Exception: pass
        time.sleep(.1)
    if child.poll() is None:
        child.terminate()
        try: child.wait(timeout=10)
        except subprocess.TimeoutExpired:
            child.kill()
            try: child.wait(timeout=10)
            except subprocess.TimeoutExpired: sys.exit(129)
    sys.exit(128)
    """;

    public RelaunchStatus? ReadStatus()
    {
        try
        {
            if (!File.Exists(StatusPath)) return null;
            return JsonSerializer.Deserialize<RelaunchStatus>(File.ReadAllText(StatusPath), Json);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public static string FindPowerShell()
    {
        foreach (var candidate in new[] { "pwsh.exe", "pwsh" })
        {
            var found = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Select(dir => { try { return Path.Combine(dir.Trim(), candidate); } catch (ArgumentException) { return null; } })
                .FirstOrDefault(p => p is not null && File.Exists(p));
            if (found is not null) return found;
        }
        var windows = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(windows) ? windows : "powershell";
    }

    private static bool IsUnder(string path, string root)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
}
