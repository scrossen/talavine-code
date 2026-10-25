using Microsoft.Extensions.DependencyInjection;
using SwarmCode.Hosting.Apps;
using SwarmCode.Hosting.Mcp;
using SwarmCore.Secrets;
using SwarmCore.Agent.Catalog;

namespace SwarmCode.Hosting.Tests;

public sealed class DeliverableHandbackTests : TempDirTest
{
    [Fact]
    public void Operation_id_is_deterministic_and_normalized()
    {
        var a = DeliverableHandback.OperationId("Landing page", "talavine", "task");
        Assert.Equal(a, DeliverableHandback.OperationId("  landing PAGE ", "Talavine", "TASK"));
        Assert.NotEqual(Guid.Empty, a);
    }

    [Fact]
    public void Distinct_effects_apps_and_deliverables_get_distinct_ids()
    {
        var ids = new[]
        {
            DeliverableHandback.OperationId("d1", "talavine", "task"),
            DeliverableHandback.OperationId("d1", "talavine", "note"),
            DeliverableHandback.OperationId("d1", "crm", "task"),
            DeliverableHandback.OperationId("d2", "talavine", "task"),
        };
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.Throws<ArgumentException>(() => DeliverableHandback.OperationId(" ", "a", "b"));
    }

    [Fact]
    public async Task Operation_id_tool_is_registered_read_only_and_returns_the_helper_value()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOsKeyStore>(new EncryptedTestKeyStore());
        services.AddSwarmCodeSecrets(Paths);
        using var sp = services.BuildServiceProvider();
        var tools = ConnectedAppsTools.Create(new ConnectedApps(Paths, sp.GetRequiredService<ISecretVault>()));

        var tool = Assert.Single(tools, t => t.ToolId == "apps.operation_id");
        Assert.Equal(ToolSideEffect.ReadOnly, tool.SideEffect);
        Assert.Equal(ToolCostClass.Cheap, tool.Cost);
        var id = (string)(await tool.Invoke(new AppsOperationIdInput("report", "talavine", "task"), default))!;
        Assert.Equal(DeliverableHandback.OperationId("report", "talavine", "task").ToString(), id);
    }

    [Fact]
    public void Guidance_names_operation_ids_and_forbids_sends()
    {
        Assert.Contains("operation_id", ConnectedAppsTools.Guidance);
        Assert.Contains("apps.operation_id", ConnectedAppsTools.Guidance);
        Assert.Contains("Never call a tool that prepares or performs a send", ConnectedAppsTools.Guidance);
        Assert.Contains("same operation id", ConnectedAppsTools.Guidance);
        Assert.Contains("schedules.create", ConnectedAppsTools.Guidance);
        Assert.Contains("user.ask", ConnectedAppsTools.Guidance);
    }

    [Fact]
    public void Guidance_hands_back_only_when_asked_and_uses_exact_crm_tool_ids()
    {
        var g = ConnectedAppsTools.Guidance;
        Assert.Contains("only when they asked for it", g);
        Assert.Contains("never unprompted", g);
        Assert.Contains("apps.list shows connected", g);
        Assert.Contains("only when the person named a client or case", g);
        Assert.Contains("apps.find_tools", g);
        Assert.Contains("assistant.preview_crm_mutation", g);
        Assert.Contains("assistant.apply_crm_mutation", g);
        Assert.DoesNotContain("When you finish a deliverable", g);
    }
}
