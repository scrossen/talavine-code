using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SwarmCode.Hosting.Apps;
using SwarmCode.Hosting.Mcp;
using SwarmCore.Agent.AppControl;
using SwarmCore.Secrets;

namespace SwarmCode.Hosting.Tests;

/// <summary>Connecting SwarmCode to the person's apps: by discovery for a running app, never leaking a stored key.</summary>
public sealed class ConnectedAppsTests : TempDirTest
{
    private ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOsKeyStore>(new EncryptedTestKeyStore());
        services.AddSwarmCodeSecrets(Paths);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task A_stored_key_is_only_sent_to_the_site_it_was_saved_for()
    {
        using var sp = Build();
        var vault = sp.GetRequiredService<ISecretVault>();
        await vault.SetAsync(new SecretWriteRequest { Name = "github-pat", Value = "ghp_not_for_you", Site = "https://github.com" });
        var apps = new ConnectedApps(Paths, vault);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            apps.ConnectAsync("https://evil.example/mcp", "evil", "github-pat", null));
        Assert.Contains("not saved for https://evil.example", refused.Message);
        Assert.DoesNotContain(new ExternalMcpServersFile(Paths).Read().Servers, s => s.Name == "evil");
    }

    [Fact]
    public async Task A_running_app_connects_by_discovery_and_its_token_stays_in_the_vault()
    {
        var dir = Path.Combine(Tmp, "app-control");
        Environment.SetEnvironmentVariable("SWARMCORE_APP_CONTROL_DIR", dir);
        try
        {
            var token = LoopbackMcpServer.NewToken();
            await using var server = new LoopbackMcpServer("demoapp", "1", token, () => [], NullLogger.Instance);
            server.Start();
            AppControlDiscovery.Publish(new("demoapp", "Demo App", server.Url, token, Environment.ProcessId, null, null, DateTimeOffset.UtcNow));
            using var sp = Build();
            var vault = sp.GetRequiredService<ISecretVault>();
            var apps = new ConnectedApps(Paths, vault);

            var result = await apps.ConnectAsync("demoapp", null, null, null);

            Assert.Contains("Demo App", result);
            var saved = Assert.Single(new ExternalMcpServersFile(Paths).Read().Servers, s => s.Name == "demoapp");
            Assert.Equal(server.Url, saved.Url);
            Assert.Equal("{{secret:app-demoapp}}", saved.BearerTokenReference);
            Assert.DoesNotContain(token, File.ReadAllText(Paths.McpServersPath));
            Assert.Equal(token, await vault.GetValueAsync("app-demoapp"));
            Assert.Contains("total", await apps.FindToolsAsync("demoapp", null, 0));
        }
        finally { Environment.SetEnvironmentVariable("SWARMCORE_APP_CONTROL_DIR", null); }
    }
}
