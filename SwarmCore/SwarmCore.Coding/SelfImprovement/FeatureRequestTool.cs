using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;

namespace SwarmCore.Coding.SelfImprovement;

public sealed record FeatureRequestInput(string Request, string Missing, string? Workaround = null);

public sealed record FeatureRequestOutput(bool Queued, string? ItemId, string Message);

/// <summary>
/// <c>harness.request_feature</c>: the model records that the person asked SwarmCode itself for something it has no
/// tool, command, setting or integration for. The request goes to the self-improvement backlog, where an idle-time cycle
/// implements and verifies it. Read-only and cheap on purpose, so plan turns and the native bridge serve it too.
/// </summary>
public static class FeatureRequestTool
{
    public const string ToolId = "harness.request_feature";
    public const string OwnerAgentId = "coding.self-improvement";
    public const string Source = "request_feature";

    public const string Guidance =
        "Self-improvement: when the person asks Talavine Code itself to do something it has no tool, command, setting or integration for " +
        "(not something about their repository), call harness.request_feature once with what they asked and exactly what is missing, " +
        "then tell them it is queued for Talavine Code's self-improvement cycle and do what you can with the tools you have. " +
        "Do not call it for a policy refusal or for work that is merely hard.";

    public static ToolDescriptor Create(SelfImprovementReporter reporter, Func<string?> conversation) =>
        ToolDescriptor.Create<FeatureRequestInput, FeatureRequestOutput>(ToolId, OwnerAgentId,
            "Record that the person asked Talavine Code (the coding assistant itself, not their repository) for a capability it does not " +
            "have: a missing tool, slash command, setting, integration or behavior. request = what they asked, in their words; " +
            "missing = exactly what Talavine Code lacks; workaround = what you did instead, if anything. The request is queued for " +
            "Talavine Code's own build-and-test improvement cycle. Never use it for a policy refusal.",
            ToolCostClass.Cheap, ToolSideEffect.ReadOnly,
            async (input, ct) =>
            {
                if (conversation() is not { Length: > 0 } conversationId)
                    return new FeatureRequestOutput(false, null, "Feature requests can be recorded only inside a conversation turn.");
                if (string.IsNullOrWhiteSpace(input.Request))
                    return new FeatureRequestOutput(false, null, "Say what the person asked for.");
                var item = await reporter.ReportFeatureRequestAsync(input.Request, input.Missing, input.Workaround, Source, conversationId, ct).ConfigureAwait(false);
                return item is null
                    ? new FeatureRequestOutput(false, null, "Not queued: self-improvement is off or its backlog is full. Tell the person what Talavine Code cannot do.")
                    : new FeatureRequestOutput(true, item.Id, item.Occurrences > 1
                        ? $"Already queued (seen {item.Occurrences} times, {item.Stage}). Tell the person it is on Talavine Code's improvement list."
                        : "Queued for Talavine Code's self-improvement cycle. Tell the person it will be built and tested while Talavine Code is idle.");
            },
            keywords: ["feature", "request", "missing", "capability", "cannot", "unsupported", "harness", "self-improvement"]);
}

/// <summary>Publishes <c>harness.request_feature</c> for engine lanes when the host registered a <see cref="SelfImprovementReporter"/>.</summary>
public sealed class FeatureRequestToolRegistration(IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (services.GetService<SelfImprovementReporter>() is not { } reporter) return Task.CompletedTask;
        var turns = services.GetRequiredService<ICurrentTurnContextAccessor>();
        services.GetRequiredService<ICapabilityCatalog>().Register(FeatureRequestTool.Create(reporter, () => turns.Current?.ConversationId));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class SelfImprovementServiceCollectionExtensions
{
    /// <summary>
    /// Opt-in self-improvement reporting for a coding host: options (<c>Coding:SelfImprovement</c>), the
    /// <see cref="SelfImprovementReporter"/> over whichever <see cref="ISelfImprovementBacklog"/> the host registered
    /// (in-memory when none), the <c>harness.request_feature</c> tool (native lanes reach it through the host tool
    /// bridge) and its guidance. The worker and idle gate are host-level (a host keeps one above its workspaces), so
    /// this registers neither. Deliberately not part of <see cref="CodingToolRegistrar.CodingToolIds"/>.
    /// </summary>
    public static IServiceCollection AddSwarmCoreSelfImprovement(this IServiceCollection services, Action<SelfImprovementOptions>? configure = null)
    {
        services.AddOptions<SelfImprovementOptions>();
        if (configure is not null) services.Configure(configure);
        services.TryAddSingleton<ISelfImprovementBacklog, InMemorySelfImprovementBacklog>();
        services.TryAddSingleton(sp => new SelfImprovementReporter(sp.GetRequiredService<ISelfImprovementBacklog>(),
            () => sp.GetRequiredService<IOptions<SelfImprovementOptions>>().Value,
            logger: sp.GetService<Microsoft.Extensions.Logging.ILogger<SelfImprovementReporter>>()));
        if (!services.Any(d => d.ImplementationType == typeof(FeatureRequestToolRegistration)))
            services.AddHostedService<FeatureRequestToolRegistration>();
        services.PostConfigure<CodingOptions>(coding =>
        {
            if (coding.AdditionalInstructions?.Contains(FeatureRequestTool.ToolId, StringComparison.Ordinal) == true) return;
            coding.AdditionalInstructions = string.IsNullOrWhiteSpace(coding.AdditionalInstructions)
                ? FeatureRequestTool.Guidance
                : coding.AdditionalInstructions.Trim() + Environment.NewLine + FeatureRequestTool.Guidance;
        });
        return services;
    }
}
