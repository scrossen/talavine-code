using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SwarmCore.Agent.Abstractions;
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
        NotebookReadTool.ToolId, NotebookEditTool.ToolId,
        "models.inspect", "models.test_vision",
        ModelVisionRecovery.ToolId,
    ];

    /// <summary>For hosts that do not run hosted services. Safe to call repeatedly.</summary>
    public static void RegisterAll(IServiceProvider services)
    {
        // Resolving the options applies Coding:GitExecutable before any git tool runs (GitExecutableOptionsHook).
        _ = services.GetService<Microsoft.Extensions.Options.IOptions<CodingOptions>>()?.Value;
        MissionPrimitiveTools.RegisterAll(services);
        var catalog = services.GetRequiredService<ICapabilityCatalog>();
        catalog.Register(services.GetRequiredService<EditFileTool>().Descriptor);
        catalog.Register(services.GetRequiredService<GrepTool>().Descriptor);
        catalog.Register(services.GetRequiredService<GlobTool>().Descriptor);
        catalog.Register(services.GetRequiredService<GitStatusTool>().Descriptor);
        catalog.Register(services.GetRequiredService<GitDiffTool>().Descriptor);
        catalog.Register(services.GetRequiredService<GitCommitTool>().Descriptor);
        catalog.Register(services.GetRequiredService<GitLogTool>().Descriptor);
        catalog.Register(services.GetRequiredService<CredentialsListTool>().Descriptor);
        foreach (var descriptor in services.GetRequiredService<ModelCapabilityTools>().Descriptors) catalog.Register(descriptor);
        catalog.Register(services.GetRequiredService<HarnessDeclineTool>().Descriptor);
        // The task list belongs to the calling turn's conversation; the model never names one.
        if (services.GetService<Planning.IConversationPlanStore>() is { } plans
            && services.GetService<SwarmCore.Agent.Runtime.ICurrentTurnContextAccessor>() is { } turns)
            foreach (var descriptor in Planning.PlanTools.Create(plans, () => turns.Current?.ConversationId)) catalog.Register(descriptor);
        catalog.Register(services.GetRequiredService<NotebookReadTool>().Descriptor);
        catalog.Register(services.GetRequiredService<NotebookEditTool>().Descriptor);
        if (services.GetService<Computer.CodingComputerTools>() is { } computer)
            foreach (var descriptor in computer.Descriptors) catalog.Register(descriptor);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        RegisterAll(services);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
