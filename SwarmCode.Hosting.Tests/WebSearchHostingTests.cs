using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SwarmCode.Hosting.Mcp;
using SwarmCode.Hosting.Web;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Coding;
using SwarmCore.Coding.Web;
using SwarmCore.Secrets;

namespace SwarmCode.Hosting.Tests;

public class WebSearchHostingTests : TempDirTest
{
    [Fact]
    public async Task A_key_pasted_as_supporting_context_is_protected_and_usable_by_search_immediately()
    {
        await using var sp = Provider();
        const string key = "BSA7synthetic9Key4ForIntake2Probe8"; // gitleaks:allow -- synthetic test fixture, never a live credential
        var intake = sp.GetRequiredService<Secrets.SecretIntake>();
        var result = await intake.ScrubAsync("here is the brave key\n" + key);
        Assert.DoesNotContain(key, result.Text);
        Assert.Contains("{{secret:" + SwarmCodeWebSearch.SecretName + "}}", result.Text);
        Assert.Equal(key, await sp.GetRequiredService<IWebSearchKeySource>().GetKeyAsync(default));
        var vault = sp.GetRequiredService<ISecretVault>();
        Assert.True((await vault.GetEntryAsync(SwarmCodeWebSearch.SecretName))!.IsEncrypted);
        Assert.Equal(result.Text, (await intake.ScrubAsync(result.Text)).Text);
        const string prose = "the Brave key behavior is relevant to the original task";
        Assert.Equal(prose, (await intake.ScrubAsync(prose)).Text);
    }

    [Fact]
    public async Task Desktop_vault_composition_binds_supporting_keys_before_a_workspace_opens()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOsKeyStore>(new EncryptedTestKeyStore());
        services.AddSwarmCodeSecrets(Paths);
        await using var sp = services.BuildServiceProvider();
        const string key = "BSA6Desktop9Vault3Composition7Probe2";
        var result = await sp.GetRequiredService<Secrets.SecretIntake>().ScrubAsync("here is the brave key\n" + key);
        Assert.DoesNotContain(key, result.Text);
        Assert.Contains("{{secret:brave-search-api-key}}", result.Text);
        Assert.Equal(key, await sp.GetRequiredService<ISecretVault>().GetValueAsync(SwarmCodeWebSearch.SecretName));
    }

    [Fact]
    public async Task Credential_binding_is_metadata_driven_for_other_integrations()
    {
        await using var sp = Provider();
        var intake = new Secrets.SecretIntake(sp.GetRequiredService<ISecretVault>(), bindings:
            [new SwarmCore.Coding.Secrets.CredentialIntakeBinding("docs-access", ["Documentation lookup"])]);
        var result = await intake.ScrubAsync("Documentation lookup API key: Synthetic8Token4ForDocs2");
        Assert.Contains("{{secret:docs-access}}", result.Text);
        Assert.Equal("Synthetic8Token4ForDocs2", await sp.GetRequiredService<ISecretVault>().GetValueAsync("docs-access"));
    }
    private ServiceProvider Provider()
    {
        var repo = Path.Combine(Tmp, "repo");
        Directory.CreateDirectory(repo);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSwarmCodeHost(new SwarmCodeHostOptions
        {
            RepositoryRoot = repo,
            DataPaths = Paths,
            ConfigureServices = s => s.AddSingleton<IOsKeyStore>(new EncryptedTestKeyStore()),
        });
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Every_workspace_gets_web_search_and_fetch_with_the_vault_key_and_guidance()
    {
        await using var sp = Provider();
        foreach (var hosted in sp.GetServices<IHostedService>().OfType<WebToolRegistration>()) await hosted.StartAsync(default);
        var ids = sp.GetRequiredService<ICapabilityCatalog>().All().Select(t => t.ToolId).ToHashSet();
        Assert.Contains(WebTools.SearchToolId, ids);
        Assert.Contains(WebTools.FetchToolId, ids);

        Assert.IsType<VaultWebSearchKeySource>(sp.GetRequiredService<IWebSearchKeySource>());
        Assert.Equal(SwarmCodeWebSearch.MissingKeyGuidance, sp.GetRequiredService<IOptions<WebToolsOptions>>().Value.MissingKeyGuidance);
        var instructions = sp.GetRequiredService<IOptions<CodingOptions>>().Value.AdditionalInstructions;
        Assert.Contains("web.search", instructions);
        Assert.Contains("web.fetch", instructions);
    }

    [Fact]
    public async Task Key_saved_in_the_vault_is_read_at_call_time_and_removal_unconfigures_search()
    {
        await using var sp = Provider();
        var vault = sp.GetRequiredService<ISecretVault>();
        var keys = sp.GetRequiredService<IWebSearchKeySource>();
        Assert.False(await SwarmCodeWebSearch.IsConfiguredAsync(vault));
        Assert.Null(await keys.GetKeyAsync(default));

        var noKey = await sp.GetRequiredService<WebTools>().SearchAsync(new WebSearchInput { Query = "x" }, default);
        Assert.False(noKey.Configured);
        Assert.Contains("Settings > Web search", noKey.Message);

        await SwarmCodeWebSearch.SaveKeyAsync(vault, "  BSA-hosting-test-key  ");
        Assert.True(await SwarmCodeWebSearch.IsConfiguredAsync(vault));
        Assert.Equal("BSA-hosting-test-key", await keys.GetKeyAsync(default));
        var entry = await vault.GetEntryAsync(SwarmCodeWebSearch.SecretName);
        Assert.Equal(SecretKinds.ApiKey, entry!.Kind);

        Assert.True(await SwarmCodeWebSearch.RemoveKeyAsync(vault));
        Assert.Null(await keys.GetKeyAsync(default));
        await Assert.ThrowsAsync<ArgumentException>(() => SwarmCodeWebSearch.SaveKeyAsync(vault, "   "));
    }


}
