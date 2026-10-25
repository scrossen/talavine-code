using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Catalog;
using SwarmCore.Coding.Tools;
using SwarmCore.Missions;

namespace SwarmCore.Coding;

/// <summary>Publishes the coding tools (and the shared primitives) to the capability catalog.</summary>
public sealed class CodingToolRegistrar(IServiceProvider services) : IHostedService
{
    public static IReadOnlyList<string> CodingToolIds { get; } =
    [
        EditFileTool.ToolId, GrepTool.ToolId, GlobTool.ToolId,
        GitStatusTool.ToolId, GitDiffTool.ToolId, GitCommitTool.ToolId, GitLogTool.ToolId,
        CredentialsListTool.ToolId, HarnessDeclineTool.ToolId,
        NotebookReadTool.ToolId, NotebookEditTool.ToolId, ReadJsonTool.ToolId,
        "models.inspect", "models.test_vision",
        ModelVisionRecovery.ToolId,
    ];

    /// <summary>
    /// The descriptors of the harness tools named by <see cref="CodingToolIds"/>. Hosts that
    /// publish the harness themselves use this list, so a new harness tool reaches them too.
    /// </summary>
    public static IEnumerable<ToolDescriptor> HarnessToolDescriptors(IServiceProvider services)
    {
        yield return services.GetRequiredService<EditFileTool>().Descriptor;
        yield return services.GetRequiredService<GrepTool>().Descriptor;
        yield return services.GetRequiredService<GlobTool>().Descriptor;
        yield return services.GetRequiredService<GitStatusTool>().Descriptor;
        yield return services.GetRequiredService<GitDiffTool>().Descriptor;
        yield return services.GetRequiredService<GitCommitTool>().Descriptor;
        yield return services.GetRequiredService<GitLogTool>().Descriptor;
        yield return services.GetRequiredService<CredentialsListTool>().Descriptor;
        yield return services.GetRequiredService<HarnessDeclineTool>().Descriptor;
        yield return services.GetRequiredService<NotebookReadTool>().Descriptor;
        yield return services.GetRequiredService<NotebookEditTool>().Descriptor;
    }

    /// <summary>For hosts that do not run hosted services. Safe to call repeatedly.</summary>
    public static void RegisterAll(IServiceProvider services)
    {
        // Resolving the options applies Coding:GitExecutable before any git tool runs (GitExecutableOptionsHook).
        _ = services.GetService<Microsoft.Extensions.Options.IOptions<CodingOptions>>()?.Value;
        MissionPrimitiveTools.RegisterAll(services);
        var catalog = services.GetRequiredService<ICapabilityCatalog>();
        foreach (var descriptor in HarnessToolDescriptors(services)) catalog.Register(descriptor);
        foreach (var descriptor in services.GetRequiredService<ModelCapabilityTools>().Descriptors) catalog.Register(descriptor);
        // The task list belongs to the calling turn's conversation; the model never names one.
        if (services.GetService<Planning.IConversationPlanStore>() is { } plans
            && services.GetService<SwarmCore.Agent.Runtime.ICurrentTurnContextAccessor>() is { } turns)
            foreach (var descriptor in Planning.PlanTools.Create(plans, () => turns.Current?.ConversationId)) catalog.Register(descriptor);
        catalog.Register(services.GetRequiredService<NotebookReadTool>().Descriptor);
        catalog.Register(services.GetRequiredService<NotebookEditTool>().Descriptor);
        catalog.Register(services.GetRequiredService<ReadJsonTool>().Descriptor);
        if (services.GetService<Computer.CodingComputerTools>() is { } computer)
            foreach (var descriptor in computer.Descriptors) catalog.Register(descriptor);
        // Element-level desktop control (desktop.*) only while computer access is on. Reads are read-only; act, input
        // and command declare an external side effect, so the turn's permission policy and broker decide them.
        if (services.GetService<Microsoft.Extensions.Options.IOptions<Computer.CodingComputerOptions>>()?.Value.Enabled == true
            && services.GetService<SwarmCore.RemoteControl.Agent.DesktopControlTools>() is { } desktop)
            foreach (var descriptor in SwarmCore.RemoteControl.Platform.DesktopToolChoice.ForHost(desktop, "coding")) catalog.Register(descriptor);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        RegisterAll(services);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
