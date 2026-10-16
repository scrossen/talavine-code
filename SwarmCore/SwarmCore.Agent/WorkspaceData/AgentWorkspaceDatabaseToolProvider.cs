using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Catalog;

namespace SwarmCore.Agent.WorkspaceData;

/// <summary>
/// Explicit catalog contribution for hosts that intentionally enable durable
/// agent workspace data. The store resolves principal and membership grants
/// from its host context accessor; no tool accepts identity or ACL mutations.
/// </summary>
public static class AgentWorkspaceDatabaseToolProvider
{
    public const string OwnerId = "agent.workspace-database";

    public static void Register(ICapabilityCatalog catalog, IServiceProvider hostServices)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(hostServices);
        foreach (var tool in GetTools(hostServices))
            catalog.Register(tool);
    }

    public static IEnumerable<ToolDescriptor> GetTools(IServiceProvider hostServices)
    {
        ToolEnvironment Environment(IServiceProvider provider) => new(
            provider.GetRequiredService<IAgentWorkspaceDatabaseStore>());

        yield return ToolDescriptor.Create<CreateDatabaseInput, AgentWorkspaceDatabaseInfo>(
            "workspace_db.create",
            OwnerId,
            "Create a private agent workspace database by lower-case name slug. Shared creation succeeds only when the host has supplied an exact membership grant; this tool cannot add members. No paths or SQL are accepted.",
            ToolCostClass.Cheap,
            ToolSideEffect.WritesLocal,
            async (input, ct) =>
            {
                using var scope = hostServices.GetRequiredService<IServiceScopeFactory>().CreateScope();
                var env = Environment(scope.ServiceProvider);
                return await env.Store.CreateDatabaseAsync(input.DatabaseSlug, input.Visibility,
                    ParseOperationId(input.OperationId), ct);
            },
            keywords: ["workspace", "database", "create", "private", "shared"],
            dataAccess: ToolDataAccessClass.VerifiedNonCommunicationExternal);

        yield return ToolDescriptor.Create<CreateTableInput, AgentWorkspaceTableInfo>(
            "workspace_db.create_table",
            OwnerId,
            "Create a typed table inside a database you can write. Field names and table names are lower-case slugs. Supported field types are text, integer, decimal, boolean, and datetime.",
            ToolCostClass.Cheap,
            ToolSideEffect.WritesLocal,
            async (input, ct) =>
            {
                using var scope = hostServices.GetRequiredService<IServiceScopeFactory>().CreateScope();
                var env = Environment(scope.ServiceProvider);
                return await env.Store.CreateTableAsync(Handle(input.OwnerAgentId, input.DatabaseSlug),
                    new AgentWorkspaceTableSchema(input.TableSlug, input.Fields), ParseOperationId(input.OperationId), ct);
            },
            keywords: ["workspace", "database", "table", "schema"],
            dataAccess: ToolDataAccessClass.VerifiedNonCommunicationExternal);

        yield return ToolDescriptor.Create<ReadRowsInput, IReadOnlyList<AgentWorkspaceRow>>(
            "workspace_db.read_rows",
            OwnerId,
            "Read rows from a typed workspace table, optionally applying one typed equality or text-contains filter. Results are deterministic and limited to at most 100 rows.",
            ToolCostClass.Cheap,
            ToolSideEffect.ReadOnly,
            async (input, ct) =>
            {
                using var scope = hostServices.GetRequiredService<IServiceScopeFactory>().CreateScope();
                var env = Environment(scope.ServiceProvider);
                return await env.Store.ReadRowsAsync(Handle(input.OwnerAgentId, input.DatabaseSlug),
                    input.TableSlug, input.Filter, input.Limit, ct);
            },
            keywords: ["workspace", "database", "read", "filter", "rows"],
            dataAccess: ToolDataAccessClass.VerifiedNonCommunicationExternal);

        yield return ToolDescriptor.Create<UpsertRowInput, AgentWorkspaceMutationReceipt>(
            "workspace_db.upsert_row",
            OwnerId,
            "Create or update one typed row using optimistic concurrency. Use expectedVersion 0 only for a new id; otherwise provide the latest row version. Deleted ids remain tombstoned and cannot be recreated.",
            ToolCostClass.Cheap,
            ToolSideEffect.WritesLocal,
            async (input, ct) =>
            {
                using var scope = hostServices.GetRequiredService<IServiceScopeFactory>().CreateScope();
                var env = Environment(scope.ServiceProvider);
                return await env.Store.UpsertRowAsync(Handle(input.OwnerAgentId, input.DatabaseSlug),
                    input.TableSlug, input.RowId, input.Fields, input.ExpectedVersion, ParseOperationId(input.OperationId), ct);
            },
            keywords: ["workspace", "database", "upsert", "row", "version"],
            dataAccess: ToolDataAccessClass.VerifiedNonCommunicationExternal);

        yield return ToolDescriptor.Create<DeleteRowInput, AgentWorkspaceMutationReceipt>(
            "workspace_db.delete_row",
            OwnerId,
            "Tombstone one row only when its current version matches expectedVersion. This is a local data mutation; the tombstone blocks stale saves from resurrecting the row.",
            ToolCostClass.Cheap,
            ToolSideEffect.WritesLocal,
            async (input, ct) =>
            {
                using var scope = hostServices.GetRequiredService<IServiceScopeFactory>().CreateScope();
                var env = Environment(scope.ServiceProvider);
                return await env.Store.DeleteRowAsync(Handle(input.OwnerAgentId, input.DatabaseSlug),
                    input.TableSlug, input.RowId, input.ExpectedVersion, ParseOperationId(input.OperationId), ct);
            },
            keywords: ["workspace", "database", "delete", "tombstone", "version"],
            dataAccess: ToolDataAccessClass.VerifiedNonCommunicationExternal);
    }

    private static AgentWorkspaceDatabaseHandle Handle(string ownerAgentId, string databaseSlug)
        => new(ownerAgentId, databaseSlug);

    private static Guid ParseOperationId(string operationId)
        => Guid.TryParse(operationId, out var parsed) && parsed != Guid.Empty
            ? parsed
            : throw new AgentWorkspaceDatabaseValidationException("OperationId must be a stable, non-empty GUID.");

    private sealed record ToolEnvironment(IAgentWorkspaceDatabaseStore Store);

    public sealed record CreateDatabaseInput(
        string DatabaseSlug,
        AgentWorkspaceDatabaseVisibility Visibility,
        string OperationId);

    public sealed record CreateTableInput(
        string OwnerAgentId,
        string DatabaseSlug,
        string TableSlug,
        IReadOnlyList<AgentWorkspaceFieldDefinition> Fields,
        string OperationId);

    public sealed record ReadRowsInput(
        string OwnerAgentId,
        string DatabaseSlug,
        string TableSlug,
        AgentWorkspaceRowFilter? Filter = null,
        int Limit = 50);

    public sealed record UpsertRowInput(
        string OwnerAgentId,
        string DatabaseSlug,
        string TableSlug,
        string RowId,
        IReadOnlyDictionary<string, JsonElement> Fields,
        long ExpectedVersion,
        string OperationId);

    public sealed record DeleteRowInput(
        string OwnerAgentId,
        string DatabaseSlug,
        string TableSlug,
        string RowId,
        long ExpectedVersion,
        string OperationId);
}
