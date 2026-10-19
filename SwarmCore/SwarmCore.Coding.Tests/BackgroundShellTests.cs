using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Coding.Tools.BackgroundShell;
using SwarmCore.Missions;
using SwarmCore.Services.Abstractions;
using Xunit;

namespace SwarmCore.Coding.Tests;

public sealed class BackgroundShellTests : IDisposable
{
    private readonly string _root = SandboxPathResolver.ResolvePhysicalPath(Directory.CreateTempSubdirectory("bgshell-").FullName);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a killed child may still hold a handle briefly */ }
    }

    private static BackgroundShellManager Manager(BackgroundShellOptions? options = null, ICredentialStore? credentials = null)
    {
        var o = options ?? new BackgroundShellOptions();
        return new BackgroundShellManager(() => o, () => credentials);
    }

    /// <summary>Platform-shell sleep without relying on PATH tools beyond the system directory.</summary>
    private static string Sleep(int seconds) => OperatingSystem.IsWindows()
        ? $"\"{Path.Combine(Environment.SystemDirectory, "PING.EXE")}\" -n {seconds + 1} 127.0.0.1 >nul"
        : $"sleep {seconds}";

    private static string Then => OperatingSystem.IsWindows() ? " & " : "; ";

    private string BlockedDescendant(string marker, string release)
    {
        var script = Path.Combine(_root, Guid.NewGuid().ToString("N") + (OperatingSystem.IsWindows() ? ".cmd" : ".sh"));
        var contents = OperatingSystem.IsWindows()
            ? $"@echo off\r\necho child-ready\r\n:wait\r\nif exist \"{release}\" goto write\r\n{Sleep(1)}\r\ngoto wait\r\n:write\r\necho late>\"{marker}\"\r\n"
            : $"echo child-ready\nwhile [ ! -f \"{release}\" ]; do sleep 0.05; done\necho late > \"{marker}\"\n";
        File.WriteAllText(script, contents);
        return OperatingSystem.IsWindows() ? $"cmd.exe /d /c call \"{script}\"" : $"/bin/sh \"{script}\"";
    }

    [Fact]
    public async Task Descendant_fixture_writes_only_after_its_release_signal()
    {
        using var manager = Manager();
        var marker = Path.Combine(_root, "control.txt");
        var release = Path.Combine(_root, "release-control.txt");
        var shell = await manager.StartAsync("conv", BlockedDescendant(marker, release), _root);
        Assert.True((await manager.ReadAsync("conv", shell.Id, "child-ready", 20)).PatternMatched);
        Assert.False(File.Exists(marker));
        await File.WriteAllTextAsync(release, "go");
        var result = await manager.ReadAsync("conv", shell.Id, null, 20);
        Assert.Equal(BackgroundShellStatus.Exited, result.Shell.Status);
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public async Task Start_returns_at_once_while_a_long_command_keeps_running()
    {
        using var manager = Manager();
        var watch = Stopwatch.StartNew();
        var shell = await manager.StartAsync("conv", "echo started" + Then + Sleep(30), _root, "server");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Equal(BackgroundShellStatus.Running, shell.Status);
        Assert.Equal("server", shell.Name);

        var read = await manager.ReadAsync("conv", shell.Id, "started", 20);
        Assert.True(read.PatternMatched);
        Assert.Contains("started", read.Output);
        Assert.True(read.Shell.IsRunning);
        Assert.Equal(BackgroundShellStatus.Stopped, manager.Stop("conv", shell.Id).Status);
    }

    [Fact]
    public async Task Output_reads_are_incremental_and_report_the_exit_code()
    {
        using var manager = Manager();
        var command = OperatingSystem.IsWindows()
            ? "echo one & echo two 1>&2 & " + Sleep(2) + " & echo three & exit /b 3"
            : "echo one; echo two 1>&2; " + Sleep(2) + "; echo three; exit 3";
        var shell = await manager.StartAsync("conv", command, _root);

        // stdout and stderr are pumped independently; wait for both before taking the first read.
        var first = await manager.ReadAsync("conv", shell.Id, "(?s)(?:one.*two|two.*one)", 20);
        Assert.Contains("one", first.Output);
        Assert.Contains("two", first.Output); // stderr is merged

        var rest = await manager.ReadAsync("conv", shell.Id, null, 30); // timeout alone waits for the exit
        Assert.DoesNotContain("one", rest.Output);
        Assert.Contains("three", rest.Output);
        Assert.Equal(BackgroundShellStatus.Exited, rest.Shell.Status);
        Assert.Equal(3, rest.Shell.ExitCode);
        Assert.False(rest.TimedOut);

        var empty = await manager.ReadAsync("conv", shell.Id);
        Assert.Equal("", empty.Output);
    }

    [SwarmCore.Testing.PosixFact]
    public async Task Pattern_wait_matches_across_separately_delivered_output()
    {
        using var manager = Manager();
        var shell = await manager.StartAsync("conv",
            "echo first; while [ ! -f release ]; do sleep 0.02; done; echo second; sleep 30", _root);
        var deadline = Stopwatch.StartNew();
        while (!manager.Peek("conv", shell.Id).Contains("first") && deadline.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(10);
        Assert.Contains("first", manager.Peek("conv", shell.Id));

        // ReadAsync scans the first chunk before awaiting the next output signal.
        var pending = manager.ReadAsync("conv", shell.Id, "(?s)first.*second", 2);
        Assert.False(pending.IsCompleted);
        File.WriteAllText(Path.Combine(_root, "release"), "go");
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.PatternMatched);
        Assert.False(result.TimedOut);
        Assert.True(result.Shell.IsRunning);
        Assert.Contains("first", result.Output);
        Assert.Contains("second", result.Output);
    }

    [Fact]
    public async Task Waiting_for_a_pattern_that_never_appears_returns_after_the_timeout()
    {
        using var manager = Manager();
        var shell = await manager.StartAsync("conv", "echo booting" + Then + Sleep(30), _root);
        var watch = Stopwatch.StartNew();
        var read = await manager.ReadAsync("conv", shell.Id, "never-printed", 1);
        Assert.False(read.PatternMatched);
        Assert.True(read.TimedOut);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.ReadAsync("conv", shell.Id, "(", 1));
        manager.Stop("conv", shell.Id);
    }

    [Fact]
    public async Task Waits_are_capped_by_the_options()
    {
        using var manager = Manager(new BackgroundShellOptions { MaxWaitSeconds = 1 });
        var shell = await manager.StartAsync("conv", Sleep(30), _root);
        var watch = Stopwatch.StartNew();
        var read = await manager.ReadAsync("conv", shell.Id, null, 600);
        Assert.True(read.TimedOut);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
        manager.Stop("conv", shell.Id);
    }

    [Fact]
    public async Task Stop_kills_the_whole_process_tree()
    {
        using var manager = Manager();
        var marker = Path.Combine(_root, "marker.txt");
        var release = Path.Combine(_root, "release-stop.txt");
        // A signal after Stop prevents a busy test host from spending the old
        // three-second delay before it even calls Stop. Wait for the descendant,
        // not the parent, so the assertion actually exercises the whole tree.
        var shell = await manager.StartAsync("conv", BlockedDescendant(marker, release), _root);
        Assert.True((await manager.ReadAsync("conv", shell.Id, "child-ready", 20)).PatternMatched);

        Assert.Equal(BackgroundShellStatus.Stopped, manager.Stop("conv", shell.Id).Status);
        await File.WriteAllTextAsync(release, "go");
        await Task.Delay(TimeSpan.FromSeconds(6));
        Assert.False(File.Exists(marker));
        Assert.Equal(BackgroundShellStatus.Stopped, manager.List("conv").Single().Status);
    }

    [Fact]
    public async Task A_conversation_sees_and_stops_only_its_own_shells()
    {
        using var manager = Manager();
        var mine = await manager.StartAsync("alice", Sleep(30), _root);
        var theirs = await manager.StartAsync("bob", Sleep(30), _root);

        Assert.Equal([mine.Id], manager.List("alice").Select(s => s.Id));
        Assert.Throws<InvalidOperationException>(() => manager.Stop("alice", theirs.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ReadAsync("alice", theirs.Id));
        Assert.Throws<InvalidOperationException>(() => manager.Peek("alice", theirs.Id));
        Assert.True(manager.List("bob").Single().IsRunning);
        Assert.Throws<InvalidOperationException>(() => manager.List(""));

        // Session deletion stops and forgets only that conversation's shells.
        Assert.Equal(1, manager.StopAll("alice"));
        Assert.Empty(manager.List("alice"));
        Assert.True(manager.List("bob").Single().IsRunning);
        manager.Stop("bob", theirs.Id);
    }

    [Fact]
    public async Task Limits_apply_per_conversation_and_in_total()
    {
        using var manager = Manager(new BackgroundShellOptions { MaxRunningPerConversation = 1, MaxRunningTotal = 2, RetainFinishedPerConversation = 1 });
        var a = await manager.StartAsync("a", Sleep(30), _root);
        var perConversation = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync("a", Sleep(30), _root));
        Assert.Contains("shell.background.stop", perConversation.Message);
        await manager.StartAsync("b", Sleep(30), _root);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync("c", Sleep(30), _root));

        // A stopped shell frees its slot; finished shells beyond the retention are forgotten.
        manager.Stop("a", a.Id);
        var a2 = await manager.StartAsync("a", "echo quick", _root);
        await manager.ReadAsync("a", a2.Id, null, 20);
        await manager.StartAsync("a", Sleep(30), _root);
        Assert.Equal(2, manager.List("a").Count); // one running + one retained finished
        Assert.DoesNotContain(manager.List("a"), s => s.Id == a.Id);
        Assert.Equal(2, manager.RunningCount);
    }

    [Fact]
    public async Task Secret_references_are_expanded_at_launch_and_scrubbed_from_output()
    {
        var store = new Credentials { ["api-token"] = "s3cr3t-value-123" };
        using var manager = Manager(credentials: store);
        var shell = await manager.StartAsync("conv", "echo token={{secret:api-token}}", _root);
        var read = await manager.ReadAsync("conv", shell.Id, null, 20);
        Assert.Contains("token={{secret:api-token}}", read.Output);
        Assert.DoesNotContain("s3cr3t-value-123", read.Output);
        Assert.DoesNotContain("s3cr3t-value-123", manager.Peek("conv", shell.Id));
        Assert.Contains("{{secret:api-token}}", read.Shell.Command);

        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync("conv", "echo {{secret:nope}}", _root));
        Assert.Contains("api-token", missing.Message);
    }

    [Fact]
    public async Task Long_output_keeps_head_and_tail_within_the_buffer()
    {
        using var manager = Manager(new BackgroundShellOptions { HeadCharacters = 64, TailCharacters = 256, MaxReadCharacters = 1024 });
        var command = OperatingSystem.IsWindows()
            ? "for /L %i in (1,1,400) do @echo line%i"
            : "i=1; while [ $i -le 400 ]; do echo line$i; i=$((i+1)); done";
        var shell = await manager.StartAsync("conv", command, _root);
        var read = await manager.ReadAsync("conv", shell.Id, null, 30);
        Assert.StartsWith("line1", read.Output.TrimStart());
        Assert.Contains("line400", read.Output);
        Assert.Contains("characters omitted", read.Output);
        Assert.True(read.OmittedCharacters > 0);
        Assert.True(read.Output.Length < 1024);
    }

    [Fact]
    public async Task Disposing_the_manager_kills_every_shell()
    {
        var manager = Manager();
        var marker = Path.Combine(_root, "disposed.txt");
        var release = Path.Combine(_root, "release-dispose.txt");
        var a = await manager.StartAsync("a", BlockedDescendant(marker, release), _root);
        await manager.StartAsync("b", Sleep(30), _root);
        Assert.True((await manager.ReadAsync("a", a.Id, "child-ready", 20)).PatternMatched);
        Assert.Equal(2, manager.RunningCount);

        await manager.DisposeAsync();
        await File.WriteAllTextAsync(release, "go");
        Assert.Equal(0, manager.RunningCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.StartAsync("a", "echo no", _root));
        await Task.Delay(TimeSpan.FromSeconds(6));
        Assert.False(File.Exists(marker));
    }
    [Fact]
    public void Output_buffer_reports_dropped_middle_output()
    {
        var buffer = new ShellOutputBuffer(4, 8);
        buffer.Append("abcdefghijklmnopqrstuvwxyz");
        Assert.Equal(26, buffer.Written);
        var all = buffer.Read(0, 1000, out var omitted);
        Assert.StartsWith("abcd", all);
        Assert.EndsWith("stuvwxyz", all);
        Assert.Equal(26 - 4 - 8, omitted);
        Assert.Equal("yz", buffer.Read(24, 1000, out omitted));
        Assert.Equal(0, omitted);
    }

    [Fact]
    public async Task Tools_act_for_the_trusted_owner_and_fence_the_working_directory()
    {
        using var manager = Manager();
        string? owner = "conv";
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        var tools = BackgroundShellTools.Create(manager, () => owner, cwd => BackgroundShellTools.ResolveUnder(_root, cwd));
        var start = tools.Single(t => t.ToolId == BackgroundShellTools.StartToolId);
        var output = tools.Single(t => t.ToolId == BackgroundShellTools.OutputToolId);
        var list = tools.Single(t => t.ToolId == BackgroundShellTools.ListToolId);

        var started = (BackgroundShellResult)(await start.Invoke(new BackgroundShellStartInput { Command = OperatingSystem.IsWindows() ? "cd" : "pwd", Cwd = "sub" }, default))!;
        Assert.True(started.Success, started.Message);
        var read = (BackgroundShellResult)(await output.Invoke(new BackgroundShellOutputInput { Id = started.Shells[0].Id, TimeoutSeconds = 20 }, default))!;
        Assert.Contains("sub", read.Output);

        var escaped = (BackgroundShellResult)(await start.Invoke(new BackgroundShellStartInput { Command = "echo x", Cwd = ".." }, default))!;
        Assert.False(escaped.Success);

        owner = null;
        var noTurn = (BackgroundShellResult)(await list.Invoke(new BackgroundShellListInput(), default))!;
        Assert.False(noTurn.Success);
        Assert.DoesNotContain(BackgroundShellTools.StartToolId, CodingToolRegistrar.CodingToolIds);
    }

    [Fact]
    public async Task Opt_in_registration_publishes_the_tools_and_disposal_stops_shells()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSwarmCoreCodingHarness();
        services.Configure<SandboxOptions>(o => o.Root = _root);
        await using (var bare = services.BuildServiceProvider())
        {
            foreach (var hosted in bare.GetServices<IHostedService>()) await hosted.StartAsync(default);
            Assert.DoesNotContain(bare.GetRequiredService<ICapabilityCatalog>().All(), t => t.ToolId.StartsWith("shell.background.", StringComparison.Ordinal));
        }

        services.AddSwarmCoreBackgroundShell(o => o.MaxRunningTotal = 3);
        BackgroundShellManager manager;
        await using (var sp = services.BuildServiceProvider())
        {
            foreach (var hosted in sp.GetServices<IHostedService>()) await hosted.StartAsync(default);
            var catalog = sp.GetRequiredService<ICapabilityCatalog>();
            foreach (var id in BackgroundShellTools.AllToolIds) Assert.NotNull(catalog.All().FirstOrDefault(t => t.ToolId == id));
            manager = sp.GetRequiredService<BackgroundShellManager>();
            await manager.StartAsync("conv", Sleep(30), _root);
            Assert.Equal(1, manager.RunningCount);
        }
        Assert.Equal(0, manager.RunningCount);
    }

    [Fact]
    public async Task Native_bridge_serves_shells_for_the_launching_conversation_only()
    {
        // The real composition: catalog tools, the coding profile, and the host tool bridge serving them to native lanes.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSwarmCoreCoding(o => o.RepositoryRoot = _root);
        services.AddSwarmCoreBackgroundShell();
        Native.HostToolBridgeServiceCollectionExtensions.AddSwarmCoreHostToolBridge(services);
        await using var sp = services.BuildServiceProvider();
        foreach (var hosted in sp.GetServices<Microsoft.Extensions.Hosting.IHostedService>()) await hosted.StartAsync(default);
        var broker = sp.GetRequiredService<Native.HostToolBridgeBroker>();
        var active = sp.GetRequiredService<ActiveCodingWorkspaces>();
        using var rootTurn = active.Enter("root", new CodingWorkspace { RepositoryRoot = _root });
        using var otherTurn = active.Enter("other", new CodingWorkspace { RepositoryRoot = _root });
        static System.Text.Json.JsonElement Args(object value) => System.Text.Json.JsonSerializer.SerializeToElement(value);

        var root = new Native.HostToolBridgeProxy(broker.GetEndpoint(), "root");
        var started = await root.CallAsync("shell_background_start", Args(new { command = "echo bridged", name = "bridge" }), default);
        Assert.False(started.IsError, started.Text);
        var manager = sp.GetRequiredService<BackgroundShellManager>();
        var shell = Assert.Single(manager.List("root"));
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(_root)), Path.TrimEndingDirectorySeparator(shell.WorkingDirectory), ignoreCase: OperatingSystem.IsWindows());

        var output = await root.CallAsync("shell_background_output", Args(new { id = shell.Id, timeoutSeconds = 20 }), default);
        Assert.Contains("bridged", output.Text);

        // Another conversation cannot stop this conversation's shell.
        var other = new Native.HostToolBridgeProxy(broker.GetEndpoint(), "other");
        Assert.True((await other.CallAsync("shell_background_stop", Args(new { id = shell.Id }), default)).IsError);
        Assert.Empty(manager.List("other"));

        // The working directory stays inside the conversation's workspace.
        Assert.True((await root.CallAsync("shell_background_start", Args(new { command = "echo x", cwd = ".." }), default)).IsError);
    }

    private sealed class Credentials : Dictionary<string, string>, ICredentialStore
    {
        public Task SetAsync(string name, string secret, CancellationToken ct = default) { this[name] = secret; return Task.CompletedTask; }
        public Task<string?> GetAsync(string name, CancellationToken ct = default) => Task.FromResult(TryGetValue(name, out var v) ? v : null);
        public Task<bool> HasAsync(string name, CancellationToken ct = default) => Task.FromResult(ContainsKey(name));
        public Task DeleteAsync(string name, CancellationToken ct = default) { Remove(name); return Task.CompletedTask; }
        public Task<IReadOnlyList<string>> ListNamesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>(Keys.ToList());
    }
}
