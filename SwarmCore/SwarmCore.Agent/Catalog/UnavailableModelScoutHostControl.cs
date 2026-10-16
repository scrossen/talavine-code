using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.Agent.Catalog;

/// <summary>
/// Honest fallback for hosts that use the reusable Scout tools without wiring
/// application-specific maintenance, benchmark, recovery, or profile services.
/// </summary>
internal sealed class UnavailableModelScoutHostControl : IModelScoutHostControl
{
    internal const string Message =
        "This app has not connected its host-specific Model Scout controls. " +
        "Status, recommendations, explanations, function pins, and policy controls remain available.";

    public Task<ModelScoutHostControlStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new ModelScoutHostControlStatus(false, Message));

    public Task<ModelScoutHostControlResult> SetAutomaticManagementAsync(
        bool enabled,
        ModelScoutHostControlContext context,
        CancellationToken cancellationToken = default)
        => Unavailable();

    public Task<ModelScoutHostControlResult> OptimizeAsync(
        ModelScoutHostOptimizeRequest request,
        ModelScoutHostControlContext context,
        CancellationToken cancellationToken = default)
        => Unavailable();

    public Task<ModelScoutHostControlResult> BenchmarkAsync(
        ModelScoutHostBenchmarkRequest request,
        ModelScoutHostControlContext context,
        CancellationToken cancellationToken = default)
        => Unavailable();

    public Task<ModelScoutHostControlResult> RecoverAsync(
        ModelScoutHostRecoveryRequest request,
        ModelScoutHostControlContext context,
        CancellationToken cancellationToken = default)
        => Unavailable();

    public Task<ModelScoutHostControlResult> PinCurrentProfileAsync(
        string provider,
        string model,
        ModelScoutHostControlContext context,
        CancellationToken cancellationToken = default)
        => Unavailable();

    public Task<ModelScoutHostControlResult> UnpinCurrentProfileAsync(
        ModelScoutHostControlContext context,
        CancellationToken cancellationToken = default)
        => Unavailable();

    private static Task<ModelScoutHostControlResult> Unavailable()
        => Task.FromResult(new ModelScoutHostControlResult(false, Message));
}
