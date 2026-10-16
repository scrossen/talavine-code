using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SwarmCore.Agent.Continual;
using SwarmCore.Agent.Runtime;

namespace SwarmCore.Missions;

/// <summary>Kernel-enforced local process boundary. No command-string inspection grants authority.</summary>
internal static class MissionProcessSandbox
{
    public static SwarmCore.Abstractions.Processes.IChildProcess Start(ProcessStartInfo start, CancellationToken ct = default)
    {
        MissionPrimitiveFence.ValidateAsync(ct).GetAwaiter().GetResult();
        if (OperatingSystem.IsWindows() && AsyncLocalCurrentTurnContextAccessor.Ambient is { MissionObjectiveId: { } id } turn)
            return WindowsMissionProcess.Start(start, turn.MissionResources!, id, ct);
        return SwarmCore.Abstractions.Processes.ChildProcessLauncher.Start(start);
    }

    public static void Apply(ProcessStartInfo start)
    {
        if (AsyncLocalCurrentTurnContextAccessor.Ambient is not { MissionObjectiveId: { } id } turn) return;
        var authority = turn.MissionResources ?? throw new MissionActionSkippedException("missing-resource-authority");
        var action = new MissionAction(new(MissionResourceKind.Directory, SandboxPathResolver.ResolvePhysicalPath(start.WorkingDirectory)),
            MissionEffect.Execute, turn.ProfileId?.ToString("D") ?? "");
        if (!authority.Evaluate(action).Allowed)
            throw new MissionActionSkippedException("Process working directory is outside mission authority.");
        if (OperatingSystem.IsLinux())
        {
            LinuxMissionProcessSandbox.Apply(start, authority, id);
            return;
        }
        if (OperatingSystem.IsWindows()) return; // Created with an AppContainer token by Start, never Process.Start.
        if (!(OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst()) || !File.Exists("/usr/bin/sandbox-exec"))
            throw new MissionActionSkippedException("Mission process containment is unavailable on this host; no unrestricted process was started. Use an available bounded adapter.");

        // Supporting scratch/package/tool locations belong to this mission, never a blanket
        // write grant over the user's home or the shared system temporary directory.
        var scratch = Path.Combine(Path.GetTempPath(), "swarmcore-mission-process", id.ToString("N"));
        Directory.CreateDirectory(scratch);
        scratch = SandboxPathResolver.ResolvePhysicalPath(scratch);
        var profile = new StringBuilder("(version 1)(deny default)(allow process*)(allow sysctl-read)(allow file-read-metadata)");
        start.Environment.TryGetValue("PATH", out var pathValue);
        var dotnetExecutable = (pathValue ?? "").Split(Path.PathSeparator).Where(Path.IsPathFullyQualified)
            .Select(directory => Path.Combine(directory, "dotnet")).FirstOrDefault(File.Exists);
        var dotnetRoot = dotnetExecutable is null ? null : Path.GetDirectoryName(SandboxPathResolver.ResolvePhysicalPath(dotnetExecutable));
        var runtimeRoot = new DirectoryInfo(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()).Parent?.Parent?.Parent?.FullName;
        if (runtimeRoot is not null && Directory.Exists(Path.Combine(runtimeRoot, "host", "fxr"))) dotnetRoot = runtimeRoot;
        else if (dotnetRoot is not null && Directory.Exists(Path.Combine(dotnetRoot, "..", "libexec", "host", "fxr")))
            dotnetRoot = SandboxPathResolver.ResolvePhysicalPath(Path.Combine(dotnetRoot, "..", "libexec"));
        void Rule(string operation, string path, bool directory, bool allow = true)
        {
            if (path.Any(char.IsControl)) throw new MissionActionSkippedException("A containment path contains control characters.");
            profile.Append('(').Append(allow ? "allow " : "deny ").Append(operation)
                .Append(directory ? " (subpath " : " (literal ").Append(JsonSerializer.Serialize(path)).Append("))");
        }
        foreach (var system in new[] { "/System", "/usr", "/bin", "/sbin", "/Library", "/opt", "/private/etc", "/dev" })
            Rule("file-read*", system, true);
        // dyld opens the root directory itself while locating its shared cache.
        // A literal rule permits that directory handle, not recursive filesystem reads.
        Rule("file-read*", "/", false);
        if (dotnetRoot is not null) Rule("file-read*", dotnetRoot, true);
        Rule("file-write*", "/dev/null", false);
        Rule("file-read*", scratch, true);
        Rule("file-write*", scratch, true);
        // CoreCLR named mutexes use this fixed runtime bookkeeping directory even
        // when TMPDIR is redirected. It is a supporting resource for builds/restores.
        Rule("file-read*", "/private/tmp/.dotnet", true);
        Rule("file-write*", "/private/tmp/.dotnet", true);
        foreach (var grant in authority.Grants.Where(g => g.Resource.Kind is MissionResourceKind.File or MissionResourceKind.Directory))
        {
            var path = SandboxPathResolver.ResolvePhysicalPath(grant.Resource.Identity);
            if (path != grant.Resource.Identity) throw new MissionActionSkippedException("Mission resource changed its physical target.");
            var directory = grant.Resource.Kind == MissionResourceKind.Directory;
            if ((grant.Effects & MissionEffect.Inspect) != 0) Rule("file-read*", path, directory);
            // file-write* includes unlink. Do not silently turn a modify-only grant into remove authority.
            if ((grant.Effects & (MissionEffect.Modify | MissionEffect.Remove)) == (MissionEffect.Modify | MissionEffect.Remove))
                Rule("file-write*", path, directory);
        }
        foreach (var exclusion in authority.Exclusions.Where(g => g.Resource.Kind is MissionResourceKind.File or MissionResourceKind.Directory))
        {
            var directory = exclusion.Resource.Kind == MissionResourceKind.Directory;
            if ((exclusion.Effects & MissionEffect.Inspect) != 0) Rule("file-read*", exclusion.Resource.Identity, directory, false);
            if ((exclusion.Effects & (MissionEffect.Modify | MissionEffect.Remove)) != 0) Rule("file-write*", exclusion.Resource.Identity, directory, false);
        }
        var executable = start.FileName;
        var arguments = start.ArgumentList.ToArray();
        start.FileName = "/usr/bin/sandbox-exec";
        start.ArgumentList.Clear();
        start.ArgumentList.Add("-p");
        start.ArgumentList.Add(profile.ToString());
        start.ArgumentList.Add(executable);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        start.Environment["PATH"] = pathValue;
        if (dotnetRoot is not null) start.Environment["DOTNET_ROOT"] = dotnetRoot;
        start.Environment["TMPDIR"] = scratch + Path.DirectorySeparatorChar;
        start.Environment["DOTNET_CLI_HOME"] = scratch;
        start.Environment["NUGET_PACKAGES"] = Path.Combine(scratch, "packages");
        start.Environment["XDG_DATA_HOME"] = Path.Combine(scratch, "data");
        start.Environment["XDG_CONFIG_HOME"] = Path.Combine(scratch, "config");
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        start.Environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false";
        start.Environment["DOTNET_EnableDiagnostics"] = "0";
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["UseSharedCompilation"] = "false";
    }
}
