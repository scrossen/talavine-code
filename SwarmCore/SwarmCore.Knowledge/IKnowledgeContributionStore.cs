namespace SwarmCore.Knowledge;

/// <summary>
/// Source-neutral exact-contribution persistence boundary. Host adapters own
/// transactions, tenancy/RLS, desktop sync stamps, and physical row shapes.
/// </summary>
public interface IKnowledgeContributionStore
{
    Task<KnowledgeContributionApplyResult> ApplyAsync(
        KnowledgeContributionBundle bundle,
        CancellationToken ct = default);

    Task<KnowledgeContributionRemovalResult> RemoveAsync(
        KnowledgePartition partition,
        KnowledgeSourceId source,
        CancellationToken ct = default);

    Task<KnowledgeContributionLedgerStatus?> GetLedgerStatusAsync(
        KnowledgePartition partition,
        CancellationToken ct = default);

    Task BeginBuildAsync(KnowledgePartition partition, CancellationToken ct = default);

    Task CompleteBuildAsync(
        KnowledgePartition partition,
        int scannedSourceCount,
        int eligibleSourceCount,
        CancellationToken ct = default);

    Task FailBuildAsync(
        KnowledgePartition partition,
        string errorCategory,
        CancellationToken ct = default);
}
