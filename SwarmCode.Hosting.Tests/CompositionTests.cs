using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Runtime;
using SwarmCore.Agent.ToolUseEngine;
using SwarmCore.Coding.Routing;
using SwarmCore.LlmRouting.Configuration;

namespace SwarmCode.Hosting.Tests;

public class CompositionTests : TempDirTest
{
    private string Repo()
    {
        var p = Path.Combine(Tmp, "repo");
        Directory.CreateDirectory(p);
        return p;
    }

    private SwarmCodeHostOptions Options(Action<IServiceCollection>? configure = null) => new()
    {
        RepositoryRoot = Repo(),
        DataPaths = Paths,
        ConfigureServices = services =>
        {
            FixedLanes.Register(services);
            configure?.Invoke(services);
        },
    };

    private ServiceProvider Provider(SwarmCodeHostOptions options)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSwarmCodeHost(options);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    [Fact]
    public void AddSwarmCodeHost_ResolvesCoreServices()
    {
        using var sp = Provider(Options());
        Assert.NotNull(sp.GetRequiredService<ICodingTurnOrchestrator>());
        Assert.IsType<FileConversationStore>(sp.GetRequiredService<IConversationStore>());
        Assert.IsType<FileAgentSessionStore>(sp.GetRequiredService<IAgentSessionStore>());
        var broker = Assert.IsType<UiPermissionBroker>(sp.GetRequiredService<IPermissionBroker>());
        Assert.True(broker.AutoApprove);
        Assert.Same(Paths, sp.GetRequiredService<SwarmCodeDataPaths>());
        Assert.NotNull(sp.GetRequiredService<AppPreferencesStore>());
        Assert.NotNull(sp.GetRequiredService<UserSettingsFile>());
    }

    [Fact]
    public void Lanes_AreSeeded_WithSubscriptionCliLanes()
    {
        using var sp = Provider(Options());
        var lanes = sp.GetRequiredService<IOptions<CodingLaneOptions>>().Value.Lanes;
        Assert.Equal(FixedLanes.Names, lanes.OrderBy(l => l.Priority).Select(l => l.Name));
        Assert.Equal(lanes.Count, sp.GetRequiredService<ICodingTurnOrchestrator>().Lanes.Count);
    }

    [Fact]
    public void SandboxRoot_EqualsRepositoryRoot()
    {
        var options = Options();
        using var sp = Provider(options);
        var root = sp.GetRequiredService<IOptions<SwarmCore.Missions.SandboxOptions>>().Value.Root;
        Assert.Equal(Path.GetFullPath(options.RepositoryRoot), Path.GetFullPath(root));
    }

    [Fact]
    public async Task SwarmCodeHost_KeepsSingleRepositoryPolicy_OnTheHostNeutralHarness()
    {
        var options = Options();
        using var sp = Provider(options);
        var workspace = await sp.GetRequiredService<SwarmCore.Coding.ICodingWorkspaceResolver>().ResolveAsync("any-conversation");
        Assert.Equal(Path.GetFullPath(options.RepositoryRoot), workspace!.FullRoot);
        // Phase 0 decorates the harness resolver with the external MCP projection; the single-repository
        // policy still comes from the coding resolver underneath.
        // Hooks wrap the outermost resolver and guard (inert without configured hooks).
        var hooked = Assert.IsType<SwarmCore.Coding.Extensions.HookedProfileResolver>(sp.GetRequiredService<IProfileResolver>());
        var resolver = Assert.IsType<SwarmCode.Hosting.Mcp.ExternalMcpProfileResolver>(hooked.Inner);
        Assert.IsType<SwarmCore.Coding.CodingProfileResolver>(resolver.Inner);
        Assert.IsType<SwarmCore.Coding.Prompting.CodingInjectorListProvider>(sp.GetRequiredService<IInjectorListProvider>());
        // The person's permission policy runs before the repeated-call guard, so a deny rule wins.
        // Hooks wrap the guard chain; inside it the person's permission policy runs before the repeated-call guard, so a deny rule wins.
        var hookedGuard = Assert.IsType<SwarmCore.Coding.Extensions.HookedToolInvocationGuard>(sp.GetRequiredService<IToolInvocationGuard>());
        var guard = Assert.IsType<CompositeToolInvocationGuard>(hookedGuard.Inner);
        Assert.Collection(guard.Guards, g => Assert.IsType<SwarmCore.Coding.Permissions.CodingPermissionGuard>(g), g => Assert.IsType<RepeatedToolCallGuard>(g));
        Assert.IsType<SessionCodingWorkspaceResolver>(sp.GetRequiredService<SwarmCore.Coding.ICodingWorkspaceResolver>());
        Assert.IsType<SwarmCode.Hosting.Extensions.FileCodingHookApprovalStore>(sp.GetRequiredService<SwarmCore.Coding.Extensions.ICodingHookApprovalStore>());
        var coding = sp.GetRequiredService<IOptions<SwarmCore.Coding.CodingOptions>>().Value;
        Assert.Contains(SwarmCore.Coding.Extensions.CodingSkillsInjector.InjectorName, coding.AdditionalInjectorNames);
        Assert.Equal(Paths.RootDirectory, coding.UserExtensionsDirectory);
        Assert.IsType<OllamaCodingLaneSource>(sp.GetRequiredService<ICodingLaneSource>());
    }

    [Fact]
    public void TurnPolicy_DirectRequestIsUserRequested_ModeTravelsAsPolicy()
    {
        var direct = SwarmCodeTurnPolicy.Apply(new CodingTurnRequest { ConversationId = "c", UserMessage = "m" }, SwarmCore.Coding.Permissions.CodingPermissionPolicy.AutoNoRules, "swarmcode-cli");
        Assert.True(direct.CommunicationScope!.UserInitiated);
        Assert.Equal("swarmcode-cli", direct.CommunicationScope.Authority);
        Assert.NotNull(direct.ValidateWorkingContextSourceCallsAsync);
        Assert.False(direct.Permissions!.Restricts);
        var ask = SwarmCodeTurnPolicy.Apply(new CodingTurnRequest { ConversationId = "c", UserMessage = "m" },
            SwarmCore.Coding.Permissions.CodingPermissionPolicy.From(SwarmCore.Coding.Permissions.CodingPermissionMode.Ask, null), "swarmcode-app");
        // Asking narrows through the guard and broker; it does not drop the person's authority.
        Assert.True(ask.CommunicationScope!.UserInitiated);
        Assert.True(ask.Permissions!.Restricts);
    }

    [Fact]
    public void TurnPolicy_RefusalsHandOffAndTheInnerRouterStaysPinned()
    {
        // SwarmCode's purpose: a model's decline sends the request to the next lane; only the host switches routes.
        var request = SwarmCodeTurnPolicy.Apply(new CodingTurnRequest { ConversationId = "c", UserMessage = "Write the parser." },
            SwarmCore.Coding.Permissions.CodingPermissionPolicy.AutoNoRules, "test");
        Assert.False(request.StopOnRefusal);
        Assert.True(request.DisableRouteFallback);
        Assert.Equal("Write the parser.", request.UserMessage);
        Assert.Null(request.AuthenticatedOriginalUserMessage);
    }

    [Fact]
    public void TurnPolicy_FramesAReviewRequestAndKeepsThePersonsWordsAuthenticated()
    {
        const string message = "Please review this caching design and tell me what is wrong with it.";
        var request = SwarmCodeTurnPolicy.Apply(new CodingTurnRequest { ConversationId = "c", UserMessage = message },
            SwarmCore.Coding.Permissions.CodingPermissionPolicy.AutoNoRules, "test");
        Assert.StartsWith("[Review requested]", request.UserMessage);
        Assert.EndsWith(message, request.UserMessage);
        Assert.Equal(message, request.AuthenticatedOriginalUserMessage);
        Assert.False(request.RequireWorkspaceChange);
        // Continuations (hand-offs, agent hand-backs) and plan turns are not re-framed.
        var continuation = SwarmCodeTurnPolicy.Apply(new CodingTurnRequest { ConversationId = "c", UserMessage = message, IsContinuation = true },
            SwarmCore.Coding.Permissions.CodingPermissionPolicy.AutoNoRules, "test");
        Assert.Equal(message, continuation.UserMessage);
        var plan = SwarmCodeTurnPolicy.Apply(new CodingTurnRequest { ConversationId = "c", UserMessage = message, PlanMode = true },
            SwarmCore.Coding.Permissions.CodingPermissionPolicy.AutoNoRules, "test");
        Assert.Equal(message, plan.UserMessage);
    }

    [Theory]
    [InlineData("Can you implement those changes for me?", true)]
    [InlineData("Please fix the failing tests.", true)]
    [InlineData("Make code changes in the app.", true)]
    [InlineData("Explain how to implement those changes.", false)]
    [InlineData("Show me an example of code to add.", false)]
    [InlineData("What does this code do?", false)]
    public void TurnPolicy_RequiresWriteEvidenceOnlyForDirectEditRequests(string message, bool expected)
    {
        var request = SwarmCodeTurnPolicy.Apply(new CodingTurnRequest { ConversationId = "c", UserMessage = message }, SwarmCore.Coding.Permissions.CodingPermissionPolicy.AutoNoRules, "test");
        Assert.Equal(expected, request.RequireWorkspaceChange);
    }

    [Fact]
    public void ConfigureServices_PreRegistration_Wins()
    {
        var mine = new WorkspaceSessionCatalog(Paths);
        var broker = new UiPermissionBroker { AutoApprove = false };
        using var sp = Provider(Options(s =>
        {
            s.AddSingleton(mine);
            s.AddSingleton<IPermissionBroker>(broker);
        }));
        Assert.Same(mine, sp.GetRequiredService<WorkspaceSessionCatalog>());
        Assert.Same(broker, sp.GetRequiredService<IPermissionBroker>());
    }

    [Fact]
    public void PermissionBrokerFactory_IsUsed()
    {
        var custom = new UiPermissionBroker { AutoApprove = false };
        var options = new SwarmCodeHostOptions { RepositoryRoot = Repo(), DataPaths = Paths, PermissionBroker = _ => custom };
        using var sp = Provider(options);
        Assert.Same(custom, sp.GetRequiredService<IPermissionBroker>());
    }

    [Fact]
    public void BuildConfiguration_ReadsUserSettings_AndOverrides()
    {
        new UserSettingsFile(Paths).WriteValue("Coding", "Foo", "bar");
        var cfg = SwarmCodeHostComposer.BuildConfiguration(Paths, new Dictionary<string, string?> { ["Coding:Other"] = "z" });
        Assert.Equal("bar", cfg["Coding:Foo"]);
        Assert.Equal("z", cfg["Coding:Other"]);
    }
}

public class WorkspaceHostTests : TempDirTest
{
    private string Repo(string name = "repo")
    {
        var p = Path.Combine(Tmp, name);
        Directory.CreateDirectory(p);
        return p;
    }

    private SwarmCodeHostOptions Options(string root) => new() { RepositoryRoot = root, DataPaths = Paths, ConfigureServices = FixedLanes.Register };

    [Fact]
    public async Task CreateAsync_StartsHostedServices_AndRegistersCodingTools()
    {
        await using var host = await WorkspaceHost.CreateAsync(Options(Repo()));
        Assert.Equal(Path.GetFullPath(Repo()), host.RepositoryRoot);
        var catalog = host.Services.GetRequiredService<ICapabilityCatalog>();
        Assert.NotNull(catalog.Get("code.edit_file"));
        Assert.NotEmpty(host.Lanes);
        Assert.Contains(host.LaneOptions.Lanes, l => l.Name == "claude");
        Assert.Equal(Path.GetFullPath(Repo()), Path.GetFullPath(host.CodingOptions.RepositoryRoot));
    }

    [Fact]
    public async Task OpenSession_ReturnsRunner()
    {
        await using var host = await WorkspaceHost.CreateAsync(Options(Repo()));
        using var runner = host.OpenSession("abc");
        Assert.Equal("abc", runner.ConversationId);
        Assert.False(runner.IsRunning);
    }

    [Fact]
    public async Task CreateAsync_MissingDirectory_Throws()
        => await Assert.ThrowsAsync<DirectoryNotFoundException>(() => WorkspaceHost.CreateAsync(Options(Path.Combine(Tmp, "nope"))));

    [Fact]
    public async Task Manager_OpenTwice_DisposesPrevious()
    {
        await using var manager = new WorkspaceHostManager(Paths, root => Options(root));
        var changed = 0;
        manager.Changed += () => changed++;
        var first = await manager.OpenAsync(Repo("a"));
        Assert.Same(first, manager.Current);
        var second = await manager.OpenAsync(Repo("b"));
        Assert.Same(second, manager.Current);
        Assert.NotSame(first, second);
        Assert.Equal(Path.GetFullPath(Repo("b")), second.RepositoryRoot);
        Assert.Equal(2, changed);
        // The previous host's provider is disposed.
        Assert.Throws<ObjectDisposedException>(() => first.Services.GetRequiredService<ICodingTurnOrchestrator>());
        await manager.CloseAsync();
        Assert.Null(manager.Current);
    }

    [Fact]
    public async Task Manager_OpenFailure_KeepsCurrent()
    {
        await using var manager = new WorkspaceHostManager(Paths, root => Options(root));
        var first = await manager.OpenAsync(Repo("a"));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => manager.OpenAsync(Path.Combine(Tmp, "missing")));
        Assert.Same(first, manager.Current);
    }
}

/// <summary>
/// Composition tests run against a fixed lane set: every subscription CLI counts as installed and no BYOK key exists,
/// whatever this machine has. Registered before the host's own seeder, which then leaves the populated lanes alone.
/// Machine discovery itself is covered by the seeder tests.
/// </summary>
internal static class FixedLanes
{
    public static readonly string[] Names = ["claude", "codex", "gemini-cli", "claude-tools", "codex-tools"];

    public static void Register(IServiceCollection services)
        => services.AddSingleton<IPostConfigureOptions<CodingLaneOptions>>(
            new DefaultLaneSeeder(new NoApiKeys(), new LlmProvidersOptions(), _ => true));

    private sealed class NoApiKeys : ILlmApiKeyProvider
    {
        public Task<string?> GetApiKeyAsync(string providerName) => Task.FromResult<string?>(null);
        public Task SetApiKeyAsync(string providerName, string apiKey) => Task.CompletedTask;
        public Task<bool> HasApiKeyAsync(string providerName) => Task.FromResult(false);
    }
}
