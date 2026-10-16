using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding;
using SwarmCore.Coding.Prompting;
using SwarmCore.Missions;
using Xunit;

namespace SwarmCore.Coding.Tests;

public class CompositionTests
{
    [Fact]
    public void Mission_execution_publishes_primitives_to_the_catalog()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddSwarmCoreCoding(o => o.RepositoryRoot = Path.GetTempPath());
        using var sp = services.BuildServiceProvider();

        Assert.Contains(sp.GetServices<IHostedService>(), h => h is MissionPrimitiveToolRegistrationService);
        Assert.Contains(sp.GetServices<IHostedService>(), h => h is CodingToolRegistrar);

        CodingToolRegistrar.RegisterAll(sp);
        var ids = sp.GetRequiredService<ICapabilityCatalog>().All().Select(t => t.ToolId).ToHashSet();
        foreach (var id in MissionPrimitiveTools.ToolIds) Assert.Contains(id, ids);
        foreach (var id in CodingToolRegistrar.CodingToolIds) Assert.Contains(id, ids);

        Assert.Equal(Path.GetFullPath(Path.GetTempPath()), sp.GetRequiredService<IOptions<SandboxOptions>>().Value.Root);
    }

    [Fact]
    public async Task Profile_offers_catalog_tools_and_injects_repository_instructions()
    {
        var root = Path.Combine(Path.GetTempPath(), "swarmcode-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "AGENTS.md"), "# Rules\nAlways run tests.");
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddHttpClient();
            services.AddSwarmCoreCoding(o => o.RepositoryRoot = root);
            using var sp = services.BuildServiceProvider();
            CodingToolRegistrar.RegisterAll(sp);

            var resolved = await sp.GetRequiredService<IProfileResolver>().ResolveAsync("conv");
            Assert.Contains(resolved.Tools, t => t.ToolId == "code.edit_file");
            Assert.Contains(resolved.Tools, t => t.ToolId == "shell.exec");
            Assert.Contains("code.edit_file", resolved.Snapshot.SystemPrompt);

            var names = sp.GetRequiredService<IInjectorListProvider>().GetForProfile(resolved.Snapshot.Id);
            var block = await sp.GetRequiredService<IContextInjectorRegistry>().BuildCombinedAsync(names,
                new ContextInjectionRequest("conv", "hi", resolved.Snapshot.Id));
            Assert.NotNull(block);
            Assert.Contains("Always run tests.", block);
            Assert.Contains("### AGENTS.md", block);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
