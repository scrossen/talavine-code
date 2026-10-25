using System.Text.Json.Nodes;
using SwarmCore.Coding.Routing;

namespace SwarmCode.Hosting.Tests;

public class WorkspaceSessionCatalogTests : TempDirTest
{
    private string Repo(string name) { var p = Path.Combine(Tmp, name); Directory.CreateDirectory(p); return p; }

    [Fact]
    public async Task Create_List_FiltersByRoot_CaseInsensitive_TrailingSlash()
    {
        var catalog = new WorkspaceSessionCatalog(Paths);
        var a = Repo("RepoA"); var b = Repo("RepoB");
        await catalog.CreateAsync(a);
        await catalog.CreateAsync(b);
        Assert.Equal(2, (await catalog.ListAsync()).Count);

        var forA = await catalog.ListAsync(a.ToUpperInvariant() + Path.DirectorySeparatorChar);
        var s = Assert.Single(forA);
        Assert.Equal("New session", s.Title);
        Assert.Single(await catalog.ListAsync(a.ToLowerInvariant() + "/"));
    }

    [Fact]
    public async Task A_damaged_index_is_moved_aside_and_the_catalog_keeps_working()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.SessionIndexPath)!);
        File.WriteAllText(Paths.SessionIndexPath, "{\"sessions\": [ {\"id\": ");
        var catalog = new WorkspaceSessionCatalog(Paths);

        Assert.Empty(await catalog.ListAsync());
        var quarantined = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(Paths.SessionIndexPath)!, "index.json.corrupt-*"));
        Assert.Equal("{\"sessions\": [ {\"id\": ", File.ReadAllText(quarantined));

        var created = await catalog.CreateAsync(Repo("r"));
        Assert.Equal(created.Id, Assert.Single(await catalog.ListAsync()).Id);
    }

    [Fact]
    public async Task Cached_reads_still_see_writes_from_another_catalog_instance()
    {
        var first = new WorkspaceSessionCatalog(Paths);
        var second = new WorkspaceSessionCatalog(Paths); // another process, in effect
        var a = await first.CreateAsync(Repo("a"));
        Assert.Single(await second.ListAsync());

        var b = await second.CreateAsync(Repo("b"));
        await second.RenameAsync(a.Id, "Renamed elsewhere");

        Assert.Equal(2, (await first.ListAsync()).Count);
        Assert.Equal("Renamed elsewhere", (await first.GetAsync(a.Id))!.Title);
        Assert.NotNull(await first.GetAsync(b.Id));
    }

    [Fact]
    public async Task Touch_SetsLane_AndReplacesDefaultTitleOnce()
    {
        var catalog = new WorkspaceSessionCatalog(Paths);
        var s = await catalog.CreateAsync(Repo("r"));
        var longLine = new string('x', 100);
        await catalog.TouchAsync(s.Id, "claude", longLine + "\nsecond line");
        var t = (await catalog.GetAsync(s.Id))!;
        Assert.Equal("claude", t.LaneName);
        Assert.Equal(60, t.Title.Length);
        Assert.EndsWith("...", t.Title);

        await catalog.TouchAsync(s.Id, null, "different");
        var u = (await catalog.GetAsync(s.Id))!;
        Assert.Equal(t.Title, u.Title);
        Assert.Equal("claude", u.LaneName);
    }

    [Fact]
    public async Task Touch_ShortHint_UsesFirstLine()
    {
        var catalog = new WorkspaceSessionCatalog(Paths);
        var s = await catalog.CreateAsync(Repo("r"));
        await catalog.TouchAsync(s.Id, titleHint: "fix the bug\nplease");
        Assert.Equal("fix the bug", (await catalog.GetAsync(s.Id))!.Title);
    }

    [Fact]
    public async Task Rename_Delete_AndPersistence()
    {
        var catalog = new WorkspaceSessionCatalog(Paths);
        var r = Repo("r");
        var s1 = await catalog.CreateAsync(r);
        var s2 = await catalog.CreateAsync(r, "keep");
        await catalog.RenameAsync(s1.Id, "  renamed  ");
        await catalog.DeleteAsync(s2.Id);

        var other = new WorkspaceSessionCatalog(Paths);
        var list = await other.ListAsync();
        var only = Assert.Single(list);
        Assert.Equal("renamed", only.Title);
        Assert.Equal(s1.Id, only.Id);
    }

    [Fact]
    public async Task RecentRepositories_MostRecentFirst_CappedAt20()
    {
        var catalog = new WorkspaceSessionCatalog(Paths);
        for (var i = 0; i < 25; i++) await catalog.RememberRepositoryAsync(Repo("r" + i));
        var recent = await catalog.RecentRepositoriesAsync();
        Assert.Equal(20, recent.Count);
        Assert.EndsWith("r24", recent[0]);
        Assert.EndsWith("r5", recent[^1]);

        await catalog.RememberRepositoryAsync(Repo("r10").ToUpperInvariant());
        Assert.EndsWith("R10", (await catalog.RecentRepositoriesAsync())[0]);
        Assert.Equal(20, (await catalog.RecentRepositoriesAsync()).Count);

        await catalog.ForgetRepositoryAsync(Repo("r24"));
        Assert.Equal(19, (await new WorkspaceSessionCatalog(Paths).RecentRepositoriesAsync()).Count);
    }

    [Fact]
    public async Task Changed_Fires_OnMutations()
    {
        var catalog = new WorkspaceSessionCatalog(Paths);
        var count = 0;
        catalog.Changed += () => count++;
        var s = await catalog.CreateAsync(Repo("r"));
        await catalog.RenameAsync(s.Id, "x");
        await catalog.DeleteAsync(s.Id);
        Assert.Equal(3, count);
    }

    [Fact]
    public async Task CorruptIndex_IsPreserved_AndCannotBeOverwrittenAsEmpty()
    {
        // The damaged bytes are never overwritten: a write first moves them aside intact, then starts an empty index.
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.SessionIndexPath)!);
        File.WriteAllText(Paths.SessionIndexPath, "{broken");
        var catalog = new WorkspaceSessionCatalog(Paths);
        await catalog.CreateAsync(Tmp);
        var quarantined = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(Paths.SessionIndexPath)!, "index.json.corrupt-*"));
        Assert.Equal("{broken", File.ReadAllText(quarantined));
    }
}

public class PreferencesTests : TempDirTest
{
    [Fact]
    public void Update_Persists_AndReloads()
    {
        var store = new AppPreferencesStore(Paths);
        var fired = 0;
        store.Changed += () => fired++;
        Assert.Equal("system", store.Current.Theme);
        store.Update(p => { p.Theme = "dark"; p.RequireApproval = true; p.DefaultLane = "codex"; p.Zoom = 1.25; });
        Assert.Equal(1, fired);

        var fresh = new AppPreferencesStore(Paths).Current;
        Assert.Equal("dark", fresh.Theme);
        Assert.True(fresh.RequireApproval);
        Assert.Equal("codex", fresh.DefaultLane);
        Assert.Equal(1.25, fresh.Zoom);
    }

    [Fact]
    public void CorruptFile_FallsBackToDefaults()
    {
        File.WriteAllText(Paths.PreferencesPath, "nope");
        Assert.Equal("system", new AppPreferencesStore(Paths).Current.Theme);
    }

    [Fact]
    public void WriteLanes_PreservesOtherSections()
    {
        File.WriteAllText(Paths.UserConfigPath, "{\"Logging\":{\"LogLevel\":{\"Default\":\"Warning\"}}}");
        var settings = new UserSettingsFile(Paths);
        var lanes = new CodingLaneOptions { DefaultLane = "a" };
        lanes.Lanes.Add(new CodingLane("a", "OpenAI", "m1") { Priority = 2 });
        settings.WriteLanes(lanes);

        var root = JsonNode.Parse(File.ReadAllText(Paths.UserConfigPath))!.AsObject();
        Assert.Equal("Warning", (string?)root["Logging"]!["LogLevel"]!["Default"]);
        Assert.NotNull(root[CodingLaneOptions.SectionName]);

        var read = settings.ReadLanes();
        Assert.Equal("a", read.DefaultLane);
        var lane = Assert.Single(read.Lanes);
        Assert.Equal("OpenAI", lane.Provider);
        Assert.Equal(2, lane.Priority);
    }

    [Fact]
    public void WriteLanes_Empty_RemovesSection_KeepsOthers()
    {
        File.WriteAllText(Paths.UserConfigPath, "{\"Logging\":{\"X\":1}}");
        var settings = new UserSettingsFile(Paths);
        var lanes = new CodingLaneOptions();
        lanes.Lanes.Add(new CodingLane("a", "P", "m"));
        settings.WriteLanes(lanes);
        settings.WriteLanes(new CodingLaneOptions());

        var root = JsonNode.Parse(File.ReadAllText(Paths.UserConfigPath))!.AsObject();
        Assert.False(root.ContainsKey(CodingLaneOptions.SectionName));
        Assert.True(root.ContainsKey("Logging"));
        Assert.Empty(settings.ReadLanes().Lanes);
    }

    [Fact]
    public void ReadValue_WriteValue_RoundTrip()
    {
        var settings = new UserSettingsFile(Paths);
        Assert.Null(settings.ReadValue("Coding", "Foo"));
        settings.WriteValue("Coding", "Foo", "bar");
        settings.WriteValue("Coding", "Other", "z");
        Assert.Equal("bar", new UserSettingsFile(Paths).ReadValue("Coding", "Foo"));
        settings.WriteValue("Coding", "Foo", null);
        Assert.Null(settings.ReadValue("Coding", "Foo"));
        Assert.Equal("z", settings.ReadValue("Coding", "Other"));
    }

    [Fact]
    public void RuntimeSettings_PreserveUnrelatedConfiguration_AndBindForTheHost()
    {
        File.WriteAllText(Paths.UserConfigPath, "{\"Logging\":{\"Keep\":true},\"Coding\":{\"AdditionalInstructions\":\"keep me\"},\"CodingLanes\":{\"DefaultLane\":\"native\"}}");
        var settings = new UserSettingsFile(Paths);
        settings.WriteRuntimeSettings(new SwarmCore.Coding.CodingExecutionLimits { Timeout = TimeSpan.FromMinutes(4), MaxToolCalls = 20, MaxCumulativeTokens = 5000 },
            new CodingAcceptanceOptions { Commands = [new() { Name = "build", FileName = "dotnet", Arguments = ["build", "--no-restore"] }] },
            new SwarmCore.Coding.Computer.CodingComputerOptions { BrowserProfileDirectory = "profile-folder", ChromeCdpEndpoint = "http://127.0.0.1:9222" });
        Assert.Equal("keep me", settings.ReadValue("Coding", "AdditionalInstructions"));
        Assert.Equal("native", settings.ReadLanes().DefaultLane);
        Assert.True((bool)JsonNode.Parse(File.ReadAllText(Paths.UserConfigPath))!["Logging"]!["Keep"]!);
        var limits = settings.ReadCodingSection<SwarmCore.Coding.CodingExecutionLimits>("ExecutionLimits");
        Assert.Equal(TimeSpan.FromMinutes(4), limits.Timeout);
        Assert.Equal(5000, limits.MaxCumulativeTokens);
        var config = SwarmCodeHostComposer.BuildConfiguration(Paths);
        Assert.Equal("20", config["Coding:ExecutionLimits:MaxToolCalls"]);
        Assert.Equal("build", config["Coding:Acceptance:Commands:0:Arguments:0"]);
        Assert.Equal("http://127.0.0.1:9222", config["Coding:Computer:ChromeCdpEndpoint"]);
    }

    [Fact]
    public async Task DefaultModelPersistsAndIsCopiedIntoOnlyNewSessions()
    {
        var prefs = new AppPreferencesStore(Paths);
        prefs.Update(p => { p.DefaultLane = "claude"; p.DefaultModel = "chosen-model"; });
        var loaded = new AppPreferencesStore(Paths).Current;
        Assert.Equal("claude", loaded.DefaultLane);
        Assert.Equal("chosen-model", loaded.DefaultModel);
        var catalog = new WorkspaceSessionCatalog(Paths);
        var existing = await catalog.CreateAsync(Tmp, laneName: "claude");
        var added = await catalog.CreateAsync(Tmp, laneName: loaded.DefaultLane, model: loaded.DefaultModel);
        Assert.Equal("chosen-model", added.ModelChoices["claude"]);
        prefs.Update(p => p.DefaultModel = "replacement");
        var next = await catalog.CreateAsync(Tmp, laneName: prefs.Current.DefaultLane, model: prefs.Current.DefaultModel);
        var reloaded = new WorkspaceSessionCatalog(Paths);
        Assert.Empty((await reloaded.GetAsync(existing.Id))!.ModelChoices);
        Assert.Equal("chosen-model", (await reloaded.GetAsync(added.Id))!.ModelChoices["claude"]);
        Assert.Equal("replacement", next.ModelChoices["claude"]);
        Assert.Empty((await catalog.CreateAsync(Tmp, model: "ignored-without-a-lane")).ModelChoices);
    }

    [Fact]
    public void Claude_ai_connectors_are_off_until_the_user_allows_them_and_the_setting_reaches_the_native_options()
    {
        File.WriteAllText(Paths.UserConfigPath, "{\"Coding\":{\"NativeAgents\":{\"AllowCodexAppConnectors\":true}}}");
        var settings = new UserSettingsFile(Paths);
        Assert.False(settings.ReadAllowClaudeAiConnectors());
        Assert.False(Bound().NativeAgents.AllowClaudeAiConnectors);

        settings.WriteAllowClaudeAiConnectors(true);
        Assert.True(Bound().NativeAgents.AllowClaudeAiConnectors);
        Assert.True(Bound().NativeAgents.AllowCodexAppConnectors); // the other switch survives

        settings.WriteAllowClaudeAiConnectors(false);
        Assert.False(settings.ReadAllowClaudeAiConnectors());

        SwarmCore.Coding.CodingOptions Bound()
        {
            var coding = new SwarmCore.Coding.CodingOptions();
            Microsoft.Extensions.Configuration.ConfigurationBinder.Bind(
                SwarmCodeHostComposer.BuildConfiguration(Paths).GetSection(SwarmCore.Coding.CodingOptions.SectionName), coding);
            return coding;
        }
    }

    [Fact]
    public void Codex_app_connectors_are_off_until_the_user_allows_them_and_the_setting_reaches_the_native_options()
    {
        File.WriteAllText(Paths.UserConfigPath, "{\"Coding\":{\"NativeAgents\":{\"CodexSandbox\":\"bypass\"},\"AdditionalInstructions\":\"keep me\"}}");
        var settings = new UserSettingsFile(Paths);
        Assert.False(settings.ReadAllowCodexAppConnectors());
        Assert.False(Bound().NativeAgents.AllowCodexAppConnectors);

        settings.WriteAllowCodexAppConnectors(true);
        Assert.True(settings.ReadAllowCodexAppConnectors());
        Assert.True(Bound().NativeAgents.AllowCodexAppConnectors);
        Assert.Equal("bypass", Bound().NativeAgents.CodexSandbox); // the rest of the section survives
        Assert.Equal("keep me", settings.ReadValue("Coding", "AdditionalInstructions"));

        settings.WriteAllowCodexAppConnectors(false);
        Assert.False(Bound().NativeAgents.AllowCodexAppConnectors);

        SwarmCore.Coding.CodingOptions Bound()
        {
            var coding = new SwarmCore.Coding.CodingOptions();
            Microsoft.Extensions.Configuration.ConfigurationBinder.Bind(
                SwarmCodeHostComposer.BuildConfiguration(Paths).GetSection(SwarmCore.Coding.CodingOptions.SectionName), coding);
            return coding;
        }
    }
}
