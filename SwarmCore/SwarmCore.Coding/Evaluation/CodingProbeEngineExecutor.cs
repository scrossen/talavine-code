using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;
using SwarmCore.Agent.ToolUseEngine;
using SwarmCore.Coding.Tools;
using SwarmCore.Missions;

namespace SwarmCore.Coding.Evaluation;

/// <summary>A transient synthetic session with no private context, credentials, native lanes,
/// external tools, durable conversation, or preference writer. The real host router still admits every call.</summary>
public sealed class CodingProbeEngineExecutor(ILlmRouter router) : ICodingProbeExecutor
{
    public async Task<IReadOnlyList<TurnEvent>> ExecuteAsync(CodingProbeExecution request, CancellationToken ct)
    {
        var sandbox = new SandboxPathResolver(Options.Create(new SandboxOptions
        { Root = request.RepositoryRoot, AllowAbsolutePathsOutsideTurnWorkspace = false }));
        using var services = new ServiceCollection().BuildServiceProvider();
        var shell = new ShellExecTool(sandbox, services).Descriptor;
        var check = shell with
        {
            Description = "Run the synthetic comparison check. Use structured executable git with argumentList [diff, --no-index, --, expected.txt, value.txt]. No other command is admitted.",
            Invoke = async (input, token) =>
            {
                if (input is not ShellExecTool.Input args || args.Command is not null || args.Environment?.Count > 0
                    || args.WorkingDirectory is not null && args.WorkingDirectory != "."
                    || args.FileName != "git" || args.ArgumentList?.SequenceEqual(["diff", "--no-index", "--", "expected.txt", "value.txt"]) != true)
                    throw new ToolCallNotDispatchedException("The probe admits only its fixed synthetic check.");
                // Host-resolved executable supports Macs whose PATH git is Intel-only.
                args.FileName = SwarmCore.Agent.Git.GitExecutable.Resolve();
                args.TimeoutSeconds = 20;
                var result = (ShellExecTool.Output)(await shell.Invoke(args, token).ConfigureAwait(false))!;
                // The ordinary shell's mission-only receipt flag is deliberately conservative.
                // This fixed synthetic adapter knows the physical process has exited when its
                // invocation returned a real exit code; no model text supplies that observation.
                return new ShellExecTool.Output { ExitCode = result.ExitCode, Stdout = result.Stdout,
                    Stderr = result.Stderr, TimedOut = result.TimedOut,
                    LocalProcessCompleted = !result.TimedOut && result.ExitCode >= 0 };
            },
        };
        ToolDescriptor[] tools = [new ReadFileTool(sandbox).Descriptor, new WriteFileTool(sandbox).Descriptor,
            new EditFileTool(sandbox).Descriptor, new GitStatusTool(sandbox).Descriptor, new GitDiffTool(sandbox).Descriptor,
            new GitCommitTool(sandbox).Descriptor, check];
        var scope = AgentCommunicationScope.UserRequested("model-scout-coding-probe");
        var profile = new AgentProfileSnapshot
        {
            Id = Guid.NewGuid(), Name = "Coding probe", SystemPrompt = "Complete the synthetic coding request using the provided tools. Work only in the supplied repository. Never claim completion without performing the requested work.",
            EnabledToolIds = tools.Select(t => t.ToolId).ToArray(), Provider = request.Route.Provider, Model = request.Route.Model,
            RouteProvenance = LlmOverrideProvenance.User, RoutingFunctionType = FunctionTypes.CodingAssistant,
            MaxIterations = 8, HardTimeout = TimeSpan.FromMinutes(2), MaxCumulativeTokens = 40_000,
        };
        var session = new AgentSession
        {
            ConversationId = request.ConversationId, TurnId = Guid.NewGuid().ToString("N"), Profile = profile, Tools = tools,
            Transcript = [], UserRequestAuthorized = true, DisableRouteFallback = true,
            TurnContext = new(request.ConversationId, profile.Id, Guid.NewGuid(), CommunicationScopeKind: scope.Kind,
                CommunicationScopeAuthority: scope.Authority, CommunicationScopeUserInitiated: scope.UserInitiated)
            { WorkspaceRoot = request.RepositoryRoot },
        };
        var context = new AsyncLocalCurrentTurnContextAccessor();
        var engine = new ToolUseAgentEngine(router, NullLogger<ToolUseAgentEngine>.Instance, turnContext: context);
        var mapper = new TurnEventMapper();
        var mapping = new TurnMappingContext(session.TurnId, profile.Id, profile.Name, DateTime.UtcNow);
        var events = new List<TurnEvent>();
        var prior = context.Current;
        try
        {
            await foreach (var evt in engine.RunAsync(session, request.Case.Request, ct).ConfigureAwait(false))
                events.AddRange(mapper.Map(evt, mapping));
            return events;
        }
        finally { context.Current = prior; }
    }
}
