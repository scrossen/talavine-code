using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using SwarmCore.Abstractions.Processes;
using SwarmCore.Agent.Continual;

// Exposes WindowsMissionProcess.SweepStaleDirectories for a direct, deterministic
// regression test; the sweep otherwise runs at most once per process (see SweepStaleScratchOnce),
// which makes it unreachable end-to-end from a shared test host after the first contained command.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("SwarmCore.Missions.Tests")]

namespace SwarmCore.Missions;

/// <summary>Native LPAC with a unique SID, no network capabilities, and a kill-on-close job.
/// ACL edits add/remove only this invocation's SID, never replace the owner's ACL.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsMissionProcess : IChildProcess
{
    private readonly string _profile;
    private readonly List<(FileSystemInfo File, FileSystemAccessRule Rule)> _rules = [];
    private readonly List<nint> _allocations = [];
    private readonly Dictionary<string, ControlFlags> _originalInheritanceFlags = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(FileSystemInfo File, FileSystemAccessRule[] CopiedRules, FileSystemAccessRule[] ExplicitRules)> _inheritanceBarriers = [];
    private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private nint _sid, _job, _processHandle, _attributes;
    private Process? _process;
    private StreamReader? _output, _error;
    private StreamWriter? _input;
    private Task _outputPump = Task.CompletedTask, _errorPump = Task.CompletedTask;
    private int _exitCode, _disposed;
    private Task? _settlement;
    private string? _workingAlias, _workingAliasTarget, _workingJunction, _scratch, _aliasRoot;
    private static int _sweptStaleScratch;

    /// <summary>LPAC ignores ALL APPLICATION PACKAGES, so every OS facility is an explicit capability.
    /// No internet or private-network capability is ever requested.
    /// <list type="bullet">
    /// <item><c>lpacFileRead</c>/<c>registryRead</c>: runtime files and read-only registry access.</item>
    /// <item><c>lpacCom</c>: NativeAOT runtimes initialize COM (MTA) on their finalizer thread and abort startup
    /// ("Fatal error. .NET runtime failed to initialize.") when that fails. From .NET 11 every SDK command
    /// (<c>dotnet build</c>, <c>dotnet --info</c>, ...) loads the AOT-compiled CLI host
    /// (<c>sdk\&lt;version&gt;\dotnet-aot.dll</c>) in-process, so without this capability no SDK command, and no
    /// NativeAOT tool, can start in the container. It admits the LPAC only to COM servers whose own
    /// launch/access permissions name this capability or ALL RESTRICTED APPLICATION PACKAGES; file, registry,
    /// and network boundaries are unchanged.</item>
    /// <item><c>lpacInstrumentation</c>: Windows Error Reporting admits LPAC callers only through this capability.
    /// Without it a contained <c>RaiseFailFastException</c> (every .NET fatal error) cannot reach WER, and Windows
    /// falls back to a modal "Fail Fast Exception" hard-error box on the user's desktop that ignores the error mode
    /// and blocks the crashed process until someone dismisses it. This capability admits reporting; it does not
    /// itself suppress WER UI. Deliberate crash probes must also set their own no-UI reporting policy.</item>
    /// </list></summary>
    private static readonly string[] LpacCapabilities = ["lpacFileRead", "registryRead", "lpacCom", "lpacInstrumentation"];

    /// <summary>KILL_ON_JOB_CLOSE | DIE_ON_UNHANDLED_EXCEPTION. The latter forces SEM_NOGPFAULTERRORBOX on every
    /// process in the job, so an ordinary unhandled exception in any descendant terminates it instead of showing
    /// a crash dialog. It does not cover fail-fast; see <see cref="LpacCapabilities"/>.</summary>
    private const uint JobLimitFlags = 0x2000 | 0x400;

    private WindowsMissionProcess() => _profile = "SwarmCore.Mission." + Guid.NewGuid().ToString("N");

    internal static WindowsMissionProcess Start(ProcessStartInfo start, MissionAuthority authority, Guid id, CancellationToken ct)
    {
        SweepStaleScratchOnce();
        var child = new WindowsMissionProcess();
        try { child.Launch(start, authority, id, ct); return child; }
        catch { child.Dispose(); throw; }
    }

    /// <summary>Once per process-host lifetime, best-effort reap scratch/alias directories left behind by
    /// missions whose host process exited before its own <see cref="Dispose"/> ran (crash, kill, force-quit).
    /// Never touches an entry younger than 24h, and silently skips one still locked by a live descendant.</summary>
    private static void SweepStaleScratchOnce()
    {
        if (Interlocked.Exchange(ref _sweptStaleScratch, 1) != 0) return;
        SweepStaleDirectories(Path.Combine(Path.GetTempPath(), "swarmcore-mission-process"));
        SweepStaleDirectories(Path.Combine(Path.GetTempPath(), "swarmcore-mission-alias"));
    }

    /// <summary>internal for a direct, deterministic regression test; production callers only reach this
    /// once per process through <see cref="SweepStaleScratchOnce"/>.</summary>
    internal static void SweepStaleDirectories(string root)
    {
        IEnumerable<string> entries;
        try { entries = Directory.Exists(root) ? Directory.EnumerateDirectories(root).ToArray() : []; }
        catch (IOException) { return; } catch (UnauthorizedAccessException) { return; }
        var cutoff = DateTime.UtcNow.AddHours(-24);
        foreach (var entry in entries)
        {
            try { if (Directory.GetLastWriteTimeUtc(entry) > cutoff) continue; }
            catch (IOException) { continue; } catch (UnauthorizedAccessException) { continue; }
            // A recursive delete fails on any file still held open by a running descendant;
            // that failure is the safety check that keeps a live mission's scratch untouched.
            try { Directory.Delete(entry, recursive: true); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private void Launch(ProcessStartInfo start, MissionAuthority authority, Guid id, CancellationToken ct)
    {
        authority = MissionFileAliases.Protect(authority);
        Marshal.ThrowExceptionForHR(CreateAppContainerProfile(_profile, _profile, "Contained mission process", 0, 0, out _sid));
        var identity = new SecurityIdentifier(_sid);
        var scratch = _scratch = Path.Combine(Path.GetTempPath(), "swarmcore-mission-process", id.ToString("N"));
        Directory.CreateDirectory(scratch);
        // Build/package state belongs to the mission across commands. The live junction
        // must be outside that shared inheritance subtree, in invocation-owned scratch.
        var aliasRoot = _aliasRoot = start.FileName.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Path.GetTempPath(), "swarmcore-mission-alias", _profile) : null;
        if (aliasRoot is not null) Directory.CreateDirectory(aliasRoot);
        var fileGrants = authority.Grants.Where(g => g.Resource.Kind is MissionResourceKind.File or MissionResourceKind.Directory).ToArray();
        foreach (var grant in fileGrants)
            if (!string.Equals(SandboxPathResolver.ResolvePhysicalPath(grant.Resource.Identity), grant.Resource.Identity, StringComparison.OrdinalIgnoreCase))
                throw new MissionActionSkippedException("Mission resource changed its physical target.");
        CaptureInheritanceFlags(scratch, ct);
        if (aliasRoot is not null) CaptureInheritanceFlags(aliasRoot, ct);
        foreach (var grant in fileGrants)
            if (Path.Exists(grant.Resource.Identity)) CaptureInheritanceFlags(grant.Resource.Identity, ct);
        Grant(scratch, identity, FileSystemRights.Modify | FileSystemRights.Synchronize, AccessControlType.Allow);
        if (aliasRoot is not null) Grant(aliasRoot, identity, FileSystemRights.Modify | FileSystemRights.Synchronize, AccessControlType.Allow);
        foreach (var grant in authority.Grants.Where(g => g.Resource.Kind is MissionResourceKind.File or MissionResourceKind.Directory))
        {
            var path = SandboxPathResolver.ResolvePhysicalPath(grant.Resource.Identity);
            if (!string.Equals(path, grant.Resource.Identity, StringComparison.OrdinalIgnoreCase))
                throw new MissionActionSkippedException("Mission resource changed its physical target.");
            if (Path.Exists(path)) GrantTree(path, identity, authority);
        }
        var capabilities = new List<nint>();
        foreach (var name in LpacCapabilities)
        {
            Check(DeriveCapabilitySidsFromName(name, out var groups, out var groupCount, out var sids, out var count));
            try
            {
                for (var i = 0; i < count; i++)
                {
                    var source = Marshal.ReadIntPtr(sids, i * IntPtr.Size);
                    var length = GetLengthSid(source);
                    var copy = Alloc((int)length);
                    Check(CopySid(length, copy, source));
                    capabilities.Add(copy);
                    LocalFree(source);
                }
                for (var i = 0; i < groupCount; i++) LocalFree(Marshal.ReadIntPtr(groups, i * IntPtr.Size));
            }
            finally { LocalFree(groups); LocalFree(sids); }
        }
        var capBuffer = Alloc(capabilities.Count * Marshal.SizeOf<SidAndAttributes>());
        for (var i = 0; i < capabilities.Count; i++)
            Marshal.StructureToPtr(new SidAndAttributes { Sid = capabilities[i], Attributes = 4 }, capBuffer + i * Marshal.SizeOf<SidAndAttributes>(), false);
        var security = Struct(new SecurityCapabilities { AppContainerSid = _sid, Capabilities = capBuffer, CapabilityCount = (uint)capabilities.Count });
        nuint size = 0;
        InitializeProcThreadAttributeList(0, 4, 0, ref size);
        _attributes = Alloc(checked((int)size));
        Check(InitializeProcThreadAttributeList(_attributes, 4, 0, ref size));
        Attribute(0x20009, security, Marshal.SizeOf<SecurityCapabilities>());
        Attribute(0x2000f, Struct(1u), sizeof(uint)); // ALL_APPLICATION_PACKAGES_POLICY: opt out.

        _job = CreateJobObject(0, null);
        Check(_job != 0);
        var limits = new JobLimits { BasicLimitInformation = new BasicLimits { LimitFlags = JobLimitFlags } };
        Check(SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf<JobLimits>()));
        Attribute(0x2000d, Struct(_job), IntPtr.Size); // Atomic job assignment at process creation.

        using var stdout = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        using var stderr = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        using var stdin = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var handles = Alloc(3 * IntPtr.Size);
        Marshal.WriteIntPtr(handles, 0, stdout.ClientSafePipeHandle.DangerousGetHandle());
        Marshal.WriteIntPtr(handles, IntPtr.Size, stderr.ClientSafePipeHandle.DangerousGetHandle());
        Marshal.WriteIntPtr(handles, 2 * IntPtr.Size, stdin.ClientSafePipeHandle.DangerousGetHandle());
        Attribute(0x20002, handles, 3 * IntPtr.Size); // Only stdio inherited.
        var si = new StartupInfoEx
        {
            StartupInfo = new StartupInfo { Cb = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100,
                StdOutput = stdout.ClientSafePipeHandle.DangerousGetHandle(), StdError = stderr.ClientSafePipeHandle.DangerousGetHandle(), StdInput = stdin.ClientSafePipeHandle.DangerousGetHandle() },
            AttributeList = _attributes
        };
        var system = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var environment = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PROCESSOR_ARCHITECTURE"] = RuntimeInformation.ProcessArchitecture switch { Architecture.Arm64 => "ARM64", Architecture.X64 => "AMD64", _ => "x86" },
            ["SystemRoot"] = system, ["WINDIR"] = system, ["ComSpec"] = Path.Combine(system, "System32", "cmd.exe"),
            ["PATH"] = start.Environment["PATH"] ?? Path.Combine(system, "System32"),
            ["TEMP"] = scratch, ["TMP"] = scratch, ["USERPROFILE"] = scratch, ["LOCALAPPDATA"] = scratch,
            ["APPDATA"] = scratch, ["DOTNET_CLI_HOME"] = scratch,
            ["ProgramFiles"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ["ProgramFiles(x86)"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            ["ProgramW6432"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ["ProgramData"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            ["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false", ["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "true", ["NUGET_PACKAGES"] = Path.Combine(scratch, "packages"),
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1", ["DOTNET_EnableDiagnostics"] = "0",
            ["MSBUILDDISABLENODEREUSE"] = "1", ["UseSharedCompilation"] = "false"
        };
        foreach (var (name, value) in MissionCommandEnvironment.Get(start)) environment[name] = value;
        var env = Marshal.StringToHGlobalUni(string.Join('\0', environment.Select(p => p.Key + "=" + p.Value)) + "\0\0");
        _allocations.Add(env);
        var isShell = start.FileName.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase);
        var executable = isShell ? Path.Combine(system, "System32", "cmd.exe") : start.FileName;
        if (!Path.IsPathFullyQualified(executable))
            executable = environment["PATH"].Split(Path.PathSeparator).Select(p => Path.Combine(p, executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? executable : executable + ".exe")).FirstOrDefault(File.Exists)
                ?? throw new FileNotFoundException($"Executable '{executable}' was not found on the contained PATH.", executable);
        var command = new StringBuilder(isShell ? Quote(executable) + " /d /s /c \"" + start.ArgumentList.Last() + "\""
            : Quote(executable) + " " + string.Join(" ", start.ArgumentList.Select(Quote)));
        MissionPrimitiveFence.ValidateAsync(ct).GetAwaiter().GetResult();
        var workingDirectory = isShell ? CreateWorkingDirectoryAlias(start.WorkingDirectory, aliasRoot!) : start.WorkingDirectory;
        Check(CreateProcess(executable, command, 0, 0, true, 0x80000 | 0x400 | 0x08000000, env,
            workingDirectory, ref si, out var info));
        stdout.DisposeLocalCopyOfClientHandle();
        stderr.DisposeLocalCopyOfClientHandle();
        stdin.DisposeLocalCopyOfClientHandle();
        _processHandle = info.Process;
        CloseHandle(info.Thread);
        _process = Process.GetProcessById(info.ProcessId);
        // Duplicate parent ends before disposing the pipe owners above.
        _output = new StreamReader(DuplicateStream(stdout.SafePipeHandle.DangerousGetHandle(), FileAccess.Read));
        _error = new StreamReader(DuplicateStream(stderr.SafePipeHandle.DangerousGetHandle(), FileAccess.Read));
        _input = new StreamWriter(DuplicateStream(stdin.SafePipeHandle.DangerousGetHandle(), FileAccess.Write)) { AutoFlush = true };
        _settlement = Task.Run(() =>
        {
            try
            {
                Check(WaitForSingleObject(_processHandle, uint.MaxValue) == 0);
                Check(GetExitCodeProcess(_processHandle, out var code));
                _exitCode = (int)code;
                Check(TerminateJobObject(_job, code));
                WaitForEmptyJob();
                _exit.TrySetResult();
                Exited?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex) { _exit.TrySetException(ex); }
        });
    }

    private void GrantTree(string path, SecurityIdentifier identity, MissionAuthority authority)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return;
        var directory = Directory.Exists(path);
        var resource = new MissionResource(directory ? MissionResourceKind.Directory : MissionResourceKind.File, path);
        var rights = (FileSystemRights)0;
        if (authority.Evaluate(new(resource, MissionEffect.Inspect, authority.PartitionKey)).Allowed)
            rights |= FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize;
        if (authority.Evaluate(new(resource, MissionEffect.Modify, authority.PartitionKey)).Allowed)
            rights |= FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteAttributes
                | FileSystemRights.WriteExtendedAttributes | FileSystemRights.Synchronize;
        if (authority.Evaluate(new(resource, MissionEffect.Remove, authority.PartitionKey)).Allowed)
            rights |= FileSystemRights.Delete | (directory ? FileSystemRights.DeleteSubdirectoriesAndFiles : 0);
        var descendants = authority.Exclusions.Where(e => e.Resource.Kind is MissionResourceKind.File or MissionResourceKind.Directory)
            .Where(e => e.Resource.Identity.StartsWith(path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToArray();
        // DELETE_CHILD on an ancestor bypasses the protected child's own DELETE check.
        // Keep deletion on each permitted child, but never grant removal of an ancestor
        // (or its children indiscriminately) when that would remove an excluded object.
        var protectDescendants = directory && descendants.Any(e => (e.Effects & (MissionEffect.Modify | MissionEffect.Remove)) != 0);
        var inheritChildDelete = protectDescendants && (rights & FileSystemRights.Delete) != 0;
        if (protectDescendants)
            rights &= ~(FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete);
        // AppContainer package access uses a separate access check: a deny ACE is not
        // a dependable subtraction from an inherited package grant. Split the allow tree
        // around exclusions instead, so a protected object never gets a write ACE.
        if (descendants.Any(e => !Path.Exists(Path.Combine(path, Path.GetRelativePath(path, e.Resource.Identity).Split(Path.DirectorySeparatorChar)[0]))))
            rights &= ~(FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories);
        // Isolate each existing branch before applying its evaluated rights. New children
        // inherit the branch grant, while existing excluded children get no such grant.
        FileSystemInfo item = directory ? new DirectoryInfo(path) : new FileInfo(path);
        FileSystemSecurity acl = directory ? ((DirectoryInfo)item).GetAccessControl() : ((FileInfo)item).GetAccessControl();
        if (!acl.AreAccessRulesProtected)
        {
            var originalExplicit = acl.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
            var copied = acl.GetAccessRules(false, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Where(r => !r.IdentityReference.Equals(identity))
                .Select(r => (FileSystemAccessRule)acl.AccessRuleFactory(r.IdentityReference, (int)r.FileSystemRights, false,
                    r.InheritanceFlags, r.PropagationFlags, r.AccessControlType)).ToArray();
            acl.SetAccessRuleProtection(true, true);
            _inheritanceBarriers.Add((item, copied, originalExplicit));
        }
        if (item is DirectoryInfo protectedDirectory) protectedDirectory.SetAccessControl((DirectorySecurity)acl);
        else ((FileInfo)item).SetAccessControl((FileSecurity)acl);
        // Windows materializes inherited ACEs as explicit only after the DACL is
        // committed. Re-read before subtracting this invocation's inherited grant.
        acl = directory ? ((DirectoryInfo)item).GetAccessControl() : ((FileInfo)item).GetAccessControl();
        foreach (FileSystemAccessRule inherited in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (inherited.IdentityReference.Equals(identity)) acl.RemoveAccessRuleSpecific(inherited);
        if (item is DirectoryInfo di) di.SetAccessControl((DirectorySecurity)acl);
        else ((FileInfo)item).SetAccessControl((FileSecurity)acl);
        if (rights != 0) Grant(path, identity, rights, AccessControlType.Allow);
        // New ordinary children retain their own evaluated removal capability. This ACE
        // does not apply to the protected ancestor. Existing excluded branches below are
        // isolated by GrantTree and have every invocation ACE removed before evaluation.
        if (inheritChildDelete)
            Grant(path, identity, FileSystemRights.Delete, AccessControlType.Allow, propagation: PropagationFlags.InheritOnly);
        if (directory && descendants.Length != 0)
            foreach (var child in Directory.EnumerateFileSystemEntries(path)) GrantTree(child, identity, authority);
    }

    private const uint RawTargetPath = 0x1, RemoveDefinition = 0x2, ExactMatchOnRemove = 0x4, NoBroadcastSystem = 0x8;
    private string CreateWorkingDirectoryAlias(string directory, string scratch)
    {
        // cmd queries its drive root before deletion. LPAC cannot inspect the physical
        // volume root. This logon-local alias exposes only the already-authorized cwd;
        // file access still passes the original per-object AppContainer DACL checks.
        // Do not change the command text or shadow any existing drive mapping.
        var physical = SandboxPathResolver.ResolvePhysicalPath(directory);
        if (string.Equals(physical.TrimEnd(Path.DirectorySeparatorChar), Path.GetPathRoot(physical)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new MissionActionSkippedException("A contained command requires an assigned working subdirectory, not a volume root.");
        using var gate = new Mutex(false, @"Local\SwarmCore.Mission.WorkingDirectoryAliases");
        try { if (!gate.WaitOne(TimeSpan.FromSeconds(10))) throw new IOException("Mission working-directory alias allocation is busy."); } catch (AbandonedMutexException) { }
        try
        {
            for (var letter = 'Z'; letter >= 'D'; letter--)
            {
                var name = letter + ":";
                if (ReadAlias(name) is not null) continue;
                _workingJunction = Path.Combine(scratch, "workspace");
                CreateWorkspaceJunction(_workingJunction, physical);
                var target = @"\??\" + scratch.TrimEnd(Path.DirectorySeparatorChar);
                Check(DefineDosDevice(RawTargetPath | NoBroadcastSystem, name, target)); // RAW_TARGET_PATH | NO_BROADCAST_SYSTEM
                _workingAlias = name; _workingAliasTarget = target;
                if (ReadAlias(name) is not { Length: 1 } actual || actual[0] != target)
                    throw new IOException("Mission working-directory alias ownership changed during creation.");
                return name + Path.DirectorySeparatorChar + "workspace";
            }
            throw new IOException("No unused local drive alias is available for the contained command.");
        }
        finally { gate.ReleaseMutex(); }
    }

    private static void CreateWorkspaceJunction(string junction, string target)
    {
        Directory.CreateDirectory(junction);
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
        var print = Encoding.Unicode.GetBytes(target);
        var buffer = new byte[16 + substitute.Length + 2 + print.Length + 2];
        BitConverter.GetBytes(0xA0000003u).CopyTo(buffer, 0); // IO_REPARSE_TAG_MOUNT_POINT
        BitConverter.GetBytes(checked((ushort)(buffer.Length - 8))).CopyTo(buffer, 4);
        BitConverter.GetBytes(checked((ushort)substitute.Length)).CopyTo(buffer, 10);
        BitConverter.GetBytes(checked((ushort)(substitute.Length + 2))).CopyTo(buffer, 12);
        BitConverter.GetBytes(checked((ushort)print.Length)).CopyTo(buffer, 14);
        substitute.CopyTo(buffer, 16); print.CopyTo(buffer, 18 + substitute.Length);
        using var handle = CreateFile(junction, 0x40000000, 7, 0, 3, 0x02200000, 0);
        Check(!handle.IsInvalid);
        Check(DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, 0, 0, out _, 0)); // FSCTL_SET_REPARSE_POINT
    }

    private static string[]? ReadAlias(string name)
    {
        var buffer = new char[32768];
        var length = QueryDosDevice(name, buffer, buffer.Length);
        if (length != 0) return new string(buffer, 0, checked((int)length)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (Marshal.GetLastWin32Error() == 2) return null;
        throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private void ReleaseWorkingDirectoryAlias()
    {
        if (_workingAlias is not { } name || _workingAliasTarget is not { } target) return;
        using var gate = new Mutex(false, @"Local\SwarmCore.Mission.WorkingDirectoryAliases");
        try { if (!gate.WaitOne(TimeSpan.FromSeconds(10))) throw new IOException("Mission working-directory alias allocation is busy."); } catch (AbandonedMutexException) { }
        try
        {
            var current = ReadAlias(name);
            var owned = current is { Length: 1 } && current[0] == target;
            // Exact target removal cannot remove a different mapping installed by an
            // unrelated process; no notifications or root ACL mutations are required.
            if (current?.Contains(target, StringComparer.OrdinalIgnoreCase) == true)
                Check(DefineDosDevice(RawTargetPath | RemoveDefinition | ExactMatchOnRemove | NoBroadcastSystem, name, target)); // RAW | REMOVE | EXACT_MATCH | NO_BROADCAST
            _workingAlias = null; _workingAliasTarget = null;
            if (!owned || ReadAlias(name)?.Contains(target, StringComparer.OrdinalIgnoreCase) == true)
                throw new IOException("Mission working-directory alias ownership changed before cleanup.");
        }
        finally { gate.ReleaseMutex(); }
    }

    private const ControlFlags InheritanceFlagsMask = ControlFlags.DiscretionaryAclAutoInherited | ControlFlags.DiscretionaryAclAutoInheritRequired;
    private void CaptureInheritanceFlags(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_originalInheritanceFlags.ContainsKey(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return;
        FileSystemSecurity acl = Directory.Exists(path) ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        _originalInheritanceFlags.Add(path, new RawSecurityDescriptor(acl.GetSecurityDescriptorBinaryForm(), 0).ControlFlags & InheritanceFlagsMask);
        if (Directory.Exists(path)) foreach (var child in Directory.EnumerateFileSystemEntries(path)) CaptureInheritanceFlags(child, ct);
    }

    private static bool IsOriginalCleanupTarget(string path)
    {
        if (!Path.Exists(path)) return false;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
            || !string.Equals(SandboxPathResolver.ResolvePhysicalPath(path), path, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Mission resource changed its physical target during ACL cleanup: " + path);
        return true;
    }

    private void RestoreInheritanceFlags()
    {
        foreach (var (path, original) in _originalInheritanceFlags)
        {
            if (!IsOriginalCleanupTarget(path)) continue;
            FileSystemSecurity acl = Directory.Exists(path) ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
            var current = new RawSecurityDescriptor(acl.GetSecurityDescriptorBinaryForm(), 0);
            if ((current.ControlFlags & InheritanceFlagsMask) == original) continue;
            // Merge into today's descriptor, preserving unrelated concurrent ACL edits.
            // SetSecurityInfo/.NET persistence itself converts the automatic-inheritance
            // flags; the low-level DACL setter preserves the selected control bits.
            current.SetFlags((current.ControlFlags & ~InheritanceFlagsMask) | original);
            var bytes = new byte[current.BinaryLength]; current.GetBinaryForm(bytes, 0);
            Check(SetFileSecurity(path, 4, bytes)); // DACL_SECURITY_INFORMATION only.
            acl = Directory.Exists(path) ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
            if ((new RawSecurityDescriptor(acl.GetSecurityDescriptorBinaryForm(), 0).ControlFlags & InheritanceFlagsMask) != original)
                throw new IOException("Mission resource inheritance flags did not restore: " + path);
        }
    }

    /// <summary>Best-effort reap of this invocation's own alias directory, run only after the contained
    /// process and its descendants have settled (<see cref="WaitForEmptyJob"/> above) and the caller has
    /// already captured its receipt from stdout/stderr/exit code. The alias root is unique per invocation
    /// (<see cref="_profile"/> is a fresh GUID every launch), so deleting it here is always safe. The
    /// scratch directory is deliberately NOT deleted here: it is keyed by mission ID, not by invocation,
    /// and "Build/package state belongs to the mission across commands" above means a later command in
    /// the same still-running mission (for example a second `dotnet restore` reusing the first one's
    /// NUGET_PACKAGES cache) legitimately expects it to still be there. A scratch directory is reclaimed
    /// only once it is actually abandoned, via <see cref="SweepStaleScratchOnce"/>'s 24h-old sweep. A
    /// locked handle from a lingering antivirus scan or similar is tolerated silently.</summary>
    private void DeleteAliasDirectory()
    {
        if (_aliasRoot is { } aliasRoot) { try { Directory.Delete(aliasRoot, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private static string Quote(string value)
    {
        var quoted = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            quoted.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c);
            slashes = 0;
        }
        return quoted.Append('\\', slashes * 2).Append('"').ToString();
    }

    private void Grant(string path, SecurityIdentifier sid, FileSystemRights rights, AccessControlType type, bool inherit = true,
        PropagationFlags propagation = PropagationFlags.None)
    {
        FileSystemInfo file = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        var rule = new FileSystemAccessRule(sid, rights, file is DirectoryInfo && inherit
            ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None, propagation, type);
        if (file is DirectoryInfo directory) { var acl = directory.GetAccessControl(); acl.AddAccessRule(rule); directory.SetAccessControl(acl); }
        else { var f = (FileInfo)file; var acl = f.GetAccessControl(); acl.AddAccessRule(rule); f.SetAccessControl(acl); }
        _rules.Add((file, rule));
    }
    private nint Alloc(int size) { var value = Marshal.AllocHGlobal(size); _allocations.Add(value); return value; }
    private nint Struct<T>(T value) where T : struct { var p = Alloc(Marshal.SizeOf<T>()); Marshal.StructureToPtr(value, p, false); return p; }
    private void Attribute(nuint key, nint value, int size) => Check(UpdateProcThreadAttribute(_attributes, 0, key, value, (nuint)size, 0, 0));
    private static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    private static FileStream DuplicateStream(nint handle, FileAccess access)
    {
        Check(DuplicateHandle(GetCurrentProcess(), handle, GetCurrentProcess(), out var duplicate, 0, false, 2));
        return new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(duplicate, true), access);
    }
    public int Id => _process!.Id;
    public bool HasExited => _exit.Task.IsCompletedSuccessfully;
    public int ExitCode => HasExited ? _exitCode : throw new InvalidOperationException("Process is running.");
    public StreamWriter StandardInput => _input!;
    public StreamReader StandardOutput => _output!;
    public StreamReader StandardError => _error!;
    public Process? DiagnosticsProcess => _process;
    public Process? AttachDiagnosticsProcess() => HasExited ? null : Process.GetProcessById(Id);
    public event EventHandler? Exited;
    public event Action<string?>? OutputLineReceived;
    public event Action<string?>? ErrorLineReceived;
    public void BeginOutputReadLine() => _outputPump = Pump(StandardOutput, line => OutputLineReceived?.Invoke(line));
    public void BeginErrorReadLine() => _errorPump = Pump(StandardError, line => ErrorLineReceived?.Invoke(line));
    private static async Task Pump(StreamReader reader, Action<string?> receive)
    { while (await reader.ReadLineAsync() is { } line) receive(line); receive(null); }
    public void Kill(bool entireProcessTree = false) { if (_job != 0) Check(TerminateJobObject(_job, 1)); }
    public bool WaitForExit(TimeSpan timeout) => _exit.Task.Wait(timeout);
    public async Task WaitForExitAsync(CancellationToken ct = default)
    { await _exit.Task.WaitAsync(ct); await Task.WhenAll(_outputPump, _errorPump).WaitAsync(ct); }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_job != 0)
        {
            Check(TerminateJobObject(_job, 1));
            // Never remove resource ACLs or release native handles while descendants can write.
            if (_settlement is not null) _settlement.GetAwaiter().GetResult();
            WaitForEmptyJob();
        }
        if (_job != 0) CloseHandle(_job);
        _process?.Dispose(); _output?.Dispose(); _error?.Dispose(); _input?.Dispose();
        if (_processHandle != 0) CloseHandle(_processHandle);
        try
        {
            // Remove only the owned link, never its target, before inherited ACL cleanup.
            if (_workingJunction is { } junction && Directory.Exists(junction)) Directory.Delete(junction, recursive: false);
            _workingJunction = null;
            foreach (var (file, rule) in _rules.AsEnumerable().Reverse())
            {
                if (!IsOriginalCleanupTarget(file.FullName)) continue;
                try
                {
                    if (file is DirectoryInfo d) { var acl = d.GetAccessControl(); acl.RemoveAccessRuleSpecific(rule); d.SetAccessControl(acl); }
                    else { var f = (FileInfo)file; var acl = f.GetAccessControl(); acl.RemoveAccessRuleSpecific(rule); f.SetAccessControl(acl); }
                }
                catch (IOException) { } // A mission may legitimately delete its files.
            }
            foreach (var (file, copied, originalExplicit) in _inheritanceBarriers.AsEnumerable().Reverse())
            {
                if (!IsOriginalCleanupTarget(file.FullName)) continue;
                try
                {
                    if (file is DirectoryInfo d) { var acl = d.GetAccessControl(); foreach (var rule in copied) acl.RemoveAccessRuleSpecific(rule); foreach (var rule in originalExplicit) acl.AddAccessRule(rule); acl.SetAccessRuleProtection(false, true); d.SetAccessControl(acl); }
                    else { var f = (FileInfo)file; var acl = f.GetAccessControl(); foreach (var rule in copied) acl.RemoveAccessRuleSpecific(rule); foreach (var rule in originalExplicit) acl.AddAccessRule(rule); acl.SetAccessRuleProtection(false, true); f.SetAccessControl(acl); }
                }
                catch (IOException) { }
            }
            RestoreInheritanceFlags();
        }
        finally
        {
            try { ReleaseWorkingDirectoryAlias(); }
            finally
            {
                if (_attributes != 0) DeleteProcThreadAttributeList(_attributes);
                foreach (var allocation in _allocations) Marshal.FreeHGlobal(allocation);
                if (_sid != 0) FreeSid(_sid);
                DeleteAppContainerProfile(_profile);
                DeleteAliasDirectory();
            }
        }
    }

    private void WaitForEmptyJob()
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            Check(QueryInformationJobObject(_job, 1, out var accounting,
                (uint)Marshal.SizeOf<JobAccounting>(), out _));
            if (accounting.ActiveProcesses == 0) return;
            if (timer.Elapsed > TimeSpan.FromSeconds(30))
                throw new TimeoutException("Contained process descendants did not settle; completion is unverified.");
            Thread.Sleep(10);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct JobAccounting
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(
        nint job, int informationClass, out JobAccounting accounting, uint length, out uint returnLength);

    [StructLayout(LayoutKind.Sequential)] private struct SecurityCapabilities { public nint AppContainerSid, Capabilities; public uint CapabilityCount, Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct SidAndAttributes { public nint Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfo { public int Cb; public nint Reserved, Desktop, Title; public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags; public short ShowWindow, Reserved2; public nint ReservedPointer, StdInput, StdOutput, StdError; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo StartupInfo; public nint AttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public nint Process, Thread; public int ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public nuint MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public nuint Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct JobLimits { public BasicLimits BasicLimitInformation; public IoCounters IoInfo; public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint code, byte[] input, int length, nint output, int outputLength, out int returned, nint overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool DefineDosDevice(uint flags, string name, string target);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint QueryDosDevice(string name, [Out] char[] target, int length);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetFileSecurity(string path, uint information, byte[] descriptor);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int CreateAppContainerProfile(string name, string display, string description, nint capabilities, uint count, out nint sid);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int DeleteAppContainerProfile(string name);
    [DllImport("kernelbase.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool DeriveCapabilitySidsFromName(string name, out nint groups, out uint groupCount, out nint sids, out uint count);
    [DllImport("advapi32.dll")] private static extern uint GetLengthSid(nint sid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool CopySid(uint length, nint destination, nint source);
    [DllImport("advapi32.dll")] private static extern nint FreeSid(nint sid);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint pointer);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcess(string executable, StringBuilder command, nint processAttributes, nint threadAttributes, bool inherit, uint flags, nint environment, string cwd, ref StartupInfoEx startup, out ProcessInfo process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(nint job, int info, ref JobLimits value, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(nint job, uint exitCode);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(nint process, out uint exitCode);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DuplicateHandle(nint sourceProcess, nint source, nint targetProcess, out nint target, uint access, bool inherit, uint options);
}
