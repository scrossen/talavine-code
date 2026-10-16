using System.Diagnostics;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.Services;

namespace SwarmCore.Agent.Catalog;

/// <summary>
/// Compatibility facade for Model Scout control paths. Physical provider work
/// is owned by the Core process-wide discovery coordinator so chat validation,
/// routing, settings, and background recovery cannot create independent pools
/// of token-ignoring provider calls.
/// </summary>
public static class ModelScoutProviderProbeCoordinator
{
    public static async Task<ModelScoutProviderProbe> ProbeAsync(
        LlmProviderSettings settings,
        ILlmProvider provider,
        TimeSpan callerTimeout,
        CancellationToken ct = default,
        bool forceRefresh = true,
        LlmProviderDiscoveryCoordinator? coordinator = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(provider);
        if (callerTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(callerTimeout));

        coordinator ??= LlmProviderDiscoveryCoordinator.Shared;
        var configurationIdentity = LlmProviderDiscoveryCoordinator.CreateConfigurationIdentity(settings);
        var started = Stopwatch.GetTimestamp();
        var connectivity = await coordinator.ProbeConnectivityAsync(
                provider,
                callerTimeout,
                ct,
                forceRefresh,
                highPriority: true,
                configurationIdentity: configurationIdentity)
            .ConfigureAwait(false);
        if (!connectivity.Completed || !connectivity.IsHealthy)
        {
            return new ModelScoutProviderProbe(
                false,
                connectivity.Message,
                [],
                connectivity.Deferred);
        }

        var remaining = callerTimeout - Stopwatch.GetElapsedTime(started);
        if (remaining <= TimeSpan.Zero)
        {
            return new ModelScoutProviderProbe(
                false,
                "Provider validation timed out before its model inventory could be checked.",
                []);
        }

        var catalog = await coordinator.GetModelCatalogAsync(
                provider,
                remaining,
                ct,
                forceRefresh,
                highPriority: true,
                configurationIdentity: configurationIdentity)
            .ConfigureAwait(false);
        if (!catalog.Completed)
        {
            return new ModelScoutProviderProbe(
                false,
                catalog.Message,
                [],
                catalog.Deferred);
        }

        return new ModelScoutProviderProbe(connectivity.IsHealthy, connectivity.Message, catalog.Models);
    }
}

public sealed record ModelScoutProviderProbe(
    bool IsHealthy,
    string Message,
    IReadOnlyList<string> Models,
    bool Deferred = false);
