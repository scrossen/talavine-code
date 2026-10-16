using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Native;
using SwarmCore.Coding.Routing;

namespace SwarmCore.Coding.Tests;

public sealed class NativeHostContextTests
{
    private static readonly CodingLane Claude = new("claude", "ClaudeCodeNative", "claude-code:default");

    private sealed class FakeInjector(string name, Func<ContextInjectionRequest, string?> build) : IContextInjector
    {
        public string Name => name;
        public List<ContextInjectionRequest> Requests { get; } = [];
        public Task<string?> BuildAsync(ContextInjectionRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(build(request));
        }
    }

    private sealed class ThrowingInjector : IContextInjector
    {
        public string Name => "broken";
        public Task<string?> BuildAsync(ContextInjectionRequest request, CancellationToken ct = default) => throw new InvalidOperationException("boom");
    }

    private static NativeLaneTurn Turn(string conversation, IReadOnlyList<NativeHostContextBlock> blocks, string message = "fix the build")
        => new(conversation, message, Claude, Path.GetTempPath(), "test", IsContinuation: false) { HostContext = blocks };

    [Fact]
    public async Task Builds_only_opted_in_injectors_in_order_within_budget_and_survives_failures()
    {
        var services = new ServiceCollection();
        var digest = new FakeInjector("personal-memory", _ => "<user-memory-profile>User prefers small commits.</user-memory-profile>");
        var big = new FakeInjector("repoMemory", _ => new string('x', 5_000));
        var notListed = new FakeInjector("emails", _ => "inbox content");
        services.AddSingleton<IContextInjector>(digest);
        services.AddSingleton<IContextInjector>(big);
        services.AddSingleton<IContextInjector>(notListed);
        services.AddSingleton<IContextInjector>(new ThrowingInjector());
        using var provider = services.BuildServiceProvider();
        var options = new CodingOptions { NativeLaneInjectorNames = ["broken", "personal-memory", "repoMemory", "missing"], MaxNativeHostContextCharacters = 1_000 };

        var blocks = await NativeHostContext.BuildAsync(provider, options, new ContextInjectionRequest("c", "hi", Guid.Empty), NullLogger.Instance, default);

        Assert.Equal(["personal-memory", "repoMemory"], blocks.Select(b => b.Name));
        Assert.True(blocks.Sum(b => b.Text.Length) <= 1_000);
        Assert.Contains("truncated", blocks[1].Text);
        Assert.Empty(notListed.Requests); // never runs an injector the host did not opt into
        Assert.Empty(await NativeHostContext.BuildAsync(provider, new CodingOptions(), new ContextInjectionRequest("c", "hi", Guid.Empty), null, default));
    }

    [Fact]
    public void First_launch_gets_every_block_and_a_resumed_session_only_changed_blocks()
    {
        var conversation = "native-ctx-" + Guid.NewGuid().ToString("N");
        var digest = new NativeHostContextBlock("personal-memory", "<user-memory-profile>User prefers small commits.</user-memory-profile>");
        var index = new NativeHostContextBlock("repoMemory", "## Repository memory\nIndex: MEMORY.md");

        var first = NativeLaneSupport.BuildPrompt(Turn(conversation, [digest, index]), isFirstTurn: true, mcp: null, "CLAUDE.md");
        Assert.Contains(NativeHostContext.Preamble, first);
        Assert.Contains("User prefers small commits.", first);
        Assert.Contains("Index: MEMORY.md", first);
        Assert.True(first.IndexOf("User prefers", StringComparison.Ordinal) < first.IndexOf("Current user request:", StringComparison.Ordinal));

        // Same context on a resumed CLI session: plain user message, nothing repeated.
        var resumed = NativeLaneSupport.BuildPrompt(Turn(conversation, [digest, index], "next step"), isFirstTurn: false, mcp: null, "CLAUDE.md");
        Assert.Equal("next step", resumed);

        // The digest changed (a memory was learned): only that block is re-sent, framed as reference data.
        var updated = digest with { Text = "<user-memory-profile>User prefers small commits. User works in C#.</user-memory-profile>" };
        var changed = NativeLaneSupport.BuildPrompt(Turn(conversation, [updated, index], "and then"), isFirstTurn: false, mcp: null, "CLAUDE.md");
        Assert.StartsWith(NativeHostContext.UpdatedPreamble, changed);
        Assert.Contains("User works in C#.", changed);
        Assert.DoesNotContain("Index: MEMORY.md", changed);
        Assert.EndsWith("Current user request:\nand then", changed);

        // A new CLI session (resume lost) gets everything again.
        var fresh = NativeLaneSupport.BuildPrompt(Turn(conversation, [updated, index], "again"), isFirstTurn: true, mcp: null, "CLAUDE.md");
        Assert.Contains("Index: MEMORY.md", fresh);
    }

    [Fact]
    public void A_turn_without_host_context_keeps_the_previous_prompt_shape()
    {
        var conversation = "native-ctx-" + Guid.NewGuid().ToString("N");
        Assert.Equal("just this", NativeLaneSupport.BuildPrompt(Turn(conversation, [], "just this"), isFirstTurn: false, mcp: null, "CLAUDE.md"));
        Assert.DoesNotContain(NativeHostContext.Preamble, NativeLaneSupport.BuildPrompt(Turn(conversation, []), isFirstTurn: true, mcp: null, "CLAUDE.md"));
    }

    private sealed class RecordingNative(List<NativeLaneTurn> seen) : INativeLaneRunner
    {
        public bool Supports(CodingLane lane) => lane.IsNative;
        public async IAsyncEnumerable<TurnEvent> RunAsync(NativeLaneTurn turn, [EnumeratorCancellation] CancellationToken ct = default)
        {
            lock (seen) seen.Add(turn);
            await Task.Yield();
            yield return new TurnCompleted("n", "native done", TimeSpan.Zero);
        }
    }

    [Fact]
    public async Task Orchestrator_hands_native_lanes_the_opted_in_context_with_the_authenticated_message()
    {
        var seen = new List<NativeLaneTurn>();
        var injector = new FakeInjector("personal-memory", r => "digest for " + r.UserMessage);
        var services = new ServiceCollection();
        services.AddScoped<INativeLaneRunner>(_ => new RecordingNative(seen));
        services.AddSingleton<IContextInjector>(injector);
        services.AddSingleton<ICodingLaneSource>(new OptionsCodingLaneSource(Options.Create(new CodingLaneOptions { Lanes = [Claude], HandOffOnRefusal = false })));
        services.AddSingleton<ICodingWorkspaceResolver>(new TestOrchestrator.FixedWorkspace(new CodingWorkspace { RepositoryRoot = TestOrchestrator.Workspace }));
        using var provider = services.BuildServiceProvider();
        var orchestrator = new CodingTurnOrchestrator(provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new CodingOptions { NativeLaneInjectorNames = ["personal-memory"] }), NullLogger<CodingTurnOrchestrator>.Instance);

        await foreach (var _ in orchestrator.RunAsync(new CodingTurnRequest
        {
            ConversationId = "native-ctx-" + Guid.NewGuid().ToString("N"),
            UserMessage = "augmented message",
            AuthenticatedOriginalUserMessage = "what the person typed",
            LaneName = "claude",
            CommunicationScope = AgentCommunicationScope.UserRequested("test"),
        })) { }

        var turn = Assert.Single(seen);
        var block = Assert.Single(turn.HostContext);
        Assert.Equal("digest for what the person typed", block.Text);
        Assert.Equal(Path.GetFullPath(TestOrchestrator.Workspace).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(injector.Requests[0].WorkspaceRoot!).TrimEnd(Path.DirectorySeparatorChar));
    }
}
