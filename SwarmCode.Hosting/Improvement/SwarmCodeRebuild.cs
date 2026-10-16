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
}

/// <summary>One verified item's merge into the source checkout.</summary>
public sealed record MergeOutcome(SelfImprovementItem Item, bool Merged, string Message);

/// <summary>Progress of the last build-and-relaunch, written by the helper script beside the backlog.</summary>
public sealed record RelaunchStatus(DateTime StartedUtc, IReadOnlyList<string> Merged, string Command, int? BuildExitCode, DateTime? FinishedUtc, string LogPath)
{
    public bool InProgress => BuildExitCode is null && FinishedUtc is null;
}

/// <summary>
/// Closes the loop for a developer: merges the verified fixes into the source checkout, then hands off to a small
/// detached PowerShell helper that waits for this process to exit, rebuilds the Debug app into the folder it is running
/// from (the same command the VS Code task uses, or the repository's Windows build script for the <c>.build</c>
/// layout) and starts the new build. The app itself only merges, writes the helper, starts it and quits.
/// </summary>
public sealed class SwarmCodeRebuild(SwarmCodeDataPaths paths, Func<SelfImprovementOptions> options, ISelfImprovementBacklog backlog,
    ILogger? logger = null, string? executablePath = null, string? baseDirectory = null)
{
    public const string ScriptName = "relaunch.ps1";
    public const string StatusName = "relaunch.json";
    public const string LogName = "relaunch.log";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

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
        if (!isWindows) return Impossible("Rebuilding and relaunching is supported on Windows only for now. Merge the branches and rebuild from VS Code.");
        var exe = ExecutablePath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return Impossible("The running executable could not be located.");
        var baseDir = Path.GetFullPath(BaseDirectory);
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
    public async Task<IReadOnlyList<MergeOutcome>> MergeVerifiedAsync(string repositoryRoot, CancellationToken ct = default)
    {
        var outcomes = new List<MergeOutcome>();
        foreach (var item in await ShippableAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            var (merged, message) = await SelfImprovementGit.MergeAsync(repositoryRoot, item.Branch!, "Merge " + item.Branch + ": " + item.Title, ct).ConfigureAwait(false);
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
        return outcomes;
    }

    /// <summary>
    /// Writes the detached helper: wait for this process, build, record the result, start the executable again. The
    /// build's output goes to <see cref="LogPath"/>; <see cref="StatusPath"/> says how it went.
    /// </summary>
    public string WriteRelaunchScript(RebuildPlan plan, int processId, IReadOnlyList<string> merged)
    {
        if (!plan.Possible || plan.RepositoryRoot is null || plan.ExecutablePath is null) throw new InvalidOperationException(plan.Message);
        Directory.CreateDirectory(paths.SelfImprovementDirectory);
        var started = DateTime.UtcNow;
        File.WriteAllText(StatusPath, JsonSerializer.Serialize(new RelaunchStatus(started, merged, plan.Describe(), null, null, LogPath), Json));
        var exeDir = Path.GetDirectoryName(plan.ExecutablePath) ?? plan.RepositoryRoot;
        var script = new StringBuilder();
        script.AppendLine("$ErrorActionPreference = 'Continue'");
        script.AppendLine($"$log = {Quote(LogPath)}");
        script.AppendLine($"$status = {Quote(StatusPath)}");
        script.AppendLine("function Note($text) { \"[$(Get-Date -Format o)] $text\" | Out-File -FilePath $log -Append -Encoding utf8 }");
        script.AppendLine($"Note 'Waiting for Talavine Code (pid {processId}) to exit.'");
        script.AppendLine($"try {{ Wait-Process -Id {processId} -Timeout 180 -ErrorAction SilentlyContinue }} catch {{ }}");
        script.AppendLine("Start-Sleep -Seconds 2");
        script.AppendLine($"Set-Location {Quote(plan.RepositoryRoot)}");
        script.AppendLine($"Note 'Building: {plan.Describe().Replace("'", "''")}'");
        script.AppendLine($"& {Quote(plan.BuildCommand)} {string.Join(' ', plan.BuildArguments.Select(Quote))} 2>&1 | Out-File -FilePath $log -Append -Encoding utf8");
        script.AppendLine("$exit = $LASTEXITCODE");
        script.AppendLine("Note \"Build exit code $exit\"");
        script.AppendLine("try { $s = Get-Content -Raw $status | ConvertFrom-Json; $s | Add-Member -NotePropertyName buildExitCode -NotePropertyValue $exit -Force; $s | Add-Member -NotePropertyName finishedUtc -NotePropertyValue ((Get-Date).ToUniversalTime().ToString('o')) -Force; $s | ConvertTo-Json -Depth 5 | Set-Content -Path $status -Encoding utf8 } catch { Note \"Could not update status: $_\" }");
        script.AppendLine($"Note 'Starting {plan.ExecutablePath.Replace("'", "''")}'");
        script.AppendLine($"Start-Process -FilePath {Quote(plan.ExecutablePath)} -WorkingDirectory {Quote(exeDir)}");
        File.WriteAllText(ScriptPath, script.ToString());
        return ScriptPath;
    }

    /// <summary>Starts the helper detached (it must outlive this process); returns the helper's process id.</summary>
    public int StartRelaunch(string scriptPath)
    {
        var shell = FindPowerShell();
        var info = new ProcessStartInfo(shell)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? paths.RootDirectory,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath }) info.ArgumentList.Add(arg);
        var process = Process.Start(info) ?? throw new InvalidOperationException("The relaunch helper could not be started.");
        return process.Id;
    }

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
}
