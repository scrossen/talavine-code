using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using SwarmCode.Hosting.Projects;
using SwarmCore.Agent.Procedures;
using SwarmCore.Coding.Projects;
using SwarmCore.Coding.Permissions;

namespace SwarmCode.Hosting.Tests;

[Trait("LlmMode", "Mock")]
[Trait("DatabaseMode", "Ephemeral")]
public sealed class MyAppsTests : TempDirTest
{
    private CodingAppLibraryStore Store => new(Paths);
    private string Folder()
    {
        var folder = Path.Combine(Tmp, "My apps");
        Directory.CreateDirectory(folder);
        Store.SetFolder(folder, null);
        return folder;
    }
    private CodingAppProject App()
    {
        var folder = Folder();
        return Store.Create("My garden", "Track planting dates", folder);
    }

    [Fact]
    public void Create_add_restart_and_folder_change_keep_files_and_app_identity()
    {
        var folder = Folder();
        var app = Store.Create("Garden planner", "Track planting dates", folder);
        Assert.True(Directory.Exists(app.Root));
        Assert.Equal(Path.Combine(folder, app.Name), app.Root);
        var outside = Path.Combine(Tmp, "existing"); Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "notes.txt"), "Keep me");
        var added = Store.Add(outside);
        Assert.Equal(added.Id, Store.Add(outside + Path.DirectorySeparatorChar).Id);
        var nextFolder = Path.Combine(Tmp, "Next apps"); Directory.CreateDirectory(nextFolder);
        Store.SetFolder(nextFolder, folder);
        Assert.Equal(app, Store.Get(app.Id));
        Assert.Equal(2, Store.Read().Apps.Count);
        Store.Remove(added.Id);
        Assert.Equal("Keep me", File.ReadAllText(Path.Combine(outside, "notes.txt")));
        Assert.Throws<InvalidOperationException>(() => Store.Get(added.Id));
        Assert.Single(Store.Read().Apps);
    }

    [Fact]
    public async Task Independent_writers_merge_additions_and_cannot_recreate_a_removed_app()
    {
        var folder = Folder();
        var a = Store; var b = Store;
        var apps = await Task.WhenAll(Task.Run(() => a.Create("One", "first", folder)), Task.Run(() => b.Create("Two", "second", folder)));
        Assert.Equal(2, Store.Read().Apps.Count);
        var old = a.Get(apps[0].Id);
        b.Remove(old.Id);
        Assert.Throws<InvalidOperationException>(() => a.Get(old.Id));
        Assert.Single(a.Read().Apps);
        var other = Path.Combine(Tmp, "other"); Directory.CreateDirectory(other);
        b.SetFolder(other, folder);
        Assert.Throws<InvalidOperationException>(() => a.SetFolder(folder, folder));
        Assert.Throws<InvalidOperationException>(() => a.Create("Stale folder", "desc", folder));
        Assert.False(Directory.Exists(Path.Combine(folder, "Stale folder")));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..")] [InlineData(".")] [InlineData("bad:name")]
    [InlineData("NUL.txt")] [InlineData("name.")] [InlineData("")]
    public void Invalid_names_never_create_project_folders(string name)
    {
        var folder = Folder();
        Assert.Throws<ArgumentException>(() => Store.Create(name, "description", folder));
        Assert.Empty(Store.Read().Apps);
    }

    [Fact]
    public void Existing_folders_and_malformed_catalogs_fail_without_overwriting()
    {
        var app = App();
        Assert.Throws<InvalidOperationException>(() => Store.Create(app.Name, "overwrite", Store.Read().AppsFolder!));
        var path = Path.Combine(Paths.RootDirectory, "apps.json");
        File.WriteAllText(path, "{broken");
        Assert.ThrowsAny<JsonException>(() => Store.Add(app.Root));
        Assert.Equal("{broken", File.ReadAllText(path));
    }

    [Fact]
    public void Files_are_real_bounded_and_physically_contained()
    {
        var app = App();
        File.WriteAllText(Path.Combine(app.Root, "hello.txt"), "Actual source");
        File.WriteAllText(Path.Combine(app.Root, ".env"), "PRIVATE");
        Directory.CreateDirectory(Path.Combine(app.Root, "node_modules"));
        Assert.Equal("Actual source", CodingAppFiles.Read(app.Root, "hello.txt"));
        Assert.DoesNotContain(CodingAppFiles.List(app.Root), f => f.Name is ".env" or "node_modules");
        Assert.Throws<InvalidOperationException>(() => CodingAppFiles.Read(app.Root, ".env"));
        Assert.Throws<InvalidOperationException>(() => CodingAppFiles.List(app.Root, ".."));
        var external = Path.Combine(Tmp, "outside.txt"); File.WriteAllText(external, "outside");
        File.CreateSymbolicLink(Path.Combine(app.Root, "link.txt"), external);
        Assert.Throws<InvalidOperationException>(() => CodingAppFiles.Read(app.Root, "link.txt"));
        File.WriteAllBytes(Path.Combine(app.Root, "large.txt"), new byte[256 * 1024 + 1]);
        Assert.Throws<InvalidOperationException>(() => CodingAppFiles.Read(app.Root, "large.txt"));
    }

    private static void Manifest(CodingAppProject app, CodingAppLaunchPlan plan)
    {
        Directory.CreateDirectory(Path.Combine(app.Root, ".swarmcode"));
        File.WriteAllText(Path.Combine(app.Root, CodingAppLaunchFile.RelativePath), JsonSerializer.Serialize(
            new CodingAppLaunchFile.Manifest(1, new() { ["default"] = plan }), CodingAppLaunchFile.Json));
    }
    private static CodingAppLaunchPlan Worker(string mode, params string[] args)
    {
        var executable = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var dll = typeof(AppRuntimeFixture.Program).Assembly.Location;
        return new()
        {
            Program = executable,
            Arguments = ["exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "SwarmCode.Hosting.Tests.runtimeconfig.json"), dll, mode, .. args],
            ReadyWhen = mode == "http" ? "http" : mode == "sleep" ? "process" : "exit", TimeoutSeconds = 10
        };
    }
    private static int Port()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }
    private CodingAppRuntime Runtime(FileAppProcedureStore? store = null)
    { store ??= new(Paths); return new(store, Paths.RootDirectory, store.AcquireRunLease); }
    private static async Task UntilAsync(Func<bool> predicate)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (!predicate()) { Assert.True(DateTime.UtcNow < until, "App status did not settle."); await Task.Delay(30); }
    }

    [Fact]
    public async Task Real_http_app_is_verified_stopped_and_replayed_after_restart_without_a_model()
    {
        var app = App(); var port = Port();
        Manifest(app, Worker("http", port.ToString(), "400") with { Url = $"http://127.0.0.1:{port}" });
        await using (var runtime = Runtime())
        {
            var task = runtime.StartAsync(app);
            Assert.Equal(CodingAppRunState.Starting, runtime.Status(app.Id).State);
            await UntilAsync(() => runtime.Status(app.Id).State != CodingAppRunState.Starting);
            Assert.Equal(CodingAppRunState.Running, runtime.Status(app.Id).State);
            Assert.False(runtime.Status(app.Id).Replayed);
            using var client = new HttpClient();
            Assert.Contains("SwarmCode app fixture", await client.GetStringAsync(runtime.Status(app.Id).Url));
            await runtime.StopAsync(app.Id); await task;
            Assert.Equal(CodingAppRunState.Stopped, runtime.Status(app.Id).State);
        }
        await using var restarted = Runtime();
        _ = restarted.StartAsync(app);
        await UntilAsync(() => restarted.Status(app.Id).State != CodingAppRunState.Starting);
        Assert.Equal(CodingAppRunState.Running, restarted.Status(app.Id).State);
        Assert.True(restarted.Status(app.Id).Replayed);
        await restarted.StopAsync(app.Id);
        var history = await new FileAppProcedureStore(Paths).GetAsync(new(Paths.RootDirectory, "app-launch", app.Id));
        Assert.NotNull(history!.ActiveRevisionId);
        Assert.All(history.Revisions.SelectMany(r => r.Steps).SelectMany(s => s.Bindings), b => Assert.Equal(AgentProcedureValueSource.Input, b.Source));
    }

    [Fact]
    public async Task Utility_exit_code_is_verified_and_dependency_changes_create_a_new_revision()
    {
        var app = App(); Manifest(app, Worker("exit", "0"));
        await using var runtime = Runtime();
        await runtime.StartAsync(app); Assert.Equal(CodingAppRunState.Completed, runtime.Status(app.Id).State);
        File.WriteAllText(Path.Combine(app.Root, "package.json"), "{\"name\":\"changed\"}");
        await runtime.StartAsync(app); Assert.False(runtime.Status(app.Id).Replayed);
        var history = await new FileAppProcedureStore(Paths).GetAsync(new(Paths.RootDirectory, "app-launch", app.Id));
        Assert.Equal(2, history!.Revisions.Count(r => r.Verification?.VerifierId == "app-launch-readiness-v1" && r.Steps.Count > 0));
        Manifest(app, Worker("exit", "9"));
        await runtime.StartAsync(app);
        Assert.Equal(CodingAppRunState.Failed, runtime.Status(app.Id).State);
        Assert.Contains("9", runtime.Status(app.Id).Message);
    }

    [Fact]
    public async Task Double_launch_cross_host_is_rejected_and_stop_during_startup_cleans_up()
    {
        var app = App(); Manifest(app, Worker("sleep"));
        await using var first = Runtime(); await using var second = Runtime();
        var running = first.StartAsync(app);
        await UntilAsync(() => first.Status(app.Id).State == CodingAppRunState.Running);
        await second.StartAsync(app);
        Assert.Equal(CodingAppRunState.Failed, second.Status(app.Id).State);
        Assert.Contains("another Talavine Code", second.Status(app.Id).Message);
        await first.StopAsync(app.Id); await running;
        var port = Port(); Manifest(app, Worker("http", port.ToString(), "20000") with { Url = $"http://127.0.0.1:{port}" });
        _ = first.StartAsync(app); await first.StopAsync(app.Id);
        Assert.False(first.IsBusy(app.Id)); Assert.Equal(CodingAppRunState.Stopped, first.Status(app.Id).State);
        Manifest(app, Worker("exit", "0"));
        await second.StartAsync(app);
        Assert.Equal(CodingAppRunState.Completed, second.Status(app.Id).State);
    }

    [Fact]
    public async Task Occupied_port_and_changed_approval_never_launch_the_wrong_app()
    {
        var app = App(); var port = Port();
        using var listener = new TcpListener(IPAddress.Loopback, port); listener.Start();
        Manifest(app, Worker("http", port.ToString(), "0") with { Url = $"http://127.0.0.1:{port}" });
        await using var runtime = Runtime();
        await runtime.StartAsync(app); Assert.Contains("already in use", runtime.Status(app.Id).Message);
        var marker = Path.Combine(app.Root, "ran.txt");
        var reviewed = Worker("exit", "0"); Manifest(app, Worker("write", marker, "unexpected"));
        await runtime.StartAsync(app, approvedPlan: JsonSerializer.Serialize(reviewed, CodingAppLaunchFile.Json));
        Assert.False(File.Exists(marker)); Assert.Contains("changed after review", runtime.Status(app.Id).Message);
    }

    [Theory]
    [InlineData("http://example.com")] [InlineData("file:///tmp/test")] [InlineData("javascript:alert(1)")] [InlineData("http://name:password@localhost:1234")]
    public void Preview_urls_cannot_contact_remote_hosts_or_include_credentials(string url)
    {
        var app = App();
        Assert.Throws<InvalidDataException>(() => new CodingAppLaunchPlan { Program = "test", Url = url }.Validate(app.Root));
    }

    [Fact]
    public async Task Procedure_CAS_rejects_competing_writes_and_profiles_are_isolated()
    {
        var a = new FileAppProcedureStore(Paths); var b = new FileAppProcedureStore(Paths);
        var key = new AgentProcedureKey("profile-a", "app-launch", "app");
        var revision = new AgentProcedureHistory(key, 1, null, []);
        var results = await Task.WhenAll(Task.Run(() => a.CompareExchangeAsync(key, 0, revision)), Task.Run(() => b.CompareExchangeAsync(key, 0, revision)));
        Assert.Single(results.Where(r => r));
        Assert.Null(await a.GetAsync(key with { ProfileId = "profile-b" }));
    }

    [Fact]
    public async Task Readiness_timeout_and_missing_executable_leave_no_replay_barrier_for_a_corrected_plan()
    {
        var app = App(); var port = Port();
        Manifest(app, Worker("http", port.ToString(), "10000") with { Url = $"http://127.0.0.1:{port}", TimeoutSeconds = 1 });
        await using var runtime = Runtime();
        await runtime.StartAsync(app); Assert.Equal(CodingAppRunState.Failed, runtime.Status(app.Id).State);
        Assert.Contains("in time", runtime.Status(app.Id).Message);
        Manifest(app, new() { Program = Path.Combine(app.Root, "missing-program"), ReadyWhen = "exit" });
        await runtime.StartAsync(app); Assert.Equal(CodingAppRunState.Failed, runtime.Status(app.Id).State);
        Manifest(app, Worker("exit", "0"));
        await runtime.StartAsync(app); Assert.Equal(CodingAppRunState.Completed, runtime.Status(app.Id).State);
    }

    [Fact]
    public async Task An_uncertain_earlier_launch_cannot_be_replayed_or_cleared_by_a_new_attempt()
    {
        var app = App(); var marker = Path.Combine(app.Root, "must-not-run.txt");
        Manifest(app, Worker("write", marker, "wrong"));
        var store = new FileAppProcedureStore(Paths);
        var key = new AgentProcedureKey(Paths.RootDirectory, "app-launch", app.Id);
        var revision = new AgentProcedureRevision(Guid.NewGuid(), null, "prior", "prior", [], DateTimeOffset.UtcNow,
            AgentProcedureRevisionState.Rejected, FailureCode: "outcome_uncertain");
        Assert.True(await store.CompareExchangeAsync(key, 0, new(key, 1, null, [revision])));
        await using var runtime = Runtime(store);
        await runtime.StartAsync(app);
        Assert.Equal(CodingAppRunState.Failed, runtime.Status(app.Id).State);
        Assert.Contains("inspection", runtime.Status(app.Id).Message);
        Assert.False(File.Exists(marker));
        Assert.Equal(1, (await store.GetAsync(key))!.Version);
    }

    [Fact]
    public void Launch_honors_current_permission_modes_and_both_app_and_shell_deny_rules()
    {
        Assert.Equal(CodingPermissionVerdict.Run, CodingAppLaunchAuthority.Decide(CodingPermissionPolicy.AutoNoRules).Verdict);
        Assert.Equal(CodingPermissionVerdict.Ask, CodingAppLaunchAuthority.Decide(new() { Mode = CodingPermissionMode.AskRisky }).Verdict);
        Assert.Equal(CodingPermissionVerdict.Deny, CodingAppLaunchAuthority.Decide(new() { Allow = ["apps.*"], Deny = ["shell.*"] }).Verdict);
        Assert.Equal(CodingPermissionVerdict.Deny, CodingAppLaunchAuthority.Decide(new() { Deny = ["apps.*"] }).Verdict);
    }

    [Fact]
    public async Task Authority_is_rechecked_at_dispatch_and_UI_observers_cannot_break_cleanup()
    {
        var app = App(); var marker = Path.Combine(app.Root, "not-authorized.txt");
        Manifest(app, Worker("write", marker, "unexpected"));
        var runtime = Runtime();
        runtime.Changed += () => throw new ObjectDisposedException("Departing page");
        await runtime.StartAsync(app, authorize: _ => throw new InvalidOperationException("Permission changed"));
        Assert.False(File.Exists(marker)); Assert.False(runtime.IsBusy(app.Id));
        Assert.Equal(CodingAppRunState.Failed, runtime.Status(app.Id).State);
        await runtime.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await runtime.StartAsync(app));
    }
}
