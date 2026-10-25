using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SwarmCore.Coding.SelfImprovement;
using SwarmCore.Coding.Tools;

namespace SwarmCode.Hosting.Improvement;

/// <summary>
/// SwarmCode's self-improvement wiring for a workspace: the durable backlog under the data directory (a host may
/// pre-register one shared across workspaces), the reporter and <c>harness.request_feature</c> tool from the harness,
/// and the per-conversation workspace overrides the cycle host uses. The worker, the idle gate and the cycle host are
/// app level (see <c>AppState</c>): one loop above every workspace, like schedules.
/// </summary>
public static class SwarmCodeSelfImprovement
{
    public static IServiceCollection AddSwarmCodeSelfImprovement(this IServiceCollection services, SwarmCodeDataPaths paths, IConfiguration configuration)
    {
        services.TryAddSingleton<SessionWorkspaceOverrides>();
        services.TryAddSingleton<ISelfImprovementBacklog>(_ => new JsonFileSelfImprovementBacklog(paths.SelfImprovementDirectory));
        services.AddSwarmCoreSelfImprovement(o => configuration.GetSection(SelfImprovementOptions.SectionName).Bind(o));
        services.TryAddSingleton<ISelfImprovementInstalledFixVerifier>(_ => new RelaunchInstalledFixVerifier(paths));
        services.TryAddSingleton(provider => new GoalRepairActivationFeedback(paths, provider.GetRequiredService<ISelfImprovementBacklog>(),
            provider.GetRequiredService<SwarmCore.Coding.Goals.ICodingGoalStore>(),
            () => provider.GetRequiredService<SelfImprovementReporter>().Options,
            () => provider.GetRequiredService<AppPreferencesStore>().Current.SelfImprovementPaused));
        services.TryAddSingleton(provider => new GoalSelfImprovementCoordinator(provider.GetRequiredService<SelfImprovementReporter>(),
            provider.GetRequiredService<SwarmCore.Coding.Goals.ICodingGoalStore>(), provider.GetRequiredService<ISelfImprovementInstalledFixVerifier>(),
            provider.GetService<ILogger<GoalSelfImprovementCoordinator>>(),
            () => provider.GetRequiredService<AppPreferencesStore>().Current.SelfImprovementPaused,
            provider.GetRequiredService<GoalRepairActivationFeedback>()));
        return services;
    }

    /// <summary>The self-improvement options as the host configuration has them right now.</summary>
    public static SelfImprovementOptions BindOptions(IConfiguration configuration)
    {
        var options = new SelfImprovementOptions();
        configuration.GetSection(SelfImprovementOptions.SectionName).Bind(options);
        return options;
    }
}

/// <summary>
/// Finds the SwarmCode source checkout: the configured root when it is one, otherwise the checkout this executable was
/// built from (a developer build runs out of <c>SwarmCode/bin/...</c> or <c>.build/...</c> inside the repository).
/// An installed app has no source nearby and gets null, which leaves the loop inert.
/// </summary>
public static class SwarmCodeSourceRepository
{
    /// <summary>A folder is the SwarmCode repository when it holds the hosting project and the harness project.</summary>
    public static bool IsSwarmCodeRepository(string? folder)
        => !string.IsNullOrWhiteSpace(folder)
           && File.Exists(Path.Combine(folder, "SwarmCode.Hosting", "SwarmCode.Hosting.csproj"))
           && File.Exists(Path.Combine(folder, "SwarmCore", "SwarmCore.Coding", "SwarmCore.Coding.csproj"));

    public static string? Resolve(string? configuredRoot, string? baseDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredRoot))
            return Directory.Exists(configuredRoot) ? Path.GetFullPath(configuredRoot) : null;
        return Detect(baseDirectory ?? AppContext.BaseDirectory);
    }

    /// <summary>Walks up from a folder looking for the repository; null when none encloses it.</summary>
    public static string? Detect(string? start)
    {
        if (string.IsNullOrWhiteSpace(start)) return null;
        string? current;
        try { current = Path.GetFullPath(start); } catch (Exception) { return null; }
        for (var depth = 0; current is not null && depth < 12; depth++)
        {
            if (IsSwarmCodeRepository(current)) return current;
            current = Path.GetDirectoryName(current);
        }
        return null;
    }
}

public enum TurnDiagnosticKind { Exception, ErrorText, ToolError, Decline }

/// <summary>Something a turn reported that may be a defect or a capability gap. Collected by the runner, classified afterwards.</summary>
public sealed record TurnDiagnostic(TurnDiagnosticKind Kind, string Message, string? Details = null, Exception? Exception = null, string? ToolName = null);

/// <summary>
/// Turns what a finished turn reported into backlog sightings: product exceptions become defects (environmental
/// failures do not), failed tool results that name a product exception become defects, and a decline whose reason is a
/// missing capability becomes a feature request. Turns a self-improvement cycle runs are never reported, so a failing
/// repair cannot feed itself. Everything runs in the background and never throws into the runner.
/// </summary>
public static class SelfImprovementCapture
{
    public const string TurnErrorSource = "turn-error";
    public const string ToolErrorSource = "tool-error";
    public const string DeclineSource = "decline";
    public const string UnknownCommandSource = "unknown-command";
    public const string UnhandledExceptionSource = "unhandled-exception";

    /// <summary>Record a recovery defect while the turn is still running; no commands, outputs or credentials are copied.</summary>
    public static async Task ToolRecoveryAsync(IServiceProvider services, string conversationId, string toolName, TurnOrigin? origin)
    {
        if (origin?.IsSelfImprovement == true || services.GetService<SelfImprovementReporter>() is not { } reporter) return;
        await reporter.ReportAsync(new SelfImprovementReport(SelfImprovementKind.Defect,
            $"Tool recovery exhausted: {toolName}",
            $"The coding harness observed repeated failures from {toolName} and blocked further retries. " +
            "Inspect the tool schema, validation feedback and recovery/handoff contract. Reproduce with synthetic inputs before changing code. " +
            "This reports a failure to recover, not proof that the requested external operation is defective. The originating conversation retains the tool receipts.",
            SelfImprovementFingerprint.Compute(SelfImprovementKind.Defect, "tool-recovery", toolName),
            ToolErrorSource, conversationId)).ConfigureAwait(false);
    }

    public static void AfterTurn(IServiceProvider services, string conversationId, IReadOnlyList<TurnDiagnostic> diagnostics, TurnOrigin? origin, string? userMessage)
    {
        if (origin?.IsSelfImprovement == true) return;
        if (services.GetService<SelfImprovementReporter>() is not { } reporter) return;
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger("SwarmCode.SelfImprovement");
        _ = Task.Run(async () =>
        {
            try
            {
                if (services.GetService<GoalSelfImprovementCoordinator>() is { } goals
                    && services.GetService<SwarmCore.Coding.Goals.ICodingGoalStore>() is { } store
                    && await store.GetLatestForConversationAsync(SwarmCore.Coding.Goals.CodingGoalOrchestrator.Partition,
                        conversationId).ConfigureAwait(false) is { } goal)
                    await goals.ObserveAsync(goal).ConfigureAwait(false);
            }
            catch (Exception ex) { logger?.LogDebug(ex, "Could not link goal recovery for {Conversation}.", conversationId); }
            foreach (var diagnostic in diagnostics)
            {
                try
                {
                    switch (diagnostic.Kind)
                    {
                        case TurnDiagnosticKind.Exception when diagnostic.Exception is { } exception:
                            await reporter.ReportDefectAsync(exception, TurnErrorSource, conversationId, Context(userMessage)).ConfigureAwait(false);
                            break;
                        case TurnDiagnosticKind.ErrorText:
                            await reporter.ReportErrorTextAsync(diagnostic.Message, diagnostic.Details, TurnErrorSource, conversationId, Context(userMessage)).ConfigureAwait(false);
                            break;
                        case TurnDiagnosticKind.ToolError:
                            await reporter.ReportErrorTextAsync(diagnostic.Message, diagnostic.Details, ToolErrorSource, conversationId,
                                "Tool " + diagnostic.ToolName + " failed during a turn." + (userMessage is null ? "" : " " + Context(userMessage))).ConfigureAwait(false);
                            break;
                        case TurnDiagnosticKind.Decline when CapabilityGapHeuristics.IsCapabilityGap(diagnostic.Message) && !string.IsNullOrWhiteSpace(userMessage):
                            await reporter.ReportFeatureRequestAsync(userMessage, diagnostic.Message, null, DeclineSource, conversationId).ConfigureAwait(false);
                            break;
                    }
                }
                catch (Exception ex) { logger?.LogDebug(ex, "Could not report a turn diagnostic for {Conversation}.", conversationId); }
            }
        });
    }

    /// <summary>The reason a <c>harness.decline</c> call gave, from its input, or null.</summary>
    public static string? DeclineReason(string toolName, JsonObject? input)
        => toolName == HarnessDeclineTool.ToolId ? HarnessDeclineTool.FromInput(input)?.Reason : null;

    /// <summary>A slash command nobody defined: a feature request when it is not a typo of one that exists.</summary>
    public static void UnknownCommand(SelfImprovementReporter? reporter, string typed, IEnumerable<string> knownCommands, string? conversationId)
    {
        if (reporter is null) return;
        var name = typed.Trim().Split(' ', '\t', '\n')[0];
        if (!CapabilityGapHeuristics.IsUnknownCommandGap(name, knownCommands)) return;
        _ = reporter.ReportFeatureRequestAsync(typed.Trim(), $"There is no {name} command in Talavine Code.", null, UnknownCommandSource, conversationId);
    }

    /// <summary>An exception nothing caught (app domain, unobserved task).</summary>
    public static void Unhandled(SelfImprovementReporter? reporter, Exception? exception, string context)
    {
        if (reporter is null || exception is null) return;
        _ = reporter.ReportDefectAsync(exception, UnhandledExceptionSource, null, context);
    }

    private static string? Context(string? userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage)) return null;
        var line = userMessage.Trim();
        var newline = line.IndexOfAny(['\r', '\n']);
        if (newline >= 0) line = line[..newline];
        return "While handling the request: " + (line.Length <= 300 ? line : line[..297] + "...");
    }
}
