using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SwarmCore.Agent;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;
using SwarmCore.Agent.ToolUseEngine;
using SwarmCore.Coding.Prompting;
using SwarmCore.Coding.Routing;
using SwarmCore.Coding.Tools;
using SwarmCore.Missions;
using Xunit;

namespace SwarmCore.Coding.Tests;

/// <summary>
/// The harness composes into a host that owns its profiles, injector lists, guard, and
/// workspaces (Talavine's shape) without overriding any of them, in either registration order.
/// </summary>
public sealed class HarnessCompositionTests
{
    private sealed class HostProfileResolver : IProfileResolver
    {
        public Task<ResolvedProfile> ResolveAsync(string conversationId, Guid? explicitProfileId = null, CancellationToken ct = default)
            => Task.FromResult(new ResolvedProfile(new AgentProfileSnapshot { Id = Guid.NewGuid(), Name = "host", SystemPrompt = "host", EnabledToolIds = [] }, []));
    }

    private sealed class HostInjectors : IInjectorListProvider
    {
        public IReadOnlyList<string> GetForProfile(Guid profileId) => ["host-injector"];
    }

    private sealed class HostGuard : IToolInvocationGuard
    {
        public Task<ToolGuardDecision> InspectAsync(ToolInvocationRequest request, CancellationToken ct = default)
            => Task.FromResult(ToolGuardDecision.Allow());
    }

    private sealed class HostWorkspaces : ICodingWorkspaceResolver
    {
        public Task<CodingWorkspace?> ResolveAsync(string conversationId, CancellationToken ct = default)
            => Task.FromResult<CodingWorkspace?>(null);
    }

    private sealed class HostLanes : ICodingLaneSource
    {
        public CodingLaneSet GetLanes() => new() { Lanes = [new("scout", "HostRouter", "recommended")] };
    }

    private static readonly Type[] HostOwned =
    [
        typeof(IProfileResolver), typeof(IInjectorListProvider), typeof(IToolInvocationGuard),
        typeof(ICodingWorkspaceResolver), typeof(IPermissionBroker), typeof(IConversationStore),
        typeof(IAgentToolProgressBroker),
    ];

    /// <summary>The host's own instances, so the test can assert the resolved services are exactly these.</summary>
    private sealed class HostInstances
    {
        public IPermissionBroker Broker { get; } = Unimplemented<IPermissionBroker>();
        public IConversationStore Store { get; } = Unimplemented<IConversationStore>();
        public IAgentToolProgressBroker Progress { get; } = new InMemoryAgentToolProgressBroker();
    }

    /// <param name="harnessAlreadyAdded">
    /// The harness calls <c>AddSwarmCoreAgent</c>, which TryAdds a default <see cref="IPermissionBroker"/>.
    /// The real contract: a host registers its broker before the harness (TryAdd is then enough) or uses
    /// <c>Replace</c> afterwards. Host-only seams the harness never registers (profile resolver, injector
    /// list, guard, workspace resolver, conversation store, progress broker) win with TryAdd in either order.
    /// </param>
    private static void AddHost(IServiceCollection services, HostInstances host, bool harnessAlreadyAdded)
    {
        services.AddLogging();
        services.AddHttpClient();
        if (harnessAlreadyAdded) services.Replace(ServiceDescriptor.Singleton(host.Broker));
        else services.TryAddSingleton(host.Broker);
        services.AddSwarmCoreAgent();
        services.AddSwarmCoreMissionExecution();
        // Host infrastructure the agent runtime, mission primitives and native lanes consume (never called here).
        services.AddSingleton(Unimplemented<SwarmCore.Abstractions.LlmRouting.ILlmRouter>());
        services.AddSingleton(Unimplemented<SwarmCore.Abstractions.LlmRouting.ILlmProviderRegistry>());
        services.AddSingleton(Unimplemented<SwarmCore.Agent.Continual.IMissionStore>());
        services.AddSingleton(Unimplemented<SwarmCore.Agent.Continual.IContinualObjectiveTextSanitizer>());
        services.AddSingleton(new MissionBrowserOptions());
        // TryAdd on purpose: for seams the harness never registers, a host's TryAdd wins in either order.
        services.TryAddSingleton(host.Store);
        services.TryAddSingleton(host.Progress);
        services.TryAddSingleton<IProfileResolver, HostProfileResolver>();
        services.TryAddSingleton<IInjectorListProvider, HostInjectors>();
        services.TryAddSingleton<IToolInvocationGuard, HostGuard>();
        services.TryAddScoped<ICodingWorkspaceResolver, HostWorkspaces>();
        services.Replace(ServiceDescriptor.Scoped<ICodingLaneSource, HostLanes>()); // harness default replaced explicitly
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Harness_never_registers_host_policy_and_host_services_win(bool hostFirst)
    {
        var services = new ServiceCollection();
        var host = new HostInstances();
        if (hostFirst) { AddHost(services, host, harnessAlreadyAdded: false); services.AddSwarmCoreCodingHarness(); }
        else
        {
            // Harness first: the host's TryAdds still win for seams the harness never registers;
            // the permission broker needs Replace because AddSwarmCoreAgent already TryAdded a default.
            services.AddSwarmCoreCodingHarness();
            foreach (var type in HostOwned)
                Assert.DoesNotContain(services, d => d.ServiceType == type && IsHarnessType(d));
            AddHost(services, host, harnessAlreadyAdded: true);
        }

        foreach (var type in HostOwned)
            Assert.DoesNotContain(services, d => d.ServiceType == type && IsHarnessType(d));

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        Assert.IsType<HostProfileResolver>(provider.GetRequiredService<IProfileResolver>());
        Assert.IsType<HostInjectors>(provider.GetRequiredService<IInjectorListProvider>());
        Assert.IsType<HostGuard>(provider.GetRequiredService<IToolInvocationGuard>());
        Assert.Same(host.Broker, provider.GetRequiredService<IPermissionBroker>());
        Assert.Same(host.Store, provider.GetRequiredService<IConversationStore>());
        Assert.Same(host.Progress, provider.GetRequiredService<IAgentToolProgressBroker>());
        Assert.Single(services, d => d.ServiceType == typeof(IPermissionBroker));

        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;
        Assert.IsType<HostWorkspaces>(sp.GetRequiredService<ICodingWorkspaceResolver>());
        Assert.Equal("scout", Assert.Single(provider.GetRequiredService<ICodingTurnOrchestrator>().Lanes).Name);

        // Every harness service resolves.
        Assert.NotNull(sp.GetRequiredService<ICodingTurnOrchestrator>());
        Assert.NotNull(sp.GetRequiredService<LaneHealthTracker>());
        Assert.NotNull(sp.GetRequiredService<RepeatedToolCallGuard>());
        Assert.NotNull(sp.GetRequiredService<ActiveCodingWorkspaces>());
        Assert.NotNull(sp.GetRequiredService<CodingModelCatalog>());
        Assert.NotNull(sp.GetRequiredService<ICodingAcceptanceVerifier>());
        Assert.Equal(3, sp.GetServices<INativeLaneRunner>().Count());
        Assert.Single(sp.GetServices<IContextInjector>(), i => i is RepoInstructionsInjector);
        Assert.Single(provider.GetServices<IHostedService>(), h => h is CodingToolRegistrar);
        Assert.Single(provider.GetServices<IHostedService>(), h => h is MissionPrimitiveToolRegistrationService);

        CodingToolRegistrar.RegisterAll(provider);
        var ids = provider.GetRequiredService<ICapabilityCatalog>().All().Select(t => t.ToolId).ToHashSet();
        foreach (var id in CodingToolRegistrar.CodingToolIds) Assert.Contains(id, ids);
    }

    [Fact]
    public void Host_broker_registered_with_TryAdd_after_the_harness_loses_to_the_agent_runtime_default()
    {
        // Pins the documented contract: register the broker before the harness, or Replace it.
        var services = new ServiceCollection();
        services.AddSwarmCoreCodingHarness();
        services.TryAddSingleton(Unimplemented<IPermissionBroker>());
        Assert.Equal(typeof(PermissionBroker), Assert.Single(services, d => d.ServiceType == typeof(IPermissionBroker)).ImplementationType);
    }

    [Fact]
    public void Harness_is_idempotent_over_agent_and_mission_registrations()
    {
        var once = new ServiceCollection();
        AddHost(once, new HostInstances(), harnessAlreadyAdded: false);
        var baseline = once.Count;
        once.AddSwarmCoreCodingHarness();
        var withHarness = once.Count;
        once.AddSwarmCoreAgent();
        once.AddSwarmCoreMissionExecution();
        once.AddSwarmCoreCodingHarness();

        Assert.True(withHarness > baseline);
        // A second round of agent, mission-execution and harness registration adds nothing at all.
        Assert.Equal(withHarness, once.Count);
        Assert.Single(once, d => d.ImplementationType == typeof(CodingToolRegistrar));
        Assert.Single(once, d => d.ImplementationType == typeof(MissionPrimitiveToolRegistrationService));
        Assert.Single(once, d => d.ServiceType == typeof(ICodingTurnOrchestrator));
    }

    [Fact]
    public void Harness_composes_into_a_host_that_registered_mission_tools()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddSwarmCoreAgent();
        services.AddSwarmCoreMissionTools();
        var before = services.Count(d => d.ImplementationType == typeof(MissionPrimitiveToolRegistrationService));
        services.AddSwarmCoreCodingHarness();

        Assert.Equal(1, before);
        Assert.Single(services, d => d.ImplementationType == typeof(MissionPrimitiveToolRegistrationService));
        Assert.Single(services, d => d.ServiceType == typeof(ISandboxPathResolver));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IProfileResolver));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IInjectorListProvider));
    }

    private static T Unimplemented<T>() where T : class => System.Reflection.DispatchProxy.Create<T, NotCalled>();

    public class NotCalled : System.Reflection.DispatchProxy
    {
        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
            => throw new NotSupportedException("Composition test host: " + targetMethod?.Name);
    }

    private static bool IsHarnessType(ServiceDescriptor d)
    {
        if (d.ImplementationInstance is not null) return false; // the harness registers types and factories only
        var type = d.ImplementationType;
        if (type is null && d.ImplementationFactory is not null) return d.ImplementationFactory.Method.DeclaringType?.Assembly == typeof(CodingTurnOrchestrator).Assembly;
        return type?.Assembly == typeof(CodingTurnOrchestrator).Assembly
               || type == typeof(InMemoryAgentToolProgressBroker);
    }
}
