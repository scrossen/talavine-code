using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwarmCore.Agent.CodingTasks;
using SwarmCore.Agent.Runtime;
using SwarmCore.Agent.ToolUseEngine;
using SwarmCore.Coding.Native;
using SwarmCore.Coding.Routing;

namespace SwarmCore.Coding.Tests;

public sealed class CodexContainmentCapabilityTests
{
    private static NativeLaneTurn Turn(NativeLaneContainment containment)
        => new("owned", "do nothing", new("codex", CodexNativeLaneRunner.ProviderId, "default"), Path.GetTempPath(), "user", false)
        { Containment = containment };

    [Theory]
    [InlineData(null, false)]
    [InlineData("--ignore-user-config", false)]
    [InlineData("--ignore-rules", false)]
    [InlineData("--ignore-user-config --ignore-rules", true)]
    public async Task Admission_and_direct_invocation_require_both_configuration_boundaries(string? help, bool expected)
    {
        var executable = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-codex.exe");
        var started = 0;
        var conversations = new Conversations();
        var sessions = new InMemoryAgentSessionStore();
        NativeCliCapabilities.ProbeOverride = (_, _) => help;
        try
        {
            var runner = new CodexNativeLaneRunner(conversations, sessions, Options.Create(new CodingOptions()), NullLogger<CodexNativeLaneRunner>.Instance)
            {
                ExecutableResolver = () => executable,
                ProcessStarter = _ => { started++; throw new InvalidOperationException("OWNED_START_INTERCEPTED"); }
            };
            var supported = ((INativeLaneRunner)runner).SupportsContainment(NativeLaneContainment.WorkspaceConfined);
            var events = new List<TurnEvent>();
            await foreach (var e in runner.RunAsync(Turn(NativeLaneContainment.WorkspaceConfined))) events.Add(e);
            Assert.Equal(expected, supported);
            Assert.True(((INativeLaneRunner)runner).SupportsContainment(NativeLaneContainment.Bypass));
            if (expected)
            {
                Assert.Equal(1, started);
                Assert.Contains(events.OfType<TurnErrored>(), e => e.Message.Contains("OWNED_START_INTERCEPTED"));
            }
            else
            {
                Assert.Equal(0, started);
                Assert.Equal(0, conversations.Calls);
                Assert.Empty(await sessions.LoadAsync("owned"));
                var error = Assert.Single(events.OfType<TurnErrored>());
                Assert.Contains("--ignore-user-config", error.Message);
                Assert.Contains("--ignore-rules", error.Message);
                Assert.DoesNotContain(events, e => e is TurnStarted);
            }
        }
        finally { NativeCliCapabilities.ProbeOverride = null; }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Unsafe_confined_start_info_is_never_constructed(bool ignoreConfig, bool ignoreRules)
    {
        var error = Assert.Throws<InvalidOperationException>(() => CodexNativeLaneRunner.BuildStartInfo("codex", Turn(NativeLaneContainment.WorkspaceConfined), null,
            new NativeAgentOptions(), ignoreConfig, ignoreRules));
        Assert.Contains("--ignore-user-config", error.Message);
        Assert.Contains("--ignore-rules", error.Message);
        var bypass = CodexNativeLaneRunner.BuildStartInfo("codex", Turn(NativeLaneContainment.Bypass), null, new NativeAgentOptions(), ignoreConfig, ignoreRules);
        Assert.Contains("--dangerously-bypass-approvals-and-sandbox", bypass.ArgumentList);
        Assert.DoesNotContain("--ignore-user-config", bypass.ArgumentList);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("--ignore-user-config")]
    [InlineData("--ignore-rules")]
    public async Task Direct_confined_invocation_refuses_before_storage_or_child_launch(string? help)
    {
        var executable = Guid.NewGuid().ToString("N") + "-unavailable-cli";
        var started = 0;
        NativeCliCapabilities.ProbeOverride = (_, _) => help;
        try
        {
            var runner = new CodexNativeLaneRunner(null!, null!, Options.Create(new CodingOptions()), NullLogger<CodexNativeLaneRunner>.Instance)
            {
                ExecutableResolver = () => executable,
                ProcessStarter = _ => { started++; throw new InvalidOperationException("No child may start."); }
            };
            var events = new List<TurnEvent>();
            await foreach (var e in runner.RunAsync(Turn(NativeLaneContainment.WorkspaceConfined))) events.Add(e);
            Assert.Equal(0, started);
            Assert.Contains("--ignore-user-config", Assert.Single(events.OfType<TurnErrored>()).Message);
            Assert.DoesNotContain(events, e => e is TurnStarted);
        }
        finally { NativeCliCapabilities.ProbeOverride = null; }
    }

    [Fact]
    public void Failed_help_cannot_admit_a_confined_cli_even_if_stdout_names_both_flags()
    {
        var shell = OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : "/bin/sh";
        var args = OperatingSystem.IsWindows()
            ? new[] { "/d", "/c", "echo --ignore-user-config --ignore-rules & exit /b 17" }
            : new[] { "-c", "printf '%s\\n' '--ignore-user-config --ignore-rules'; exit 17" };
        Assert.False(NativeCliCapabilities.Supports(shell, CodexNativeLaneRunner.IgnoreUserConfigFlag, args));
        Assert.False(NativeCliCapabilities.Supports(shell, CodexNativeLaneRunner.IgnoreRulesFlag, args));
    }

    [Fact]
    public async Task Bypass_invocation_never_probes_the_required_confined_flags()
    {
        var started = 0;
        NativeCliCapabilities.ProbeOverride = (_, _) => throw new InvalidOperationException("Bypass must not probe confinement.");
        try
        {
            var runner = new CodexNativeLaneRunner(new Conversations(), new InMemoryAgentSessionStore(), Options.Create(new CodingOptions()), NullLogger<CodexNativeLaneRunner>.Instance)
            {
                ExecutableResolver = () => "owned-codex.exe",
                ProcessStarter = info =>
                {
                    Assert.Contains("--dangerously-bypass-approvals-and-sandbox", info.ArgumentList);
                    Assert.DoesNotContain("--ignore-user-config", info.ArgumentList);
                    Assert.DoesNotContain("--ignore-rules", info.ArgumentList);
                    started++;
                    throw new InvalidOperationException("OWNED_BYPASS_START_INTERCEPTED");
                }
            };
            var events = new List<TurnEvent>();
            await foreach (var e in runner.RunAsync(Turn(NativeLaneContainment.Bypass))) events.Add(e);
            Assert.Equal(1, started);
            Assert.Contains(events.OfType<TurnErrored>(), e => e.Message.Contains("OWNED_BYPASS_START_INTERCEPTED"));
        }
        finally { NativeCliCapabilities.ProbeOverride = null; }
    }

    [Fact]
    public void Task_receipt_never_claims_confinement_arguments_when_capabilities_are_unavailable()
    {
        NativeCliCapabilities.ProbeOverride = (_, _) => null;
        try
        {
            var receipt = NativeLaneCodingTaskAgentRunner.DescribeContainment(Turn(NativeLaneContainment.WorkspaceConfined), new NativeAgentOptions(),
                _ => Guid.NewGuid().ToString("N") + "-missing");
            Assert.Contains(CodexNativeLaneRunner.ConfinementUnavailableReason, receipt);
            Assert.DoesNotContain("workspace-write", receipt);
        }
        finally { NativeCliCapabilities.ProbeOverride = null; }
    }

    [Fact]
    public async Task Admitted_containment_cannot_be_widened_by_configuration_change_during_storage()
    {
        var native = new NativeAgentOptions { DefaultContainment = NativeLaneContainment.WorkspaceConfined };
        var conversations = new Conversations { StateRead = () => native.DefaultContainment = NativeLaneContainment.Bypass };
        List<string>? captured = null;
        NativeCliCapabilities.ProbeOverride = (_, _) => "--ignore-user-config --ignore-rules";
        try
        {
            var runner = new CodexNativeLaneRunner(conversations, new InMemoryAgentSessionStore(), Options.Create(new CodingOptions { NativeAgents = native }),
                NullLogger<CodexNativeLaneRunner>.Instance)
            {
                ExecutableResolver = () => Guid.NewGuid().ToString("N") + "-owned-cli",
                ProcessStarter = info => { captured = info.ArgumentList.ToList(); throw new InvalidOperationException("OWNED_START_INTERCEPTED"); }
            };
            await foreach (var _ in runner.RunAsync(Turn(NativeLaneContainment.WorkspaceConfined) with { Containment = null })) { }
            Assert.NotNull(captured);
            Assert.Equal(NativeLaneContainment.Bypass, native.DefaultContainment); // mutation really occurred
            Assert.DoesNotContain("--dangerously-bypass-approvals-and-sandbox", captured);
            Assert.Contains("--ignore-user-config", captured);
            Assert.Contains("--ignore-rules", captured);
            Assert.Contains("sandbox_workspace_write.network_access=false", captured);
        }
        finally { NativeCliCapabilities.ProbeOverride = null; }
    }

    [Fact]
    public async Task Unsupported_coding_task_refuses_before_hidden_conversation_creation()
    {
        var conversations = new Conversations();
        var childStarts = 0;
        NativeCliCapabilities.ProbeOverride = (_, _) => null;
        var accessor = new AsyncLocalCurrentTurnContextAccessor
        {
            Current = new("chat", null, Guid.NewGuid(), null, null, CommunicationScopeKind: AgentCommunicationScopeKind.TrustedProfileWide,
                CommunicationScopeAuthority: "user", CommunicationScopeUserInitiated: true)
        };
        try
        {
            var native = new CodexNativeLaneRunner(conversations, new InMemoryAgentSessionStore(), Options.Create(new CodingOptions()), NullLogger<CodexNativeLaneRunner>.Instance)
            {
                ExecutableResolver = () => Guid.NewGuid().ToString("N") + "-missing-cli",
                ProcessStarter = _ => { childStarts++; throw new InvalidOperationException("No child may start."); }
            };
            var services = new ServiceCollection();
            services.AddSingleton<INativeLaneRunner>(native);
            services.AddSingleton<IConversationStore>(conversations);
            await using var provider = services.BuildServiceProvider();
            var task = new NativeLaneCodingTaskAgentRunner(provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(new CodingOptions()),
                NullLogger<NativeLaneCodingTaskAgentRunner>.Instance, accessor);
            var result = await task.RunAsync(new("codex", "do nothing", Path.GetTempPath(), "owned", "owned", TimeSpan.FromSeconds(1)));
            Assert.Equal(1, result.ExitCode);
            Assert.Equal(0, conversations.HiddenConversationCreates);
            Assert.Contains(CodingTurnOrchestrator.NativeLaneCannotBeConfined, result.StandardError);
            Assert.Equal(0, conversations.Calls);
            Assert.Equal(0, childStarts);
        }
        finally { accessor.Current = null; NativeCliCapabilities.ProbeOverride = null; }
    }

    private sealed class Conversations : IConversationStore
    {
        public int Calls;
        public int HiddenConversationCreates;
        public Action? StateRead;
        public Task<IReadOnlyDictionary<string, object>> GetStateAsync(string conversationId, CancellationToken ct = default)
        { Calls++; StateRead?.Invoke(); return Task.FromResult<IReadOnlyDictionary<string, object>>(new Dictionary<string, object>()); }
        public Task<ChatTurnInfo> AppendUserTurnAsync(string conversationId, string userText, IReadOnlyList<TurnImageAttachment>? images = null,
            IReadOnlyList<TurnFileAttachment>? files = null, CancellationToken ct = default)
        { Calls++; return Task.FromResult(new ChatTurnInfo(Guid.NewGuid(), conversationId, 1, userText, null, null, null, DateTime.UtcNow, null, null)); }
        public Task CompleteAssistantTurnAsync(Guid turnId, string assistantText, Guid? profileId, TimeSpan duration, string? reasoningContent = null,
            CancellationToken ct = default, string? metadataJson = null) => Task.CompletedTask;
        public Task UpdateStateAsync(string conversationId, IReadOnlyDictionary<string, object> stateUpdates, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ChatConversationInfo> GetOrCreateAsync(string conversationId, string title, Guid? profileId = null, CancellationToken ct = default)
        { HiddenConversationCreates++; return Task.FromResult(new ChatConversationInfo(conversationId, title, profileId, DateTime.UtcNow, DateTime.UtcNow)); }
        public Task<ChatConversationInfo> CreateAsync(Guid? profileId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChatConversationInfo?> GetAsync(string conversationId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ChatConversationInfo>> ListRecentAsync(int take = 25, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetLatestIdAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ChatTurnInfo>> LoadTurnsAsync(string conversationId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string conversationId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RenameAsync(string conversationId, string title, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetPinnedAsync(string conversationId, bool isPinned, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
