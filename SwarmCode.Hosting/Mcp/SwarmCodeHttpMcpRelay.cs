using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using SwarmCore.Coding;
using SwarmCore.Mcp.Core;
using SwarmCore.Secrets;

namespace SwarmCode.Hosting.Mcp;

/// <summary>Console adapter: resolves only an existing host configuration and holds its bearer inside the relay.</summary>
public static class SwarmCodeHttpMcpRelay
{
    public static async Task<int> RunAsync(SwarmCodeDataPaths paths, string serverName, CancellationToken ct = default)
    {
        try
        {
            var launch = new ExternalMcpServersFile(paths).Read().Servers.SingleOrDefault(s => s.Name == serverName)?.ToLaunch();
            if (launch?.Url is null || McpHttpLaunch.ValidationProblem(launch) is not null)
                throw new InvalidOperationException();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSwarmCodeSecrets(paths);
            using var provider = services.BuildServiceProvider();
            var bearer = await McpHttpLaunch.ResolveBearerAsync(launch, provider.GetRequiredService<ISecretVault>(), ct).ConfigureAwait(false);
            await using var relay = await HttpMcpRelay.ConnectAsync(new Uri(launch.Url), bearer, ct).ConfigureAwait(false);
            await using var server = McpServer.Create(new StdioServerTransport("swarmcode-http-relay", loggerFactory: null),
                relay.ServerOptions(serverName), loggerFactory: null, serviceProvider: null);
            await server.RunAsync(ct).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return 0; }
        catch
        {
            // Never print an upstream exception: it may include request headers or an echoed credential.
            Console.Error.WriteLine("MCP HTTP relay failed: configured endpoint or credential is unavailable.");
            return 1;
        }
    }
}
