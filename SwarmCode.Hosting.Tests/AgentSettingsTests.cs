using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SwarmCode.Hosting.Settings;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding;
using SwarmCore.Coding.Native;
using SwarmCore.Coding.Routing;
using SwarmCore.Coding.Settings;

namespace SwarmCode.Hosting.Tests;

[Trait("LlmMode", "Mock")]
public sealed class AgentSettingsTests : TempDirTest
{
    private sealed class TestTurns : ICurrentTurnContextAccessor { public CurrentTurnContext? Current { get; set; } }
    private ServiceProvider Provider()
    {
        var services = new ServiceCollection(); services.AddLogging();
        services.AddSingleton<ICurrentTurnContextAccessor, TestTurns>();
        services.AddSingleton<SwarmCore.Secrets.IOsKeyStore>(new EncryptedTestKeyStore());
        services.AddSwarmCodeHost(new() { RepositoryRoot = Tmp, DataPaths = Paths });
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
    private async Task<string> UserAsync(ServiceProvider p, string? parent = null)
    {
        var lane = p.GetRequiredService<IOptions<CodingLaneOptions>>().Value.Lanes.First();
        var session = await p.GetRequiredService<WorkspaceSessionCatalog>().CreateAsync(Tmp, laneName: lane.Name, model: lane.Model + "@high", parentSessionId: parent);
        p.GetRequiredService<ICurrentTurnContextAccessor>().Current = new(session.Id, null, null, CommunicationScopeUserInitiated: true);
        return session.Id;
    }
    private static async Task<AgentSettingsDocument> Read(IAgentSettingsHost host, string id) => (await host.ReadAsync(null, default)).Single(d => d.Id == id);
    private static Task<SettingsChangeResult> Set(IAgentSettingsHost host, AgentSettingsDocument doc, string json) => host.UpdateAsync(new(doc.Id, doc.Revision, JsonNode.Parse(json)!.AsObject()), default);

    [Fact]
    public async Task Catalog_and_native_bridge_expose_settings_and_schemas_for_every_category()
    {
        await using var p = Provider();
        await p.GetServices<IHostedService>().OfType<SwarmCodeSettingsToolRegistration>().Single().StartAsync(default);
        var tools = p.GetRequiredService<ICapabilityCatalog>().All();
        var bridge = HostToolBridgeCatalog.Select(tools, HostToolProfile.Full);
        Assert.Equal("object", tools.Single(t => t.ToolId == "settings.update").InputSchema!["properties"]!["changes"]!["type"]!.GetValue<string>());
        Assert.Contains("settings_read", bridge.Keys); Assert.Contains("settings_update", bridge.Keys); Assert.Contains("settings_use_current_defaults", bridge.Keys);
        var docs = await p.GetRequiredService<IAgentSettingsHost>().ReadAsync(null, default);
        foreach (var id in new[] { "preferences", "work", "verification", "computer", "attachments", "fallback", "lanes", "permissions", "connectors", "memory", "hooks", "model-sources", "automatic-selection", "connections", "credentials" })
            Assert.Contains(docs, d => d.Id == id && d.Schema["properties"] is not null);
        Assert.Contains("settings.use_current_defaults", p.GetRequiredService<IOptions<CodingOptions>>().Value.AdditionalInstructions);
    }

    [Fact]
    public async Task Current_defaults_preserve_provider_model_and_reasoning_without_changing_existing_sessions()
    {
        await using var p = Provider(); var id = await UserAsync(p);
        var session = (await p.GetRequiredService<WorkspaceSessionCatalog>().GetAsync(id))!;
        var host = p.GetRequiredService<IAgentSettingsHost>();
        var result = await host.UseCurrentDefaultsAsync(default);
        Assert.False(result.RequiresReload);
        var prefs = new AppPreferencesStore(Paths).Current;
        Assert.Equal(session.Selection!.Route, prefs.DefaultLane); Assert.Equal(session.Selection.Model, prefs.DefaultModel);
        Assert.EndsWith("@high", prefs.DefaultModel);
        Assert.Equal(session.Selection, (await new WorkspaceSessionCatalog(Paths).GetAsync(id))!.Selection);
    }

    [Fact]
    public async Task Partial_updates_preserve_unrelated_fields_explicit_false_null_and_restart()
    {
        await using var p = Provider(); await UserAsync(p);
        var host = p.GetRequiredService<IAgentSettingsHost>();
        var other = new AppPreferencesStore(Paths);
        other.Update(x => { x.EditorCommand = "cursor"; x.ShowReasoning = true; x.SidebarAllRepositories = true; });
        await Set(host, await Read(host, "preferences"), """{"showReasoning":false,"editorCommand":null,"zoom":1.15}""");
        var fresh = new AppPreferencesStore(Paths).Current;
        Assert.Null(fresh.EditorCommand); Assert.False(fresh.ShowReasoning); Assert.True(fresh.SidebarAllRepositories); Assert.Equal(1.15, fresh.Zoom);
        other.Update(x => x.Theme = "dark");
        Assert.Equal("dark", p.GetRequiredService<AppPreferencesStore>().Current.Theme);
        await Set(host, await Read(host, "computer"), """{"enabled":false}""");
        Assert.False(new UserSettingsFile(Paths).ReadCodingSection<SwarmCore.Coding.Computer.CodingComputerOptions>("Computer").Enabled);
    }

    [Fact]
    public async Task Competing_UI_writer_stale_revision_and_delete_then_save_do_not_overwrite()
    {
        await using var p = Provider(); await UserAsync(p); var host = p.GetRequiredService<IAgentSettingsHost>();
        var read = await Read(host, "preferences");
        new AppPreferencesStore(Paths).Update(x => x.Zoom = 1.3);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Set(host, read, """{"theme":"dark"}"""));
        Assert.Equal(1.3, new AppPreferencesStore(Paths).Current.Zoom);
        read = await Read(host, "preferences"); File.Delete(Paths.PreferencesPath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Set(host, read, """{"theme":"dark"}"""));
        Assert.False(File.Exists(Paths.PreferencesPath));
        new UserSettingsFile(Paths).WriteCodingSection("Computer", new { enabled = true });
        var runtime = await Read(host, "computer");
        new UserSettingsFile(Paths).WriteAllowCodexAppConnectors(true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Set(host, runtime, """{"enabled":false}"""));
        Assert.True(new UserSettingsFile(Paths).ReadAllowCodexAppConnectors());
    }

    [Fact]
    public async Task Validation_authority_and_malformed_files_fail_without_writing()
    {
        await using var p = Provider(); var id = await UserAsync(p); var host = p.GetRequiredService<IAgentSettingsHost>();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Set(host, await Read(host,"preferences"), """{"zoom":9}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Set(host, await Read(host,"preferences"), """{"secretPassword":"x"}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Set(host, await Read(host,"computer"), """{"chromeCdpEndpoint":"https://example.com"}"""));
        await UserAsync(p, id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.UseCurrentDefaultsAsync(default));
        var turn = p.GetRequiredService<ICurrentTurnContextAccessor>(); turn.Current = turn.Current! with { UnattendedAgentTurn = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.UseCurrentDefaultsAsync(default));
        await File.WriteAllTextAsync(Paths.PreferencesPath, "not-json");
        Assert.ThrowsAny<Exception>(() => new AppPreferencesStore(Paths).Update(x => x.Theme = "light"));
        Assert.Equal("not-json", await File.ReadAllTextAsync(Paths.PreferencesPath));
    }

    [Fact]
    public async Task Model_sources_and_decision_changes_use_owning_validators_and_revisioned_stores()
    {
        await using var p = Provider(); await UserAsync(p); var host = p.GetRequiredService<IAgentSettingsHost>();
        var doc = await Read(host, "model-sources");
        await Set(host, doc, """{"preferLocalModelsOnDecline":false}""");
        Assert.False(new ModelSourcesStore(Paths).Read().PreferLocalModelsOnDecline);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Set(host, doc, """{"preferLocalModelsOnDecline":true}"""));
        doc = await Read(host, "automatic-selection");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Set(host, doc, """{"enabled":true}"""));
        await Set(host, doc, """{"connection":{"model":"another-model"}}""");
        Assert.Equal("another-model", (await new DecisionSettingsStore(Paths).ReadAsync()).Connection.Model);
    }

    [Fact]
    public async Task Credential_binding_uses_saved_secret_without_returning_values_and_refreshes_provider_routes()
    {
        await using var p = Provider(); await UserAsync(p); var host = p.GetRequiredService<IAgentSettingsHost>();
        var vault = p.GetRequiredService<SwarmCore.Secrets.ISecretVault>();
        const string value = "synthetic-private-api-key";
        await vault.SetAsync(new SwarmCore.Secrets.SecretWriteRequest { Name = "saved-key", Value = value });
        var doc = await Read(host,"credentials"); Assert.DoesNotContain(value,doc.Values.ToJsonString());
        var activation = p.GetRequiredService<SettingsActivation>(); var refresh = 0; activation.ReloadRequested += () => refresh++;
        var result = await Set(host,doc,"""{"bindings":{"llm.openai":"saved-key"}}""");
        Assert.True(result.RequiresReload); Assert.Equal(1,refresh);
        Assert.DoesNotContain(value,result.Message);
        Assert.Equal(value,await p.GetRequiredService<SwarmCodeApiKeyProvider>().GetApiKeyAsync("OpenAI"));
        doc = await Read(host,"credentials"); Assert.DoesNotContain(value,doc.Values.ToJsonString());
        await Set(host,doc,"""{"bindings":{"llm.openai":null}}""");
        Assert.False(await vault.HasAsync("llm.openai"));
    }

    [Fact]
    public async Task UI_drafts_merge_other_fields_and_reject_same_field_and_deleted_settings()
    {
        var ui = new UserSettingsFile(Paths);
        ui.WriteCodingSection("Computer", new SwarmCore.Coding.Computer.CodingComputerOptions { Headless = true });
        var draft = ui.ReadCodingSection<SwarmCore.Coding.Computer.CodingComputerOptions>("Computer");
        await using var p = Provider(); await UserAsync(p); var host = p.GetRequiredService<IAgentSettingsHost>();
        await Set(host, await Read(host,"computer"), """{"width":1000}""");
        draft.Enabled = false;
        ui.WriteCodingSection("Computer", draft);
        var saved = new UserSettingsFile(Paths).ReadCodingSection<SwarmCore.Coding.Computer.CodingComputerOptions>("Computer");
        Assert.Equal(1000, saved.Width); Assert.False(saved.Enabled); Assert.True(saved.Headless);
        draft = ui.ReadCodingSection<SwarmCore.Coding.Computer.CodingComputerOptions>("Computer");
        await Set(host, await Read(host,"computer"), """{"width":1100}""");
        draft.Width = 1200;
        Assert.Throws<InvalidOperationException>(() => ui.WriteCodingSection("Computer", draft));
        draft = ui.ReadCodingSection<SwarmCore.Coding.Computer.CodingComputerOptions>("Computer");
        File.Delete(Paths.UserConfigPath); draft.Enabled = true;
        Assert.Throws<InvalidOperationException>(() => ui.WriteCodingSection("Computer", draft));
        Assert.False(File.Exists(Paths.UserConfigPath));
    }

    [Fact]
    public async Task MCP_patch_preserves_other_servers_and_protected_values_and_checks_conflicts()
    {
        await using var p = Provider(); await UserAsync(p); var host = p.GetRequiredService<IAgentSettingsHost>();
        await File.WriteAllTextAsync(Paths.McpServersPath, """{"other":true,"mcpServers":{"one":{"command":"test","env":{"TOKEN":"synthetic-private-value"}},"two":{"command":"other"}}}""");
        var doc = await Read(host,"connections");
        Assert.DoesNotContain("synthetic-private-value", doc.Values.ToJsonString());
        await Set(host, doc, """{"servers":{"one":{"required":true}}}""");
        var saved = JsonNode.Parse(await File.ReadAllTextAsync(Paths.McpServersPath))!;
        Assert.True((bool)saved["other"]!); Assert.True((bool)saved["mcpServers"]!["one"]!["required"]!);
        Assert.Equal("synthetic-private-value", (string)saved["mcpServers"]!["one"]!["env"]!["TOKEN"]!);
        Assert.NotNull(saved["mcpServers"]!["two"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Set(host, doc, """{"servers":{"two":null}}"""));
    }
}
