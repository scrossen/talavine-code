using System.Diagnostics;
using SwarmCore.Agent.Continual;

namespace SwarmCore.Missions;

/// <summary>Private mount/PID/IPC/network namespaces; only mission resources are mounted.</summary>
internal static class LinuxMissionProcessSandbox
{
    internal static void Apply(ProcessStartInfo start, MissionAuthority authority, Guid id)
    {
        authority = MissionFileAliases.Protect(authority);
        var bwrap = new[] { "/usr/bin/bwrap", "/bin/bwrap" }.FirstOrDefault(File.Exists)
            ?? throw new MissionActionSkippedException("Linux containment requires the distribution's bubblewrap package.");
        var scratch = SandboxPathResolver.ResolvePhysicalPath(Path.Combine(Path.GetTempPath(), "swarmcore-mission-process", id.ToString("N")));
        Directory.CreateDirectory(scratch);
        var executable = start.FileName;
        var arguments = start.ArgumentList.ToArray();
        var path = start.Environment["PATH"] ?? "/usr/bin:/bin";
        start.FileName = bwrap;
        start.ArgumentList.Clear();
        void Add(params string[] values) { foreach (var value in values) start.ArgumentList.Add(value); }
        Add("--unshare-all", "--die-with-parent", "--new-session", "--cap-drop", "ALL", "--clearenv");
        // No host /proc, /run, /tmp, /home, sockets, D-Bus or inherited credentials.
        foreach (var system in new[] { "/usr", "/bin", "/sbin", "/lib", "/lib64" })
            if (Directory.Exists(system)) Add("--ro-bind", system, system);
        foreach (var config in new[] { "/etc/passwd", "/etc/group", "/etc/nsswitch.conf", "/etc/ld.so.cache", "/etc/ld.so.conf", "/etc/localtime", "/etc/ssl/certs" })
            if (Path.Exists(config)) Add("--ro-bind", config, config);
        var runtime = new DirectoryInfo(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()).Parent?.Parent?.Parent?.FullName;
        if (runtime is not null && Directory.Exists(Path.Combine(runtime, "host", "fxr"))) Add("--ro-bind", runtime, runtime);
        Add("--proc", "/proc", "--dev", "/dev", "--tmpfs", "/tmp");
        Add("--bind", scratch, scratch);
        foreach (var grant in authority.Grants.Where(g => g.Resource.Kind is MissionResourceKind.File or MissionResourceKind.Directory)
                     .OrderBy(g => g.Resource.Identity.Length))
        {
            var physical = SandboxPathResolver.ResolvePhysicalPath(grant.Resource.Identity);
            if (physical != grant.Resource.Identity) throw new MissionActionSkippedException("Mission resource changed its physical target.");
            if (!Path.Exists(physical)) continue;
            var writable = authority.Evaluate(new(grant.Resource, MissionEffect.Modify | MissionEffect.Remove, authority.PartitionKey)).Allowed;
            if (writable || authority.Evaluate(new(grant.Resource, MissionEffect.Inspect, authority.PartitionKey)).Allowed)
                Add(writable ? "--bind" : "--ro-bind", physical, physical);
        }
        // Apply exclusions last. A mounted exclusion cannot be unlinked/renamed through its writable parent.
        foreach (var exclusion in authority.Exclusions.Where(g => g.Resource.Kind is MissionResourceKind.File or MissionResourceKind.Directory)
                     .OrderBy(g => g.Resource.Identity.Length))
        {
            var resource = exclusion.Resource;
            var physical = SandboxPathResolver.ResolvePhysicalPath(resource.Identity);
            if (physical != resource.Identity) throw new MissionActionSkippedException("Excluded resource changed its physical target.");
            if (!Path.Exists(physical))
            {
                var parent = Path.GetDirectoryName(physical)!;
                while (!Directory.Exists(parent)) parent = Path.GetDirectoryName(parent)!;
                Add("--ro-bind", parent, parent);
                continue;
            }
            if ((exclusion.Effects & MissionEffect.Inspect) != 0)
            {
                if (resource.Kind == MissionResourceKind.Directory) Add("--tmpfs", physical, "--remount-ro", physical);
                else Add("--ro-bind", "/dev/null", physical);
            }
            else if ((exclusion.Effects & (MissionEffect.Modify | MissionEffect.Remove)) != 0)
            {
                if (Path.Exists(physical)) Add("--ro-bind", physical, physical);
                else if (resource.Kind == MissionResourceKind.Directory) Add("--tmpfs", physical, "--remount-ro", physical);
                else Add("--ro-bind", "/dev/null", physical);
            }
        }
        foreach (var pair in new Dictionary<string, string>
        {
            ["PATH"] = path, ["HOME"] = scratch, ["TMPDIR"] = "/tmp", ["DOTNET_CLI_HOME"] = scratch,
            ["NUGET_PACKAGES"] = Path.Combine(scratch, "packages"), ["XDG_DATA_HOME"] = Path.Combine(scratch, "data"),
            ["XDG_CONFIG_HOME"] = Path.Combine(scratch, "config"), ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_EnableDiagnostics"] = "0", ["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false", ["MSBUILDDISABLENODEREUSE"] = "1", ["UseSharedCompilation"] = "false"
        }) Add("--setenv", pair.Key, pair.Value);
        if (runtime is not null) Add("--setenv", "DOTNET_ROOT", runtime);
        Add("--chdir", start.WorkingDirectory, "--", executable);
        Add(arguments);
        start.Environment.Clear();
        start.Environment["PATH"] = "/usr/bin:/bin";
    }
}
