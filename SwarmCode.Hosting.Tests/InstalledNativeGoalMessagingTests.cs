using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SwarmCode.Hosting.Goals;
using SwarmCore.Agent.Communication;
using SwarmCore.Agent.Continual;
using SwarmCore.Agent.Runtime;
using SwarmCore.Agent.ToolUseEngine;
using SwarmCore.Coding;
using SwarmCore.Coding.Goals;
using SwarmCore.Coding.Native;
using SwarmCore.Coding.Routing;

namespace SwarmCode.Hosting.Tests;

public sealed class InstalledNativeGoalMessagingTests : TempDirTest
{
    [InstalledGoalMessagingFact]
    [Trait("Category", "InstalledNativeGoal")]
    public async Task Actual_native_worker_receives_inflight_message_replies_through_host_bridge_and_preserves_leased_goal()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var repo = Path.Combine(Tmp, "repo"); Directory.CreateDirectory(repo);
        var baseDir = AppContext.BaseDirectory;
        var testAssembly = Path.GetFileNameWithoutExtension(typeof(InstalledNativeGoalMessagingTests).Assembly.Location);
        var helper = typeof(CliOptions).Assembly.Location;
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
        var arguments = new[] { "exec", "--depsfile", Path.Combine(baseDir, testAssembly + ".deps.json"),
            "--runtimeconfig", Path.Combine(baseDir, testAssembly + ".runtimeconfig.json"), helper, "--mcp-server" };
        var settings = new Dictionary<string, string?>
        {
            ["CodingLanes:DefaultLane"] = "native-goal-proof",
            ["CodingLanes:HandOffOnRefusal"] = "false",
            ["CodingLanes:Lanes:0:Name"] = "native-goal-proof",
            ["CodingLanes:Lanes:0:Provider"] = "CodexNative",
            ["CodingLanes:Lanes:0:Model"] = "codex-cli:default",
            ["Coding:Agents:Enabled"] = "false",
            ["Coding:EnabledToolIds:0"] = "goal_messages.reply",
            ["Coding:NativeAgents:McpServer:Command"] = dotnet,
            ["Coding:NativeAgents:McpServer:Environment:SWARMCODE_HOME"] = Paths.RootDirectory,
            ["Coding:NativeAgents:AllowCodexAppConnectors"] = "false",
        };
        for (var index = 0; index < arguments.Length; index++) settings["Coding:NativeAgents:McpServer:Arguments:" + index] = arguments[index];
        await using var workspace = await WorkspaceHost.CreateAsync(new SwarmCodeHostOptions
        {
            RepositoryRoot = repo, DataPaths = Paths,
            Configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
        }, ct: timeout.Token);
        var services = workspace.Services;
        var goals = services.GetRequiredService<FileCodingGoalStore>();
        var mailbox = services.GetRequiredService<FileGoalMailbox>();
        var orchestrator = services.GetRequiredService<ICodingTurnOrchestrator>();
        var conversations = services.GetRequiredService<IConversationStore>();
        var conversation = await conversations.CreateAsync(ct: timeout.Token);
        const string objective = "This is an isolated goal messaging verification. First execute exactly one shell command: printf 'first\\n' >> counter.txt; sleep 4. "
            + "Wait for that command's completed tool result. Only then, in a separate subsequent tool call, write old-answer.txt containing exactly OLD. "
            + "Do not combine the second operation with the first. No network, credentials, delegation, repository edits, or other work. "
            + "If an authenticated user message arrives during this work, preserve completed actions and use the actual goal_messages.reply host tool to give a useful reply with disposition Incorporated and the supplied messageId/deliveryId. Then finish the remaining updated task.";
        var goal = await goals.CreateOrJoinAsync(new ContinualObjectiveStart
        {
            Surface = "coding-goal", PartitionKey = CodingGoalOrchestrator.Partition,
            IdempotencyKey = "installed-native-goal", ConversationId = conversation.Id,
            ExecutorKey = CodingGoalOrchestrator.ExecutorKey, ObjectiveText = objective, ObjectiveSummary = "Native message proof",
            Authority = ContinualObjectiveAuthority.UserRequested("installed-goal-test"), Provider = "CodexNative", Model = "codex-cli:default",
        }, DateTimeOffset.UtcNow, timeout.Token);
        var lease = await goals.TryClaimAsync(goal.Id, goal.Version, "installed-native-test", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(4), timeout.Token);
        Assert.NotNull(lease);
        var baseline = await goals.GetAsync(goal.Id, timeout.Token);
        var events = new List<TurnEvent>(); GoalInboxMessage? sent = null;
        await foreach (var item in orchestrator.RunAsync(new CodingTurnRequest
        {
            ConversationId = conversation.Id, UserMessage = objective, AuthenticatedOriginalUserMessage = objective,
            GoalId = goal.Id, GoalMessageLeaseToken = lease.Token, GoalMailbox = mailbox,
            LaneName = "native-goal-proof", SelectionPinned = true, AllowHandOff = false, DisableRouteFallback = true,
            RememberModelResponses = false, ExecutionLimits = new() { MaxIterations = 10 },
            Workspace = new() { RepositoryRoot = repo, AcceptanceChecks = [] },
            CommunicationScope = AgentCommunicationScope.UserRequested("installed-goal-test"),
        }, timeout.Token))
        {
            events.Add(item);
            if (sent is null && item is ToolCallStarted { ToolName: CodexNativeLaneRunner.CommandToolName } command
                && command.Input.ToJsonString().Contains("counter.txt", StringComparison.Ordinal))
            {
                const string key = "inflight-message";
                sent = await mailbox.SendAsync(goal.Id, new(key,
                    "Keep the completed counter action. Change the remaining task: write updated-answer.txt containing exactly NEW instead of old-answer.txt. Do not execute the counter command again. Use goal_messages.reply with the supplied IDs, disposition Incorporated, and explain that this change is incorporated without claiming work is already complete. Then finish the new file and briefly confirm.",
                    GoalMessageKind.Correction, new("code-ui", key, "local-user")), timeout.Token);
            }
        }
        var persistedGoal = await goals.GetAsync(goal.Id, timeout.Token);
        var thread = await new FileGoalMailbox(Paths, new FileCodingGoalStore(Paths)).ReadAsync(goal.Id, ct: timeout.Token);
        var transcript = await services.GetRequiredService<IAgentSessionStore>().LoadAsync(conversation.Id, timeout.Token);
        var state = await conversations.GetStateAsync(conversation.Id, timeout.Token);
        if (Environment.GetEnvironmentVariable("SWARMCODE_INSTALLED_GOAL_REPORT") is { Length: > 0 } report)
        {
            var files = new[] { helper, typeof(WorkspaceHost).Assembly.Location, typeof(CodexNativeLaneRunner).Assembly.Location,
                typeof(GoalMessaging).Assembly.Location };
            var hashes = new Dictionary<string, string>();
            foreach (var path in files) hashes[path] = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path, timeout.Token)));
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
            {
                Hashes = hashes, GoalBefore = baseline, GoalAfter = persistedGoal, Thread = thread,
                NativeThreadId = state.GetValueOrDefault(CodexNativeLaneRunner.ThreadStateKey),
                Events = events.Select(item => new { Type = item.GetType().Name, Json = JsonSerializer.Serialize(item, item.GetType()) }),
                Counter = File.Exists(Path.Combine(repo, "counter.txt")) ? await File.ReadAllLinesAsync(Path.Combine(repo, "counter.txt"), timeout.Token) : [],
                UpdatedAnswer = File.Exists(Path.Combine(repo, "updated-answer.txt")) ? await File.ReadAllTextAsync(Path.Combine(repo, "updated-answer.txt"), timeout.Token) : null,
                OldAnswerExists = File.Exists(Path.Combine(repo, "old-answer.txt")),
                PersistedTranscriptHasMessage = transcript.Any(message => message.Parts.OfType<SwarmCore.Abstractions.LlmRouting.TextPart>().Any(part => part.Text.Contains("inflight-message", StringComparison.Ordinal)
                    || part.Text.Contains("Keep the completed counter action", StringComparison.Ordinal))),
            }, new JsonSerializerOptions { WriteIndented = true }), timeout.Token);
        }
        Assert.NotNull(sent);
        Assert.Equal(["first"], await File.ReadAllLinesAsync(Path.Combine(repo, "counter.txt"), timeout.Token));
        Assert.Equal("NEW", (await File.ReadAllTextAsync(Path.Combine(repo, "updated-answer.txt"), timeout.Token)).Trim());
        Assert.False(File.Exists(Path.Combine(repo, "old-answer.txt")));
        var message = Assert.Single(thread.Messages); Assert.Equal(sent.Id, message.Id);
        Assert.Equal(GoalDeliveryState.Incorporated, message.State);
        var reply = Assert.Single(thread.Replies); Assert.Equal(message.Id, reply.MessageId); Assert.False(string.IsNullOrWhiteSpace(reply.Text));
        Assert.Equal(GoalDeliveryState.Incorporated, reply.Disposition);
        Assert.Equal(JsonSerializer.Serialize(baseline), JsonSerializer.Serialize(persistedGoal));
        Assert.Equal(lease.Token, persistedGoal!.LeaseToken); Assert.Equal(ContinualObjectiveStatus.InProgress, persistedGoal.Status);
        Assert.True(state.ContainsKey(CodexNativeLaneRunner.ThreadStateKey));
        Assert.Single(events.OfType<TurnStarted>()); Assert.Single(events.OfType<TurnCompleted>());
        Assert.DoesNotContain(events, item => item is CodingLaneSelected lane && lane.Lane.Provider != "CodexNative");
        Assert.Contains(events.OfType<ToolCallStarted>(), tool => tool.ToolName == CodexNativeLaneRunner.McpToolName
            && tool.Input.ToJsonString().Contains("goal_messages", StringComparison.Ordinal));
    }

    private sealed class InstalledGoalMessagingFactAttribute : FactAttribute
    {
        public InstalledGoalMessagingFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("SWARMCODE_INSTALLED_GOAL_PROBE") != "1")
                Skip = "Explicit isolated installed-native goal integration only.";
        }
    }
}
