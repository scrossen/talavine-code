using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwarmCore.Agent.CodingTasks;
using SwarmCore.Agent.InstructionChat;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Native;
using SwarmCore.Coding.Routing;
using SwarmCore.Coding.Secrets;
using SwarmCore.Secrets;
using Xunit;

namespace SwarmCore.Coding.Tests;

/// <summary>What a governed host (Talavine) relies on: containment, native-lane admission, workspace policy, strict detection.</summary>
public sealed class GovernedHostTests
{
    private static NativeLaneTurn Turn(string provider, NativeLaneContainment? containment)
        => new("c", "fix it", new CodingLane("lane", provider, "default"), Path.GetTempPath().TrimEnd('\\', '/'), "user", false)
        { Containment = containment };

    private static List<string> Pair(List<string> args, string flag)
    {
        var i = args.IndexOf(flag);
        return i < 0 || i + 1 >= args.Count ? [] : [args[i], args[i + 1]];
    }

    // ── per-request containment and exact CLI arguments ─────────

    [Fact]
    public void Claude_code_confined_has_no_command_web_or_mcp_tools_ignores_user_settings_and_prefers_restricted()
    {
        var root = Path.GetTempPath().TrimEnd('\\', '/');
        var restricted = ClaudeCodeNativeLaneRunner.BuildStartInfo("claude.exe", Turn(ClaudeCodeNativeLaneRunner.ProviderId, null), null, null,
            NativeLaneContainment.WorkspaceConfined, restrictedSupported: true).ArgumentList.ToList();
        Assert.Equal(
            ["-p", "--output-format", "stream-json", "--verbose",
             "--restricted", "--strict-mcp-config",
             "--permission-mode", "acceptEdits",
             "--tools", "Read,Edit,MultiEdit,Write,Glob,Grep",
             "--disallowedTools", "Bash,PowerShell,WebFetch,WebSearch,mcp__*",
             "--add-dir", root],
            restricted);

        // An older CLI without --restricted: only project settings, every hook disabled, same tool set.
        var older = ClaudeCodeNativeLaneRunner.BuildStartInfo("claude.exe", Turn(ClaudeCodeNativeLaneRunner.ProviderId, null), null, null,
            NativeLaneContainment.WorkspaceConfined, restrictedSupported: false).ArgumentList.ToList();
        Assert.Equal(
            ["-p", "--output-format", "stream-json", "--verbose",
             "--setting-sources", "project", "--settings", "{\"disableAllHooks\":true}", "--strict-mcp-config",
             "--permission-mode", "acceptEdits",
             "--tools", "Read,Edit,MultiEdit,Write,Glob,Grep",
             "--disallowedTools", "Bash,PowerShell,WebFetch,WebSearch,mcp__*",
             "--add-dir", root],
            older);
        Assert.DoesNotContain("--mcp-config", older);

        var swarmCode = ClaudeCodeNativeLaneRunner.BuildStartInfo("claude.exe", Turn(ClaudeCodeNativeLaneRunner.ProviderId, null), null)
            .ArgumentList.ToList();
        Assert.Equal(["-p", "--output-format", "stream-json", "--verbose", "--permission-mode", "bypassPermissions"], swarmCode);
    }

    [Fact]
    public void Codex_confined_uses_its_sandbox_without_network_mcp_user_config_or_rules_and_swarmcode_keeps_bypass()
    {
        var talavine = new NativeAgentOptions { DefaultContainment = NativeLaneContainment.WorkspaceConfined };
        var confined = CodexNativeLaneRunner.BuildStartInfo("codex.exe", Turn(CodexNativeLaneRunner.ProviderId, null), null, talavine,
            ignoreUserConfigSupported: true, ignoreRulesSupported: true, windows: true).ArgumentList.ToList();
        var start = confined.IndexOf("-s");
        Assert.Equal(
            ["-s", "workspace-write",
             "-c", "approval_policy='never'",
             "-c", "sandbox_workspace_write.network_access=false",
             "-c", "mcp_servers={}",
             // The user's Codex app connectors are a built-in feature mcp_servers={} does not cover.
             "-c", "features.apps=false",
             "-c", "sandbox_workspace_write.exclude_tmpdir_env_var=true",
             "-c", "sandbox_workspace_write.exclude_slash_tmp=true",
             "-c", "windows.sandbox='unelevated'",
             "--ignore-user-config", "--ignore-rules"],
            confined.Skip(start).Take(18));
        // Elsewhere the platform sandbox needs no Windows override.
        Assert.DoesNotContain(CodexNativeLaneRunner.WindowsSandboxOverride,
            CodexNativeLaneRunner.BuildStartInfo("codex", Turn(CodexNativeLaneRunner.ProviderId, null), null, talavine,
                ignoreUserConfigSupported: true, ignoreRulesSupported: true, windows: false).ArgumentList);
        Assert.DoesNotContain("--dangerously-bypass-approvals-and-sandbox", confined);
        Assert.DoesNotContain(confined, a => a.StartsWith("mcp_servers.", StringComparison.Ordinal));

        var forced = CodexNativeLaneRunner.BuildStartInfo("codex.exe", Turn(CodexNativeLaneRunner.ProviderId, NativeLaneContainment.WorkspaceConfined),
            null, new NativeAgentOptions(), ignoreUserConfigSupported: true, ignoreRulesSupported: true).ArgumentList.ToList();
        Assert.Equal(["-s", "workspace-write"], Pair(forced, "-s"));
        Assert.Contains("sandbox_workspace_write.network_access=false", forced);

        var swarmCode = CodexNativeLaneRunner.BuildStartInfo("codex.exe", Turn(CodexNativeLaneRunner.ProviderId, null), null, new NativeAgentOptions())
            .ArgumentList.ToList();
        Assert.Contains("--dangerously-bypass-approvals-and-sandbox", swarmCode);
        Assert.DoesNotContain("workspace-write", swarmCode);
    }

    [Fact]
    public void Gemini_cannot_be_confined_so_it_is_only_offered_unconfined()
    {
        var runner = new GeminiCliNativeLaneRunner(null!, null!, Options.Create(new CodingOptions()), NullLogger<GeminiCliNativeLaneRunner>.Instance);
        Assert.False(runner.SupportsContainment(NativeLaneContainment.WorkspaceConfined));
        Assert.True(runner.SupportsContainment(NativeLaneContainment.Bypass));
        Assert.True(((INativeLaneRunner)new ClaudeCodeNativeLaneRunner(null!, null!, Options.Create(new CodingOptions()), NullLogger<ClaudeCodeNativeLaneRunner>.Instance))
            .SupportsContainment(NativeLaneContainment.WorkspaceConfined));
        var swarmCode = GeminiCliNativeLaneRunner.BuildStartInfo("gemini.exe", Turn(GeminiCliNativeLaneRunner.ProviderId, null), null, "s", new NativeAgentOptions())
            .ArgumentList.ToList();
        Assert.Equal(["--approval-mode", "yolo"], Pair(swarmCode, "--approval-mode"));
    }

    [Fact]
    public void Capability_probe_reads_the_installed_cli_help_once()
    {
        var probes = 0;
        // A unique executable key and async-local seams: nothing here touches another test's state.
        var exe = "probe-claude-" + Guid.NewGuid().ToString("N") + ".exe";
        NativeCliCapabilities.ProbeOverride = (_, _) => { probes++; return "  --restricted   Restricted mode"; };
        try
        {
            Assert.True(NativeCliCapabilities.Supports(exe, "--restricted"));
            Assert.False(NativeCliCapabilities.Supports(exe, "--nonexistent"));
            Assert.Equal(1, probes);
        }
        finally
        {
            NativeCliCapabilities.ProbeOverride = null;
        }
    }

    [Fact]
    public void A_failed_capability_probe_is_retried_after_a_while_so_an_upgraded_cli_is_detected()
    {
        var now = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
        string? help = null; // not installed yet
        var probes = 0;
        var exe = "probe-upgrade-" + Guid.NewGuid().ToString("N") + ".exe";
        NativeCliCapabilities.UtcNowOverride = () => now;
        NativeCliCapabilities.ProbeOverride = (_, _) => { probes++; return help; };
        try
        {
            Assert.False(NativeCliCapabilities.Supports(exe, "--restricted"));
            help = "  --restricted   Restricted mode";
            Assert.False(NativeCliCapabilities.Supports(exe, "--restricted")); // failure still remembered
            Assert.Equal(1, probes);

            now += NativeCliCapabilities.FailedProbeRetryAfter;
            Assert.True(NativeCliCapabilities.Supports(exe, "--restricted"));
            Assert.Equal(2, probes);

            now += TimeSpan.FromDays(1); // a successful probe is kept
            Assert.True(NativeCliCapabilities.Supports(exe, "--restricted"));
            Assert.Equal(2, probes);
        }
        finally
        {
            NativeCliCapabilities.ProbeOverride = null;
            NativeCliCapabilities.UtcNowOverride = null;
        }
    }

    // ── native lanes only for the attended, admitted request ────

    private sealed class Lanes : ICodingLaneSource
    {
        public CodingLaneSet GetLanes() => new()
        {
            Lanes = [new("claude", ClaudeCodeNativeLaneRunner.ProviderId, "claude-code:default") { Priority = 0 }, new("api", "Api", "m") { Priority = 1 }],
        };
    }

    private sealed class Runner(List<AgentTurnRequest> seen) : IAgentTurnRunner
    {
        public async IAsyncEnumerable<TurnEvent> RunAsync(AgentTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            seen.Add(request);
            await Task.Yield();
            yield return new TurnCompleted("t", "done", TimeSpan.Zero);
        }
    }

    private sealed class Native(List<NativeLaneTurn> seen) : INativeLaneRunner
    {
        public bool Supports(CodingLane lane) => lane.IsNative;
        public async IAsyncEnumerable<TurnEvent> RunAsync(NativeLaneTurn turn, [EnumeratorCancellation] CancellationToken ct = default)
        {
            seen.Add(turn);
            await Task.Yield();
            yield return new TurnCompleted("n", "done", TimeSpan.Zero);
        }
    }

    private static async Task<(List<TurnEvent> Events, List<AgentTurnRequest> Api, List<NativeLaneTurn> Native)> Run(CodingTurnRequest request)
    {
        var api = new List<AgentTurnRequest>();
        var native = new List<NativeLaneTurn>();
        var services = new ServiceCollection();
        services.AddScoped<IAgentTurnRunner>(_ => new Runner(api));
        services.AddScoped<INativeLaneRunner>(_ => new Native(native));
        services.AddSingleton<ICodingLaneSource>(new Lanes());
        services.AddSingleton<ICodingWorkspaceResolver>(new TestOrchestrator.FixedWorkspace(new CodingWorkspace { RepositoryRoot = TestOrchestrator.Workspace }));
        var provider = services.BuildServiceProvider();
        var orchestrator = new CodingTurnOrchestrator(provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(new CodingOptions()),
            NullLogger<CodingTurnOrchestrator>.Instance);
        var events = new List<TurnEvent>();
        await foreach (var e in orchestrator.RunAsync(request)) events.Add(e);
        return (events, api, native);
    }

    [Fact]
    public async Task Attended_admitted_user_request_runs_the_native_lane_with_the_requested_containment()
    {
        var (_, api, native) = await Run(new CodingTurnRequest
        {
            ConversationId = "c", UserMessage = "fix", CommunicationScope = AgentCommunicationScope.UserRequested("chat"),
            NativeContainment = NativeLaneContainment.WorkspaceConfined,
        });
        Assert.Equal(NativeLaneContainment.WorkspaceConfined, Assert.Single(native).Containment);
        Assert.Empty(api);
    }

    [Theory]
    [InlineData(true, true)]   // unattended (host continuation, scheduled, worker, employee, sub-agent)
    [InlineData(false, false)] // the host does not admit native lanes for this profile
    public async Task Unattended_or_unadmitted_turns_fall_back_to_a_non_native_lane_with_a_notice(bool unattended, bool admitted)
    {
        var (events, api, native) = await Run(new CodingTurnRequest
        {
            ConversationId = "c", UserMessage = "fix", CommunicationScope = AgentCommunicationScope.UserRequested("chat"),
            UnattendedAgentTurn = unattended, AllowNativeLanes = admitted,
        });
        Assert.Empty(native);
        Assert.Equal("Api", Assert.Single(api).ProviderOverride);
        Assert.True(Assert.Single(api).UnattendedAgentTurn == unattended);
        Assert.Contains(events.OfType<CodingLaneSkipped>(), s => s.Lane.Name == "claude" && s.Reason == CodingTurnOrchestrator.NativeLaneNotAdmitted);
    }

    private sealed class UnconfinableNative(List<NativeLaneTurn> seen) : INativeLaneRunner
    {
        public bool Supports(CodingLane lane) => lane.IsNative;
        public bool SupportsContainment(NativeLaneContainment containment) => containment == NativeLaneContainment.Bypass;
        public async IAsyncEnumerable<TurnEvent> RunAsync(NativeLaneTurn turn, [EnumeratorCancellation] CancellationToken ct = default)
        {
            seen.Add(turn);
            await Task.Yield();
            yield return new TurnCompleted("n", "done", TimeSpan.Zero);
        }
    }

    [Fact]
    public async Task A_native_cli_that_cannot_be_confined_is_skipped_for_a_confined_request_only()
    {
        async Task<(List<TurnEvent> Events, List<NativeLaneTurn> Native)> RunWith(NativeLaneContainment containment)
        {
            var api = new List<AgentTurnRequest>();
            var native = new List<NativeLaneTurn>();
            var services = new ServiceCollection();
            services.AddScoped<IAgentTurnRunner>(_ => new Runner(api));
            services.AddScoped<INativeLaneRunner>(_ => new UnconfinableNative(native));
            services.AddSingleton<ICodingLaneSource>(new Lanes());
            services.AddSingleton<ICodingWorkspaceResolver>(new TestOrchestrator.FixedWorkspace(new CodingWorkspace { RepositoryRoot = TestOrchestrator.Workspace }));
            var provider = services.BuildServiceProvider();
            var orchestrator = new CodingTurnOrchestrator(provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(new CodingOptions()),
                NullLogger<CodingTurnOrchestrator>.Instance);
            var events = new List<TurnEvent>();
            await foreach (var e in orchestrator.RunAsync(new CodingTurnRequest
            {
                ConversationId = "c", UserMessage = "fix", CommunicationScope = AgentCommunicationScope.UserRequested("chat"), NativeContainment = containment,
            })) events.Add(e);
            return (events, native);
        }

        var confined = await RunWith(NativeLaneContainment.WorkspaceConfined);
        Assert.Empty(confined.Native);
        Assert.Contains(confined.Events.OfType<CodingLaneSkipped>(), s => s.Reason == CodingTurnOrchestrator.NativeLaneCannotBeConfined);

        var bypass = await RunWith(NativeLaneContainment.Bypass);
        Assert.Single(bypass.Native);
    }

    // ── a workspace turn is fenced to its workspace ──────────────

    [Fact]
    public void Workspace_turns_refuse_absolute_paths_outside_the_workspace_when_the_host_says_so_and_swarmcode_keeps_its_allowance()
    {
        var root = Directory.CreateTempSubdirectory("fence-");
        var workspace = Directory.CreateDirectory(Path.Combine(root.FullName, "ws")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(root.FullName, "other")).FullName;
        var outsideFile = Path.Combine(outside, "Talavine.db");
        var accessor = new AsyncLocalCurrentTurnContextAccessor();
        try
        {
            SwarmCore.Missions.SandboxPathResolver Resolver(bool allow) => new(Options.Create(new SwarmCore.Missions.SandboxOptions
            { Root = Path.Combine(root.FullName, "sandbox"), AllowAbsolutePathsOutsideTurnWorkspace = allow }));

            foreach (var unattended in new[] { false, true })
            {
                accessor.Current = UserTurn(unattended) with { WorkspaceRoot = workspace };
                var governed = Resolver(allow: false);
                Assert.Throws<UnauthorizedAccessException>(() => governed.Resolve(outsideFile));
                Assert.StartsWith(SwarmCore.Missions.SandboxPathResolver.ResolvePhysicalPath(workspace), governed.Resolve("src/file.cs"));
                Assert.NotNull(governed.Resolve(Path.Combine(workspace, "inside.txt")));
            }

            // SwarmCode (default options): a direct request in its repository may still name an absolute path.
            accessor.Current = UserTurn() with { WorkspaceRoot = workspace };
            Assert.NotNull(Resolver(allow: true).Resolve(outsideFile));

            // Outside any workspace turn the existing user-initiated rule is unchanged.
            accessor.Current = UserTurn();
            Assert.NotNull(Resolver(allow: false).Resolve(outsideFile));
        }
        finally
        {
            accessor.Current = null;
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Workspace_header_tells_the_model_where_paths_resolve_and_the_resolver_fallback_can_be_disabled()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICodingWorkspaceResolver>(new TestOrchestrator.FixedWorkspace(new CodingWorkspace { RepositoryRoot = TestOrchestrator.Workspace }));
        var provider = services.BuildServiceProvider();
        var withRoot = await new SwarmCore.Coding.Prompting.RepoInstructionsInjector(Options.Create(new CodingOptions()), provider.GetRequiredService<IServiceScopeFactory>())
            .BuildAsync(new ContextInjectionRequest("c", "x", Guid.NewGuid()) { WorkspaceRoot = TestOrchestrator.Workspace });
        Assert.Contains("use paths relative to this root", withRoot);
        Assert.Contains("is not the project", withRoot);

        var governed = new SwarmCore.Coding.Prompting.RepoInstructionsInjector(
            Options.Create(new CodingOptions { InstructionsFromWorkspaceResolver = false }), provider.GetRequiredService<IServiceScopeFactory>());
        Assert.Null(await governed.BuildAsync(new ContextInjectionRequest("c", "x", Guid.NewGuid())));
    }

    [Fact]
    public async Task Selected_route_starts_the_request_on_a_route_scoped_health_key()
    {
        var (events, api, _) = await Run(new CodingTurnRequest
        {
            ConversationId = "c", UserMessage = "fix", CommunicationScope = AgentCommunicationScope.UserRequested("chat"),
            RouteProvider = "OpenRouter", RouteModel = "z-ai/glm",
        });
        var selected = Assert.Single(events.OfType<CodingLaneSelected>()).Lane;
        Assert.Equal(CodingTurnOrchestrator.SelectedRouteLane, selected.Name);
        Assert.Equal("selected:c:OpenRouter/z-ai/glm", selected.HealthKey);
        Assert.Equal("OpenRouter", Assert.Single(api).ProviderOverride);
    }

    [Fact]
    public void Lane_health_follows_the_health_key_not_the_lane_position()
    {
        var tracker = new LaneHealthTracker();
        var first = new CodingLane("scout-1", "A", "a") { HealthKey = "route:A/a" };
        tracker.MarkUnavailable(first.HealthKeyOrName, "quota", TimeSpan.FromMinutes(5), "profile:x");
        var nextTime = new CodingLane("scout-1", "B", "b") { HealthKey = "route:B/b" };
        Assert.False(tracker.IsUnavailable(nextTime.HealthKeyOrName, out _, "profile:x"));
        Assert.True(tracker.IsUnavailable(new CodingLane("scout-2", "A", "a") { HealthKey = "route:A/a" }.HealthKeyOrName, out _, "profile:x"));
    }

    // ── coding tasks: direct user turn, admission, confinement ──

    private sealed class Admission(bool allow) : ICodingNativeLaneAdmission
    {
        public ValueTask<bool> AdmitsAsync(CurrentTurnContext turn, CancellationToken ct = default) => ValueTask.FromResult(allow);
    }

    private sealed class Conversations : IConversationStore
    {
        public Task<ChatConversationInfo> GetOrCreateAsync(string conversationId, string title, Guid? profileId = null, CancellationToken ct = default)
            => Task.FromResult(new ChatConversationInfo(conversationId, title, profileId, DateTime.UtcNow, DateTime.UtcNow));
        public Task<ChatConversationInfo> CreateAsync(Guid? profileId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChatConversationInfo?> GetAsync(string conversationId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ChatConversationInfo>> ListRecentAsync(int take = 25, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetLatestIdAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ChatTurnInfo>> LoadTurnsAsync(string conversationId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChatTurnInfo> AppendUserTurnAsync(string conversationId, string userText, IReadOnlyList<TurnImageAttachment>? images = null,
            IReadOnlyList<TurnFileAttachment>? files = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CompleteAssistantTurnAsync(Guid turnId, string assistantText, Guid? profileId, TimeSpan duration, string? reasoningContent = null,
            CancellationToken ct = default, string? metadataJson = null) => throw new NotSupportedException();
        public Task DeleteAsync(string conversationId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RenameAsync(string conversationId, string title, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetPinnedAsync(string conversationId, bool isPinned, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateStateAsync(string conversationId, IReadOnlyDictionary<string, object> stateUpdates, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, object>> GetStateAsync(string conversationId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static async Task<(CodingTaskProcessResult Result, List<NativeLaneTurn> Seen)> RunTask(CurrentTurnContext? turn, bool? admit = null)
    {
        var seen = new List<NativeLaneTurn>();
        var services = new ServiceCollection();
        services.AddSingleton<IConversationStore>(new Conversations());
        services.AddScoped<INativeLaneRunner>(_ => new Native(seen));
        if (admit is { } allow) services.AddSingleton<ICodingNativeLaneAdmission>(new Admission(allow));
        var provider = services.BuildServiceProvider();
        var accessor = new AsyncLocalCurrentTurnContextAccessor { Current = turn };
        try
        {
            var runner = new NativeLaneCodingTaskAgentRunner(provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(new CodingOptions()),
                NullLogger<NativeLaneCodingTaskAgentRunner>.Instance, accessor);
            return (await runner.RunAsync(new CodingTaskAgentRun("claude", "fix", Path.GetTempPath(), "t", "agents/fix/t", TimeSpan.FromMinutes(1))), seen);
        }
        finally
        {
            accessor.Current = null;
        }
    }

    private static CurrentTurnContext UserTurn(bool unattended = false, string? actingAgent = null) =>
        new("chat", null, Guid.NewGuid(), null, null, CommunicationScopeKind: AgentCommunicationScopeKind.TrustedProfileWide,
            CommunicationScopeAuthority: "user", CommunicationScopeUserInitiated: true, ActingAgentId: actingAgent, UnattendedAgentTurn: unattended);

    [Fact]
    public async Task Coding_task_native_agent_runs_confined_for_an_admitted_direct_user_turn()
    {
        var (result, seen) = await RunTask(UserTurn(), admit: true);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(NativeLaneContainment.WorkspaceConfined, Assert.Single(seen).Containment);
    }

    public static TheoryData<string> ExcludedTurns => ["none", "unattended", "employee", "repair", "repair-grant", "background", "not-admitted"];

    [Theory]
    [MemberData(nameof(ExcludedTurns))]
    public async Task Coding_task_native_agent_never_runs_outside_a_direct_admitted_user_turn(string kind)
    {
        var turn = kind switch
        {
            "none" => null,
            "unattended" => UserTurn(unattended: true),
            "employee" => UserTurn(actingAgent: "employee-1"),
            "repair" => UserTurn() with { RepairAuthority = AgentRepairAuthority.SystemRepair("test", "test repair") },
            "repair-grant" => UserTurn() with { CodingRepairGrant = CodingRepairGrant.ForConfiguredRepository(
                AgentRepairAuthority.SystemRepair("test", "test repair"), Path.Combine(Path.GetTempPath(), "repo"), ["code.edit_file"]) },
            "background" => new CurrentTurnContext("chat", null, Guid.NewGuid(), null, null),
            _ => UserTurn(),
        };
        var (result, seen) = await RunTask(turn, admit: kind != "not-admitted");
        Assert.Empty(seen);
        Assert.NotEqual(0, result.ExitCode);
    }

    // ── workspace folder policy ─────────────────────────────────

    public static TheoryData<string, string> RefusedFolders()
    {
        var data = new TheoryData<string, string>
        {
            { "", "required" },
            { "relative\\repo", "full path" },
            { @"\\server\share\repo", "UNC" },
            { "//server/share/repo", "UNC" },
            { Path.Combine(Path.GetTempPath(), "x", "..", "y"), "segments" },
            { Path.GetPathRoot(Path.GetTempPath())!, "root" },
            { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "home" },
            { Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N")), "does not exist" },
        };
        if (OperatingSystem.IsWindows())
        {
            data.Add(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System");
            data.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32"), "System");
            data.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "System");
            data.Add(Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))!, "home");
        }
        else if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
        {
            foreach (var system in new[] { "/System", "/Library", "/Applications", "/usr/bin", "/etc" })
                if (Directory.Exists(system)) data.Add(system, "System");
        }
        else
        {
            foreach (var system in new[] { "/usr/bin", "/etc", "/var/lib" })
                if (Directory.Exists(system)) data.Add(system, "System");
        }
        return data;
    }

    [Fact]
    public void Workspace_policy_names_this_platforms_system_folders_and_keeps_the_windows_message()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("System folders (Windows, Program Files) cannot be coding workspaces.", CodingWorkspacePolicy.SystemFolderRefusal);
            Assert.Equal(CodingWorkspacePolicy.SystemFolderRefusal,
                CodingWorkspacePolicy.ValidateShape(Environment.GetFolderPath(Environment.SpecialFolder.Windows), []).Error);
            return;
        }
        Assert.DoesNotContain("Windows", CodingWorkspacePolicy.SystemFolderRefusal);
        Assert.Equal(CodingWorkspacePolicy.SystemFolderRefusal, CodingWorkspacePolicy.ValidateShape("/usr/bin", []).Error);
        // The temporary folder (on macOS a link into /private) is ordinary user space, not a system folder.
        var temp = Directory.CreateTempSubdirectory("coding-policy-temp-");
        try { Assert.True(CodingWorkspacePolicy.ValidateShape(temp.FullName, []).IsValid, CodingWorkspacePolicy.ValidateShape(temp.FullName, []).Error); }
        finally { temp.Delete(recursive: true); }
    }

    [Theory]
    [MemberData(nameof(RefusedFolders))]
    public async Task Workspace_policy_refuses_unsafe_folder_shapes(string path, string reason)
    {
        var check = await CodingWorkspacePolicy.ValidateAsync(path, []);
        Assert.False(check.IsValid);
        Assert.Contains(reason, check.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Workspace_policy_accepts_a_git_repository_and_refuses_protected_data_or_non_git_folders()
    {
        var root = Directory.CreateTempSubdirectory("coding-policy-");
        try
        {
            var repo = Directory.CreateDirectory(Path.Combine(root.FullName, "repo")).FullName;
            var plain = Directory.CreateDirectory(Path.Combine(root.FullName, "plain")).FullName;
            var data = Directory.CreateDirectory(Path.Combine(root.FullName, "talavine-data")).FullName;
            Git(repo, "init", "-q");

            var ok = await CodingWorkspacePolicy.ValidateAsync(repo, [data]);
            Assert.True(ok.IsValid, ok.Error);
            Assert.Equal(Path.TrimEndingDirectorySeparator(SwarmCore.Missions.SandboxPathResolver.ResolvePhysicalPath(repo)), ok.NormalizedPath);

            Assert.Contains("not a git work tree", (await CodingWorkspacePolicy.ValidateAsync(plain, [data])).Error);
            Assert.Contains("data", (await CodingWorkspacePolicy.ValidateAsync(data, [data])).Error);
            // A folder containing protected data (the profile root's parent) is refused too.
            Assert.Contains("data", (await CodingWorkspacePolicy.ValidateAsync(root.FullName, [data])).Error);
            // The install folder: refused when inside it, but a repository may contain it (a dev build).
            Assert.Contains("application's own folder", CodingWorkspacePolicy.ValidateShape(repo, [], [root.FullName]).Error);
            Assert.True(CodingWorkspacePolicy.ValidateShape(root.FullName, [], [repo]).IsValid);
        }
        finally
        {
            foreach (var file in root.EnumerateFiles("*", SearchOption.AllDirectories)) file.Attributes = FileAttributes.Normal;
            root.Delete(recursive: true);
        }
    }

    private static void Git(string dir, params string[] args) => SwarmCore.Testing.TestGit.Run(dir, args);

    [SwarmCore.Testing.WindowsFact]
    public async Task Workspace_policy_sees_through_a_subst_drive_onto_protected_data()
    {
        // Decided before anything is created, so this early exit leaves nothing behind.
        var letter = "PQRSTUVWXYZ".Select(c => c + ":").FirstOrDefault(d => !Directory.Exists(d + "\\"));
        if (letter is null) return; // no free drive letter: nothing to test here
        var root = Directory.CreateTempSubdirectory("coding-subst-");
        var data = Directory.CreateDirectory(Path.Combine(root.FullName, "profile-data")).FullName;
        var repo = Directory.CreateDirectory(Path.Combine(data, "subst-repo")).FullName;
        Git(repo, "init", "-q");
        if (!Subst(letter, data)) { root.Delete(true); return; } // subst unavailable: skip cleanly
        try
        {
            var aliased = letter + "\\subst-repo";
            Assert.True(Directory.Exists(aliased));
            var check = await CodingWorkspacePolicy.ValidateAsync(aliased, [data]);
            Assert.False(check.IsValid);
            Assert.Contains("data", check.Error);
            // The same folder through its real path is refused too, and an unrelated repo is accepted.
            Assert.False((await CodingWorkspacePolicy.ValidateAsync(repo, [data])).IsValid);
        }
        finally
        {
            Subst(letter, null);
            foreach (var file in root.EnumerateFiles("*", SearchOption.AllDirectories)) file.Attributes = FileAttributes.Normal;
            root.Delete(recursive: true);
        }
    }

    private static bool Subst(string drive, string? target)
    {
        try
        {
            var psi = new ProcessStartInfo("subst") { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, UseShellExecute = false };
            psi.ArgumentList.Add(drive);
            psi.ArgumentList.Add(target ?? "/D");
            using var p = Process.Start(psi)!;
            p.WaitForExit(10_000);
            return p.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    // ── strict shared detection and no-overwrite storage ────────

    [Theory]
    [InlineData("{ \"partitionKey\": \"orders-2024\" }")]
    [InlineData("{ \"cacheKey\": \"user-profile-v2\" }")]
    [InlineData("token: 3f9a8b7c6d5e4f30")] // gitleaks:allow -- synthetic test fixture, never a live credential
    [InlineData("the token usage report is due")]
    [InlineData("password = form[\"password\"]")]
    public void Strict_detector_ignores_ordinary_code_and_identifiers(string text)
        => Assert.Empty(CredentialShapeDetector.Detect(text));

    [Theory]
    [InlineData("my github token is ghp_abcdefghijklmnopqrstuvwxyz0123456789", DetectedCredentialSource.KnownPrefix)]
    [InlineData("db password: Tr0ub4dor&3x", DetectedCredentialSource.Labeled)]
    [InlineData("\"StripeApiKey\": \"rk_live_51HxQ2aBcDeF9gHiJ\"", DetectedCredentialSource.Labeled)]
    public void Strict_detector_finds_known_prefixes_and_labeled_credentials(string text, DetectedCredentialSource source)
        => Assert.Equal(source, Assert.Single(CredentialShapeDetector.Detect(text)).Source);

    private sealed class Vault : ISecretVault
    {
        public readonly Dictionary<string, string> Values = new(StringComparer.OrdinalIgnoreCase);
        public Task<SecretEntry> SetAsync(SecretWriteRequest request, CancellationToken ct = default)
        {
            Values[SecretNames.Normalize(request.Name)] = request.Value;
            return Task.FromResult(new SecretEntry(SecretNames.Normalize(request.Name), request.Kind, null, null, false, DateTime.UtcNow, DateTime.UtcNow, null, true));
        }
        public Task<string?> GetValueAsync(string name, CancellationToken ct = default) => Task.FromResult(Values.GetValueOrDefault(SecretNames.Normalize(name)));
        public Task<SecretLogin?> GetLoginAsync(string name, CancellationToken ct = default) => Task.FromResult<SecretLogin?>(null);
        public Task<SecretEntry?> GetEntryAsync(string name, CancellationToken ct = default) => Task.FromResult<SecretEntry?>(null);
        public Task<bool> HasAsync(string name, CancellationToken ct = default) => Task.FromResult(Values.ContainsKey(SecretNames.Normalize(name)));
        public Task<bool> DeleteAsync(string name, CancellationToken ct = default) => Task.FromResult(Values.Remove(name));
        public Task<IReadOnlyList<SecretEntry>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SecretEntry>>([]);
    }

    [Fact]
    public async Task Governed_scrubber_never_overwrites_an_existing_vault_entry()
    {
        var vault = new Vault();
        vault.Values["github-token"] = "ghp_existingvalueexistingvalue0123456789"; // gitleaks:allow -- synthetic test fixture, never a live credential
        const string typed = "ghp_newvaluenewvaluenewvalue0123456789ab"; // gitleaks:allow -- synthetic test fixture, never a live credential

        var result = await SecretMessageScrubber.ScrubAsync(vault, $"use {typed} to push");

        Assert.Equal("ghp_existingvalueexistingvalue0123456789", vault.Values["github-token"]); // gitleaks:allow -- synthetic test fixture, never a live credential
        Assert.Equal("github-token-2", Assert.Single(result.StoredNames));
        Assert.Equal(typed, vault.Values["github-token-2"]);
        Assert.Contains("{{secret:github-token-2}}", result.Text);

        // The same value again reuses its entry rather than creating another.
        var again = await SecretMessageScrubber.ScrubAsync(vault, $"again {typed}");
        Assert.Equal("github-token-2", Assert.Single(again.StoredNames));
        Assert.Equal(2, vault.Values.Count);
    }

    // ── external MCP servers: mounted only unconfined ──────────

    private static McpServerLaunch ExternalServer() => new() { ServerName = "talavine", Command = "talavine-mcp.exe", Arguments = ["--stdio"] };
    private static McpServerLaunch BridgeServer() => new() { Command = "swarmcode.exe", Arguments = ["--mcp-server"] };

    [Fact]
    public void Confined_mode_with_external_servers_mounts_no_mcp_config_and_no_mcp_server_overrides()
    {
        var turn = Turn(ClaudeCodeNativeLaneRunner.ProviderId, NativeLaneContainment.WorkspaceConfined);
        Assert.Null(ClaudeCodeNativeLaneRunner.WriteMcpConfigFor(NativeLaneContainment.WorkspaceConfined, BridgeServer(), turn.RepositoryRoot, [ExternalServer()]));
        var claude = ClaudeCodeNativeLaneRunner.BuildStartInfo("claude.exe", turn, null, null, NativeLaneContainment.WorkspaceConfined, restrictedSupported: true)
            .ArgumentList.ToList();
        Assert.DoesNotContain("--mcp-config", claude);
        Assert.Contains("--strict-mcp-config", claude);

        var native = new NativeAgentOptions
        {
            DefaultContainment = NativeLaneContainment.WorkspaceConfined,
            McpServer = BridgeServer(),
            ExternalMcpServers = [ExternalServer()],
        };
        var codex = CodexNativeLaneRunner.BuildStartInfo("codex.exe", Turn(CodexNativeLaneRunner.ProviderId, null), null, native,
            ignoreUserConfigSupported: true, ignoreRulesSupported: true).ArgumentList.ToList();
        Assert.Contains("mcp_servers={}", codex);
        Assert.DoesNotContain(codex, a => a.StartsWith("mcp_servers.", StringComparison.Ordinal));
    }

    [Fact]
    public void Bypass_mode_still_mounts_the_bridge_and_external_servers_with_one_mcp_config()
    {
        var turn = Turn(ClaudeCodeNativeLaneRunner.ProviderId, NativeLaneContainment.Bypass);
        var config = ClaudeCodeNativeLaneRunner.WriteMcpConfigFor(NativeLaneContainment.Bypass, BridgeServer(), turn.RepositoryRoot, [ExternalServer()]);
        Assert.NotNull(config);
        try
        {
            var json = File.ReadAllText(config!);
            Assert.Contains("\"talavine\"", json);
            Assert.Contains("\"swarmcode\"", json);
            var claude = ClaudeCodeNativeLaneRunner.BuildStartInfo("claude.exe", turn, null, config, NativeLaneContainment.Bypass).ArgumentList.ToList();
            Assert.Single(claude, a => a == "--mcp-config");
            Assert.Equal(["--mcp-config", config!], Pair(claude, "--mcp-config"));
            Assert.DoesNotContain("--strict-mcp-config", claude);
        }
        finally
        {
            File.Delete(config!);
        }

        var native = new NativeAgentOptions { McpServer = BridgeServer(), ExternalMcpServers = [ExternalServer()] };
        var codex = CodexNativeLaneRunner.BuildStartInfo("codex.exe", Turn(CodexNativeLaneRunner.ProviderId, null), null, native).ArgumentList.ToList();
        Assert.Contains(codex, a => a.StartsWith("mcp_servers.talavine.", StringComparison.Ordinal));
        Assert.Contains(codex, a => a.StartsWith("mcp_servers.swarmcode.", StringComparison.Ordinal));
        Assert.DoesNotContain("mcp_servers={}", codex);
    }
}
