using System.Text.Json;

namespace SwarmCore.Agent.WorkspaceData;

public enum AgentWorkspaceDatabaseVisibility
{
    Private = 0,
    Shared = 1,
}

public enum AgentWorkspaceFieldType
{
    Text = 0,
    Integer = 1,
    Decimal = 2,
    Boolean = 3,
    DateTime = 4,
}

public enum AgentWorkspaceFilterOperator
{
    Equal = 0,
    Contains = 1,
}

/// <summary>
/// Authenticated, host-supplied scope for one agent turn. Never accept this
/// object from tool input; hosts resolve it from the current profile/tenant.
/// Memberships are explicit grants supplied by the host, not model edits.
/// </summary>
public sealed record AgentWorkspaceDatabaseAccessContext
{
    public required string TenantId { get; init; }
    public required string ProfileId { get; init; }
    public required string AgentId { get; init; }
    public IReadOnlyList<AgentWorkspaceDatabaseMembership> Memberships { get; init; } = [];
}

/// <summary>Host-reviewed membership for one shared database.</summary>
public sealed record AgentWorkspaceDatabaseMembership(
    string TenantId,
    string ProfileId,
    string OwnerAgentId,
    string DatabaseSlug,
    string MemberAgentId,
    bool CanRead,
    bool CanWrite,
    bool CanCreateShared = false);

/// <summary>Opaque logical locator. It contains no path or SQL name.</summary>
public sealed record AgentWorkspaceDatabaseHandle(string OwnerAgentId, string DatabaseSlug);

public sealed record AgentWorkspaceFieldDefinition(
    string Name,
    AgentWorkspaceFieldType Type,
    bool Required = false);

public sealed record AgentWorkspaceTableSchema(
    string TableSlug,
    IReadOnlyList<AgentWorkspaceFieldDefinition> Fields);

public sealed record AgentWorkspaceRowFilter(
    string FieldName,
    AgentWorkspaceFilterOperator Operator,
    JsonElement Value);

public sealed record AgentWorkspaceDatabaseInfo(
    AgentWorkspaceDatabaseHandle Handle,
    AgentWorkspaceDatabaseVisibility Visibility,
    DateTimeOffset CreatedAtUtc);

public sealed record AgentWorkspaceTableInfo(
    string TableSlug,
    IReadOnlyList<AgentWorkspaceFieldDefinition> Fields,
    long Version);

public sealed record AgentWorkspaceRow(
    string RowId,
    long Version,
    IReadOnlyDictionary<string, JsonElement> Fields,
    DateTimeOffset UpdatedAtUtc);

public sealed record AgentWorkspaceMutationReceipt(
    string OperationId,
    long Version,
    bool Replayed);

public interface IAgentWorkspaceDatabaseContextAccessor
{
    ValueTask<AgentWorkspaceDatabaseAccessContext> GetCurrentAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Typed, bounded data surface. There is deliberately no path, SQL, shell,
/// attach, or membership-edit operation in this contract.
/// </summary>
public interface IAgentWorkspaceDatabaseStore
{
    Task<AgentWorkspaceDatabaseInfo> CreateDatabaseAsync(
        string databaseSlug,
        AgentWorkspaceDatabaseVisibility visibility,
        Guid operationId,
        CancellationToken cancellationToken = default);

    Task<AgentWorkspaceTableInfo> CreateTableAsync(
        AgentWorkspaceDatabaseHandle database,
        AgentWorkspaceTableSchema schema,
        Guid operationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentWorkspaceRow>> ReadRowsAsync(
        AgentWorkspaceDatabaseHandle database,
        string tableSlug,
        AgentWorkspaceRowFilter? filter = null,
        int limit = 50,
        CancellationToken cancellationToken = default);

    Task<AgentWorkspaceMutationReceipt> UpsertRowAsync(
        AgentWorkspaceDatabaseHandle database,
        string tableSlug,
        string rowId,
        IReadOnlyDictionary<string, JsonElement> fields,
        long expectedVersion,
        Guid operationId,
        CancellationToken cancellationToken = default);

    Task<AgentWorkspaceMutationReceipt> DeleteRowAsync(
        AgentWorkspaceDatabaseHandle database,
        string tableSlug,
        string rowId,
        long expectedVersion,
        Guid operationId,
        CancellationToken cancellationToken = default);
}

public class AgentWorkspaceDatabaseException(string message) : InvalidOperationException(message);

public sealed class AgentWorkspaceDatabaseValidationException(string message)
    : AgentWorkspaceDatabaseException(message);

public sealed class AgentWorkspaceDatabaseAccessException(string message)
    : AgentWorkspaceDatabaseException(message);

public sealed class AgentWorkspaceDatabaseConflictException(string message)
    : AgentWorkspaceDatabaseException(message);

/// <summary>The transaction may already be durable. Reconcile by operation identity;
/// this error does not authorize an automatic retry or exposure of a saved receipt.</summary>
public sealed class AgentWorkspaceDatabaseOutcomeUnknownException(Guid operationId)
    : AgentWorkspaceDatabaseException("Workspace operation outcome unknown after commit; reconcile the operation identity before retrying.")
{
    public Guid OperationId { get; } = operationId;
}
