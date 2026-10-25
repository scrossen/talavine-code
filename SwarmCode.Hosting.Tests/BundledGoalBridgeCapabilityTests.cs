using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SwarmCode.Hosting.Goals;
using SwarmCode.Hosting.Mcp;
using SwarmCode.Hosting.Secrets;
using SwarmCore.Abstractions.Processes;
using SwarmCore.Agent.Communication;
using SwarmCore.Agent.Continual;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding;
using SwarmCore.Coding.Goals;
using SwarmCore.Coding.Native;
using SwarmCore.Secrets;

namespace SwarmCode.Hosting.Tests;

/// <summary>Zero inference: the exact signed app's real stdio MCP helper crosses the attempt/lease boundary.</summary>
public sealed class BundledGoalBridgeCapabilityTests : TempDirTest
{
    [BundledBridgeFact]
    [Trait("Category", "BundledGoalBridge")]
    public async Task Exact_bundled_helper_rejects_retired_process_capability_and_admits_current_goal_worker()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var app = Path.GetFullPath(Environment.GetEnvironmentVariable("SWARMCODE_BUNDLED_GOAL_APP")!);
        Assert.EndsWith(".app", app);
        var contents = Path.Combine(app, "Contents");
        var expectedHelper = Path.Combine(contents, MacVaultBridge.RelativeHelperPath);
        var launch = McpBridgeLocator.Resolve(Paths, Path.Combine(contents, "MonoBundle"), null, windows: false);
        Assert.NotNull(launch); Assert.Equal(expectedHelper, launch.Command);
        Assert.True(File.Exists(expectedHelper)); Assert.True(MacVaultBridge.IsUnlinkedOwnedFile(contents, expectedHelper));
        await VerifySignatureAsync(app, timeout.Token);
        var helperHashes = PayloadHashes(expectedHelper);
        var repo = Path.Combine(Tmp, "repo"); Directory.CreateDirectory(repo);
        var counter = Path.Combine(repo, "counter.txt"); await File.WriteAllTextAsync(counter, "unchanged\n", timeout.Token);
        var counterHash = Hash(await File.ReadAllBytesAsync(counter, timeout.Token));
        var settings = new Dictionary<string, string?>
        {
            ["Coding:Agents:Enabled"] = "false",
            ["Coding:EnabledToolIds:0"] = "goal_messages.read",
            ["Coding:EnabledToolIds:1"] = "goal_messages.reply",
        };
        McpBridgeLocator.AddOverrides(settings, Paths, launch);
        await using var workspace = await WorkspaceHost.CreateAsync(new SwarmCodeHostOptions
        {
            RepositoryRoot = repo, DataPaths = Paths,
            Configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            ConfigureServices = services => services.AddSingleton<IOsKeyStore>(new EncryptedTestKeyStore()),
        }, ct: timeout.Token);
        var services = workspace.Services;
        var goals = services.GetRequiredService<FileCodingGoalStore>();
        var mailbox = services.GetRequiredService<FileGoalMailbox>();
        var active = services.GetRequiredService<ActiveCodingWorkspaces>();
        var broker = services.GetRequiredService<HostToolBridgeBroker>();
        var conversation = await services.GetRequiredService<IConversationStore>().CreateAsync(ct: timeout.Token);
        var goal = await goals.CreateOrJoinAsync(new ContinualObjectiveStart
        {
            Surface = "coding-goal", PartitionKey = CodingGoalOrchestrator.Partition, IdempotencyKey = "bundled-bridge",
            ConversationId = conversation.Id, ExecutorKey = CodingGoalOrchestrator.ExecutorKey,
            ObjectiveText = "Synthetic goal: preserve the counter and perform no physical work.", ObjectiveSummary = "Synthetic bridge verification",
            Authority = ContinualObjectiveAuthority.UserRequested("bundled-bridge-fixture"),
        }, DateTimeOffset.UtcNow, timeout.Token);
        var leaseA = await ClaimAsync(goals, goal, "worker-A", timeout.Token);
        var first = await mailbox.SendAsync(goal.Id, Command("first", "A complete message for worker A."), timeout.Token);
        using var entryA = active.Enter(conversation.Id, new() { RepositoryRoot = repo },
            allowedTools: ["goal_messages.read", "goal_messages.reply"], goalId: goal.Id, goalMessageLeaseToken: leaseA.Token);
        var capabilityA = active.BridgeCapability(conversation.Id)!;
        var proxyA = new HostToolBridgeProxy(broker.GetEndpoint(), conversation.Id, capabilityA);
        await using var processA = await BridgeChild.StartAsync(expectedHelper, repo, Paths, broker.GetEndpoint(), conversation.Id, capabilityA, timeout.Token);
        var toolsA = await processA.Client.ListToolsAsync(cancellationToken: timeout.Token);
        Assert.Equal(new[] { "goal_messages_read", "goal_messages_reply" }, toolsA.Select(t => t.Name).Order().ToArray());
        var firstDelivery = await mailbox.BeginDeliveryForWorkerAsync(goal.Id, conversation.Id, "worker-A", leaseA.Token, ct: timeout.Token);
        Assert.NotNull(firstDelivery);
        var firstRead = await ReadAsync(processA.Client, 0, timeout.Token);
        Assert.Equal(first.Text, (string?)firstRead["thread"]?["messages"]?[0]?["text"]);
        var pausedBaseline = (await goals.GetAsync(goal.Id, timeout.Token))!;
        Assert.True(await goals.ControlAsync(goal.Id, pausedBaseline.Version, CodingGoalControl.Pause, DateTimeOffset.UtcNow, timeout.Token));
        var pausedRead = await CallAsync(processA.Client, "goal_messages_read", new() { ["afterSequence"] = 0L }, timeout.Token);
        Assert.True(pausedRead.IsError);
        var paused = (await goals.GetAsync(goal.Id, timeout.Token))!;
        Assert.True(await goals.ControlAsync(goal.Id, paused.Version, CodingGoalControl.Resume, DateTimeOffset.UtcNow, timeout.Token));
        var resumed = (await goals.GetAsync(goal.Id, timeout.Token))!;
        var leaseB = await ClaimAsync(goals, resumed, "worker-B", timeout.Token);
        using var entryB = active.Enter(conversation.Id, new() { RepositoryRoot = repo },
            allowedTools: ["goal_messages.read", "goal_messages.reply"], goalId: goal.Id, goalMessageLeaseToken: leaseB.Token);
        var capabilityB = active.BridgeCapability(conversation.Id)!;
        Assert.NotEqual(capabilityA, capabilityB); Assert.NotEqual(leaseA.Token, leaseB.Token);
        var second = await mailbox.SendAsync(goal.Id, Command("second", "The complete current message belongs to worker B."), timeout.Token);
        var beforeRejectedCalls = await goals.GetAsync(goal.Id, timeout.Token);
        var rejectedClaim = await mailbox.BeginDeliveryForWorkerAsync(goal.Id, conversation.Id, "retired-worker-A", leaseA.Token, ct: timeout.Token);
        Assert.Null(rejectedClaim);
        Assert.Equal(GoalDeliveryState.Received, (await mailbox.ReadAsync(goal.Id, ct: timeout.Token)).Messages.Single(m => m.Id == second.Id).State);
        Assert.Empty(await processA.Client.ListToolsAsync(cancellationToken: timeout.Token));
        var retiredRead = await CallAsync(processA.Client, "goal_messages_read", new() { ["afterSequence"] = 0L }, timeout.Token);
        Assert.True(retiredRead.IsError);
        await Assert.ThrowsAsync<InvalidOperationException>(() => proxyA.ListAsync(timeout.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => proxyA.CallAsync("goal_messages_read", JsonSerializer.SerializeToElement(new { afterSequence = 0L }), timeout.Token));
        Assert.Equal(JsonSerializer.Serialize(beforeRejectedCalls), JsonSerializer.Serialize(await goals.GetAsync(goal.Id, timeout.Token)));

        await using var processB = await BridgeChild.StartAsync(expectedHelper, repo, Paths, broker.GetEndpoint(), conversation.Id, capabilityB, timeout.Token);
        var toolsB = await processB.Client.ListToolsAsync(cancellationToken: timeout.Token);
        Assert.Equal(new[] { "goal_messages_read", "goal_messages_reply" }, toolsB.Select(t => t.Name).Order().ToArray());
        var deliveryB = await mailbox.BeginDeliveryForWorkerAsync(goal.Id, conversation.Id, "worker-B", leaseB.Token, ct: timeout.Token);
        Assert.NotNull(deliveryB); Assert.Equal(second.Id, Assert.Single(deliveryB.Messages).Id);
        var currentGoalBaseline = await goals.GetAsync(goal.Id, timeout.Token);
        var page = await ReadAsync(processB.Client, 0, timeout.Token);
        while ((string?)page["thread"]?["messages"]?[0]?["id"] != second.Id)
        {
            Assert.True((bool?)page["thread"]?["hasMore"]);
            page = await ReadAsync(processB.Client, (long)page["thread"]!["nextSequence"]!, timeout.Token);
        }
        Assert.Equal(second.Text, (string?)page["thread"]?["messages"]?[0]?["text"]);
        var replyArguments = new Dictionary<string, object?>
        {
            ["messageId"] = second.Id, ["deliveryId"] = deliveryB.Id,
            ["text"] = "The current worker incorporated the complete message. No physical work was performed.", ["disposition"] = "Incorporated",
        };
        Assert.True((await CallAsync(processA.Client, "goal_messages_reply", replyArguments, timeout.Token)).IsError);
        var reply = await CallAsync(processB.Client, "goal_messages_reply", replyArguments, timeout.Token);
        Assert.False(reply.IsError, Text(reply));
        var receipt = JsonNode.Parse(Text(reply))!;
        Assert.True((bool?)receipt["saved"]); Assert.Equal(second.Id, (string?)receipt["MessageId"]);
        entryA.Dispose(); // A late frame cleanup must not erase B's authority.
        Assert.False((await CallAsync(processB.Client, "goal_messages_read", new() { ["afterSequence"] = 0L }, timeout.Token)).IsError);
        var persisted = await new FileGoalMailbox(Paths, new FileCodingGoalStore(Paths)).ReadAsync(goal.Id, ct: timeout.Token);
        var persistedReply = Assert.Single(persisted.Replies);
        Assert.Equal(second.Id, persistedReply.MessageId); Assert.Equal(GoalDeliveryState.Incorporated, persistedReply.Disposition);
        Assert.Equal(GoalDeliveryState.Incorporated, persisted.Messages.Single(m => m.Id == second.Id).State);
        Assert.Equal(JsonSerializer.Serialize(currentGoalBaseline), JsonSerializer.Serialize(await goals.GetAsync(goal.Id, timeout.Token)));
        Assert.Equal(counterHash, Hash(await File.ReadAllBytesAsync(counter, timeout.Token)));
        foreach (var value in new[] { firstRead.ToJsonString(), page.ToJsonString(), Text(reply), Text(retiredRead), Text(pausedRead) })
            foreach (var privateValue in new[] { leaseA.Token, leaseB.Token, capabilityA, capabilityB }) Assert.DoesNotContain(privateValue, value);
        if (Environment.GetEnvironmentVariable("SWARMCODE_BUNDLED_GOAL_REPORT") is { Length: > 0 } report)
        {
            var hostAssemblies = new[] { typeof(WorkspaceHost).Assembly.Location, typeof(HostToolBridgeBroker).Assembly.Location, typeof(GoalMessaging).Assembly.Location };
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
            {
                App = app, Helper = expectedHelper, HelperPayloadHashes = helperHashes,
                HostPayloadHashes = hostAssemblies.ToDictionary(p => p, p => Hash(File.ReadAllBytes(p))),
                ModelCalls = 0, Processes = new[] { processA.Id, processB.Id },
                ATools = toolsA.Select(t => t.Name), BTools = toolsB.Select(t => t.Name),
                PausedReadRejected = pausedRead.IsError, RetiredReadRejected = retiredRead.IsError,
                RetiredClaimRejected = rejectedClaim is null, CurrentMessageBodyExact = (string?)page["thread"]?["messages"]?[0]?["text"] == second.Text,
                BoundReplyPersisted = persistedReply.MessageId == second.Id, CounterUnchanged = true, GoalUnchangedAfterCurrentReply = true,
                ReplyDisposition = persistedReply.Disposition.ToString(), AttemptCapabilitiesDistinct = capabilityA != capabilityB,
                OldCleanupPreservesCurrentAuthority = true, PrivateValuesAbsentFromResponses = true,
            }, new JsonSerializerOptions { WriteIndented = true }), timeout.Token);
        }
    }

    private static GoalMessageCommand Command(string key, string text) => new(key, text, GoalMessageKind.Idea, new("code-ui", key, "local-user"));
    private static async Task<ContinualObjectiveLease> ClaimAsync(FileCodingGoalStore goals, ContinualObjectiveSnapshot goal, string worker, CancellationToken ct)
        => (await goals.TryClaimAsync(goal.Id, goal.Version, worker, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(3), ct))!;
    private static Task<CallToolResult> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments, CancellationToken ct)
        => client.CallToolAsync(tool, arguments, cancellationToken: ct).AsTask();
    private static string Text(CallToolResult result) => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
    private static async Task<JsonNode> ReadAsync(McpClient client, long after, CancellationToken ct)
    {
        var result = await CallAsync(client, "goal_messages_read", new() { ["afterSequence"] = after }, ct);
        Assert.False(result.IsError, Text(result)); return JsonNode.Parse(Text(result))!;
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static Dictionary<string, string> PayloadHashes(string helper)
    {
        var result = new Dictionary<string, string> { [helper] = Hash(File.ReadAllBytes(helper)) };
        foreach (var name in new[] { "swarmcode.dll", "SwarmCode.Hosting.dll", "SwarmCore.Coding.dll", "SwarmCore.Agent.dll" })
        {
            var path = Path.Combine(Path.GetDirectoryName(helper)!, name); Assert.True(File.Exists(path), "Missing bundled bridge payload: " + name);
            result[path] = Hash(File.ReadAllBytes(path));
        }
        return result;
    }
    private static async Task VerifySignatureAsync(string app, CancellationToken ct)
    {
        using var child = ChildProcessLauncher.Start(new ProcessStartInfo("/usr/bin/codesign")
        { ArgumentList = { "--verify", "--deep", "--strict", app }, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true });
        var error = child.StandardError.ReadToEndAsync(ct); var output = child.StandardOutput.ReadToEndAsync(ct);
        await child.WaitForExitAsync(ct); await output; Assert.True(child.ExitCode == 0, await error);
    }
    private sealed class BridgeChild(IChildProcess process, McpClient client, Task<string> error) : IAsyncDisposable
    {
        public McpClient Client => client;
        public int Id => process.Id;
        public static async Task<BridgeChild> StartAsync(string command, string repo, SwarmCodeDataPaths paths,
            string pipe, string conversation, string capability, CancellationToken ct)
        {
            var start = new ProcessStartInfo(command)
            {
                WorkingDirectory = repo, UseShellExecute = false, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                ArgumentList = { "--mcp-server", "--repo", repo },
            };
            start.Environment[SwarmCodeDataPaths.HomeEnvironmentVariable] = paths.RootDirectory;
            start.Environment[HostToolBridgeBroker.PipeEnvironmentVariable] = pipe;
            start.Environment["SWARMCODE_CONVERSATION_ID"] = conversation;
            start.Environment[HostToolBridgeBroker.AttemptEnvironmentVariable] = capability;
            var child = ChildProcessLauncher.Start(start);
            var error = child.StandardError.ReadToEndAsync(ct);
            try
            {
                var transport = new StreamClientTransport(child.StandardInput.BaseStream, child.StandardOutput.BaseStream);
                var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
                return new(child, client, error);
            }
            catch { try { child.Kill(true); } catch { } child.Dispose(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            try { await client.DisposeAsync(); } finally
            {
                try { if (!process.HasExited) process.Kill(true); } catch { }
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
                try { await error; } catch { } process.Dispose();
            }
        }
    }
    private sealed class BundledBridgeFactAttribute : FactAttribute
    {
        public BundledBridgeFactAttribute()
        {
            if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("SWARMCODE_BUNDLED_GOAL_PROBE") != "1")
                Skip = "Explicit final signed macOS bundle, zero inference only.";
        }
    }
}
