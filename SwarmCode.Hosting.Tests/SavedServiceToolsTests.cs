using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SwarmCode.Hosting.Services;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Catalog;
using SwarmCore.Coding;
using SwarmCore.Services;
using SwarmCore.Services.Abstractions;
using SwarmCore.Services.AgentTools;
using SwarmCore.Services.Sqlite;

namespace SwarmCode.Hosting.Tests;

public sealed class SavedServiceToolsTests : TempDirTest
{
    private async Task<ServiceProvider> Host()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddSingleton<ICredentialStore, Credentials>();
        services.AddSwarmCoreCoding(o => o.RepositoryRoot = Tmp);
        services.AddSwarmCodeServiceCatalog(Paths);
        // No LLM is used by saved C# replay. Remove that unrelated adapter in this model-free fixture.
        var llm = services.Single(d => d.ImplementationType == typeof(SwarmCore.Services.Adapters.LlmServiceAdapter));
        services.Remove(llm);
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        // Start only catalog publication; no providers, network discovery or external models in this contract test.
        foreach (var hosted in provider.GetServices<IHostedService>())
            if (hosted.GetType().FullName!.Contains("SwarmCodeServiceCatalog")) await hosted.StartAsync(default);
        return provider;
    }
    private static Task<object?> Invoke(ServiceProvider host, string id, object input)
        => host.GetRequiredService<ICapabilityCatalog>().All().Single(t => t.ToolId == id).Invoke(input, default);

    [Fact]
    public async Task Authored_tool_survives_restart_replays_new_arguments_and_supports_tested_revision_and_rollback()
    {
        await using (var host = await Host())
        {
            var added = Assert.IsType<ServiceCatalogTools.AddOutput>(await Invoke(host, ServiceCatalogToolIds.Add,
                new ServiceCatalogTools.AddInput { Name = "multiply", Kind = "csharp", Description = "Multiply input by two", WhenToUse = "doubling",
                    Config = [new() { Field = "source", Value = "return (int.Parse((string)Input[\"value\"]) * 2).ToString();" }] }));
            Assert.True(added.Ok, added.Error);
            var unverified = Assert.IsType<ServiceCatalogTools.CallOutput>(await Invoke(host, ServiceCatalogToolIds.Call,
                new ServiceCatalogTools.CallInput { Name = "multiply", Args = [new() { Field = "value", Value = "7" }] }));
            Assert.False(unverified.Ok);
            Assert.False(((IToolExecutionEvidence)unverified).Succeeded);
            Assert.Contains("test_service", unverified.Error);
            var tested = Assert.IsType<ServiceRevisionTools.Result>(await Invoke(host, "catalog.test_service",
                new ServiceRevisionTools.TestInput { Name = "multiply", Version = 1, Args = [new() { Field = "value", Value = "7" }], ExpectedContains = "14" }));
            Assert.True(tested.Ok, tested.Error);
            Assert.True(((IToolExecutionEvidence)tested).Succeeded);
        }
        await using var restarted = await Host();
        var bridged = SwarmCore.Coding.Native.HostToolBridgeCatalog.Select(
            restarted.GetRequiredService<ICapabilityCatalog>().All(), SwarmCore.Coding.Native.HostToolProfile.Full);
        Assert.Contains("catalog_add_service", bridged.Keys);
        Assert.Contains("catalog_call_service", bridged.Keys);
        Assert.Contains("catalog_test_service", bridged.Keys);
        var search = Assert.IsType<ServiceCatalogTools.SearchOutput>(await Invoke(restarted, ServiceCatalogToolIds.Search,
            new ServiceCatalogTools.SearchInput { Query = "doubling" }));
        Assert.Contains(search.Services, s => s.Name == "multiply");
        async Task<string?> Call(string value)
        {
            var result = Assert.IsType<ServiceCatalogTools.CallOutput>(await Invoke(restarted, ServiceCatalogToolIds.Call,
                new ServiceCatalogTools.CallInput { Name = "multiply", Args = [new() { Field = "value", Value = value }] }));
            Assert.True(result.Ok, result.Error);
            return result.Result;
        }
        Assert.Contains("18", await Call("9"));
        Assert.Equal(ToolSideEffect.Exec, restarted.GetRequiredService<ICapabilityCatalog>().All().Single(t => t.ToolId == ServiceCatalogToolIds.Call).SideEffect);
        var revised = Assert.IsType<ServiceRevisionTools.Result>(await Invoke(restarted, "catalog.revise_service", new ServiceRevisionTools.ReviseInput
            { Name = "multiply", Source = "return (int.Parse((string)Input[\"value\"]) * 3).ToString();", Reason = "Triple instead" }));
        Assert.True(revised.Ok, revised.Error);
        var version = new ServiceRevisionTools.VersionInput { Name = "multiply", Version = revised.Version!.Value };
        Assert.False(Assert.IsType<ServiceRevisionTools.Result>(await Invoke(restarted, "catalog.activate_service", version)).Ok);
        Assert.Contains("18", await Call("9"));
        Assert.True(Assert.IsType<ServiceRevisionTools.Result>(await Invoke(restarted, "catalog.test_service",
            new ServiceRevisionTools.TestInput { Name = "multiply", Version = version.Version, Args = [new() { Field = "value", Value = "9" }], ExpectedContains = "27" })).Ok);
        Assert.True(Assert.IsType<ServiceRevisionTools.Result>(await Invoke(restarted, "catalog.activate_service", version)).Ok);
        Assert.Contains("27", await Call("9"));
        Assert.True(Assert.IsType<ServiceRevisionTools.Result>(await Invoke(restarted, "catalog.rollback_service",
            new ServiceRevisionTools.VersionInput { Name = "multiply", Version = 1 })).Ok);
        Assert.Contains("18", await Call("9"));
    }

    [Fact]
    public async Task Competing_store_writes_and_delete_then_save_cannot_replace_or_resurrect_a_tool()
    {
        var options = new SqliteServiceStoreOptions { DatabasePath = Path.Combine(Tmp, "concurrency.db"), RequireReadBaseline = true };
        using var first = new SqliteServiceStore(options);
        using var second = new SqliteServiceStore(options);
        var created = new ServiceDefinition { Name = "saved", Description = "original", CredentialRef = "test-reference" };
        await first.SaveAsync(created);
        var stale = (await second.GetAsync(created.Id))!;
        created.Description = "newer";
        await first.SaveAsync(created);
        stale.Enabled = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.SaveAsync(stale));
        var current = (await second.GetAsync(created.Id))!;
        Assert.Equal("newer", current.Description);
        current.Enabled = false;
        current.CredentialRef = null;
        await second.SaveAsync(current);
        Assert.False((await first.GetAsync(created.Id))!.Enabled);
        Assert.Null((await first.GetAsync(created.Id))!.CredentialRef);
        await first.DeleteAsync(created.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.SaveAsync(current));
        Assert.Null(await first.GetAsync(created.Id));
    }

    private sealed class Credentials : ICredentialStore
    {
        public Task SetAsync(string name, string secret, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetAsync(string name, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<bool> HasAsync(string name, CancellationToken ct = default) => Task.FromResult(false);
        public Task DeleteAsync(string name, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListNamesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
    }
}
