using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting.Services;

/// <summary>
/// Fail-closed DI sentinel used when a host enables SwarmCore routing without
/// supplying the required durable audit ledger. It prevents an apparently
/// functional but unauditable router from reaching a provider.
/// </summary>
internal sealed class MissingLlmRoutingAuditStore : ILlmRoutingAuditStore
{
    private static InvalidOperationException MissingStore() => new(
        "No durable ILlmRoutingAuditStore is registered. Register a host-persisted append-only audit store before using ILlmRouter.");

    public Task AppendAsync(LlmRoutingAuditEntry entry, CancellationToken ct = default) =>
        Task.FromException(MissingStore());

    public Task<IReadOnlyList<LlmRoutingAuditEntry>> QueryAsync(
        LlmRoutingAuditQuery query,
        CancellationToken ct = default) =>
        Task.FromException<IReadOnlyList<LlmRoutingAuditEntry>>(MissingStore());
}
