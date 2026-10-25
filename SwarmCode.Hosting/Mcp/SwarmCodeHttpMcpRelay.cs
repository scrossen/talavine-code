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
        // Every message below is written by this host and names only the server and the secret's name: an upstream
        // exception is never printed, because it may include request headers or an echoed credential.
        var file = new ExternalMcpServersFile(paths);
        // Server names are matched case-insensitively everywhere else in the host (catalog, native options, reserved names).
        var server = file.Read().Servers.FirstOrDefault(s => string.Equals(s.Name, serverName, StringComparison.OrdinalIgnoreCase));
        if (server is null) return Fail($"No MCP server named \"{serverName}\" is configured in {file.Path}.");
        var launch = server.ToLaunch();
        if (launch.Url is null) return Fail($"\"{server.Name}\" is a stdio server, not an HTTP server; it cannot be relayed.");
        if (McpHttpLaunch.ValidationProblem(launch) is { } problem) return Fail($"\"{server.Name}\" has an invalid HTTP endpoint: {problem}");

        string? bearer;
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSwarmCodeSecrets(paths);
            using var provider = services.BuildServiceProvider();
            bearer = await McpHttpLaunch.ResolveBearerAsync(launch, provider.GetRequiredService<ISecretVault>(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return 0; }
        catch
        {
            var secret = launch.BearerTokenReference is { } reference ? SecretReference.Names(reference).FirstOrDefault() : null;
            return Fail(secret is null
                ? $"The credential for \"{server.Name}\" could not be read from the vault."
                : $"The bearer secret \"{secret}\" for \"{server.Name}\" is not in the vault. Save it with /secret, then retry.");
        }

        try
        {
            await using var relay = await HttpMcpRelay.ConnectAsync(new Uri(launch.Url), bearer, ct).ConfigureAwait(false);
            await using var mcp = McpServer.Create(new StdioServerTransport("swarmcode-http-relay", loggerFactory: null),
                relay.ServerOptions(serverName), loggerFactory: null, serviceProvider: null);
            await mcp.RunAsync(ct).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return 0; }
        catch
        {
            return Fail($"Could not connect to \"{server.Name}\" at its configured endpoint. Check that the server is running and reachable.");
        }

        static int Fail(string message)
        {
            Console.Error.WriteLine("MCP HTTP relay failed: " + message);
            return 1;
        }
    }
}
