namespace SwarmCore.Coding.Peers;

/// <summary>Deterministic routing over fresh evidence. Missing facts cannot satisfy requested constraints.</summary>
public static class PeerRouting
{
    public static PeerCandidate Match(PeerIdentity peer, PeerCapabilities caps, PeerRequirements requirements, DateTimeOffset now)
    {
        PeerCandidate No(string reason) => new(peer, caps, false, reason);
        if (caps.DeviceId != peer.DeviceId) return No("Capability identity does not match the paired device.");
        var age = now.ToUnixTimeMilliseconds() - caps.GeneratedUtcMs;
        if (age is > 120_000 or < -30_000) return No("Capability snapshot is stale or its clock is ahead.");
        if (!caps.AgentBridgeEnabled || caps.LaneVersion < 2) return No("Peer needs its durable agent bridge enabled and a current SwarmSpan build.");
        if (requirements.Platform is { Length: > 0 } os && !Equal(Platform(os), Platform(caps.Platform))) return No("Operating system does not match.");
        if (requirements.Architecture is { Length: > 0 } arch && !Equal(arch, caps.Architecture)) return No("Architecture does not match.");
        var hardware = caps.Hardware;
        if (requirements.MinimumLogicalProcessors.HasValue || requirements.MinimumMemoryBytes.HasValue || requirements.RequireGpu
            || requirements.GpuName is { Length: > 0 } || requirements.MinimumGpuMemoryBytes.HasValue)
        {
            var measuredAge = now.ToUnixTimeMilliseconds() - (hardware?.MeasuredUtcMs ?? 0);
            if (measuredAge is > 600_000 or < -30_000) return No("Hardware inventory is missing or stale; update SwarmSpan on the peer.");
        }
        if (requirements.MinimumLogicalProcessors is { } cores && !(hardware?.LogicalProcessors >= cores)) return No("Insufficient or unknown processor count.");
        if (requirements.MinimumMemoryBytes is { } ram && !(hardware?.TotalMemoryBytes >= ram)) return No("Insufficient or unknown system memory.");
        if (requirements.RequireGpu || requirements.GpuName is { Length: > 0 } || requirements.MinimumGpuMemoryBytes is > 0)
        {
            var hardwareAge = now.ToUnixTimeMilliseconds() - (hardware?.MeasuredUtcMs ?? 0);
            if (hardwareAge is > 600_000 or < -30_000) return No("GPU inventory is missing or stale.");
            if (hardware?.Gpus.Any(g =>
                    (string.IsNullOrWhiteSpace(requirements.GpuName) || g.Name.Contains(requirements.GpuName, StringComparison.OrdinalIgnoreCase))
                    && (requirements.MinimumGpuMemoryBytes is not > 0 || g.DedicatedMemoryBytes >= requirements.MinimumGpuMemoryBytes)) != true)
                return No("No measured GPU satisfies the requested name and dedicated memory. Unified RAM is not VRAM.");
        }
        if (requirements.ExecutionMode == "job")
        {
            if (string.IsNullOrWhiteSpace(requirements.Job)) return No("Job mode requires an advertised job id.");
            if (requirements.RequireLocalInference || requirements.Provider is { Length: > 0 } || requirements.Model is { Length: > 0 })
                return No("Job mode cannot select an inference provider or model.");
            return caps.Jobs.Any(j => Equal(j.JobId, requirements.Job) && j.Available)
                ? new(peer, caps, true, null) : No("Requested job is unavailable to this paired device.");
        }
        if (requirements.Job is { Length: > 0 }) return No("Use executionMode=job to select a job.");
        if (requirements.ExecutionMode is not ("headless" or "persistent" or "visible")) return No("Execution mode must be headless, persistent, visible, or job.");
        var provider = caps.Providers.Where(p => p.Available
                && p.ExecutionModes.Contains(requirements.ExecutionMode, StringComparer.OrdinalIgnoreCase)
                && (!requirements.RequireLocalInference || p.LocalInference && p.LocalModels is { Count: > 0 }
                    && (string.IsNullOrWhiteSpace(requirements.Model) || p.LocalModels.Contains(requirements.Model, StringComparer.OrdinalIgnoreCase)))
                && (string.IsNullOrWhiteSpace(requirements.Provider) || Equal(p.ProviderId, requirements.Provider))
                && (string.IsNullOrWhiteSpace(requirements.Model) || p.Models.Contains(requirements.Model, StringComparer.OrdinalIgnoreCase)))
            .OrderByDescending(p => Equal(p.ProviderId, caps.DefaultProviderId))
            .ThenBy(p => p.ProviderId, StringComparer.Ordinal).FirstOrDefault();
        if (provider is null) return No("No ready provider satisfies the requested model, execution mode, and local inference requirement.");
        var allowedModels = requirements.RequireLocalInference ? provider.Models.Intersect(provider.LocalModels ?? [], StringComparer.OrdinalIgnoreCase).ToArray() : provider.Models;
        var model = string.IsNullOrWhiteSpace(requirements.Model)
            ? allowedModels.FirstOrDefault(m => Equal(m, provider.DefaultModel)) ?? allowedModels.FirstOrDefault()
            : allowedModels.FirstOrDefault(m => Equal(m, requirements.Model));
        if (string.IsNullOrWhiteSpace(model)) return No("The provider did not advertise a selectable model.");
        return new(peer, caps, true, null, provider.ProviderId, model);
    }

    public static void Validate(PeerRequirements r)
    {
        if (r.MinimumLogicalProcessors is < 1 || r.MinimumMemoryBytes is < 1 || r.MinimumGpuMemoryBytes is < 1)
            throw new ArgumentException("Minimum hardware capacities must be positive.");
    }

    private static bool Equal(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static string Platform(string value) => value.Trim().ToLowerInvariant() switch
    {
        "mac" or "maccatalyst" or "osx" or "macos" => "macos",
        "win" or "win32" or "windows" => "windows",
        var other => other,
    };
}
