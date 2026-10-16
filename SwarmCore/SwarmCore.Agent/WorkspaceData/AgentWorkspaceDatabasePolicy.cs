using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SwarmCore.Agent.WorkspaceData;

/// <summary>Shared validation and ACL rules for host adapters of workspace data.</summary>
public static class AgentWorkspaceDatabasePolicy
{
    public const int MaximumDatabaseSlugLength = 63;
    public const int MaximumFieldCount = 32;
    public const int MaximumReadLimit = 100;
    public const int MaximumRowIdLength = 128;
    public const int MaximumRowJsonLength = 65_536;

    public static void ValidateContext(AgentWorkspaceDatabaseAccessContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidateIdentity(context.TenantId, nameof(context.TenantId));
        ValidateIdentity(context.ProfileId, nameof(context.ProfileId));
        ValidateIdentity(context.AgentId, nameof(context.AgentId));
        if (context.Memberships is null)
            throw new AgentWorkspaceDatabaseValidationException("Workspace memberships are required.");
        foreach (var membership in context.Memberships)
        {
            ValidateIdentity(membership.TenantId, nameof(membership.TenantId));
            ValidateIdentity(membership.ProfileId, nameof(membership.ProfileId));
            ValidateIdentity(membership.OwnerAgentId, nameof(membership.OwnerAgentId));
            ValidateSlug(membership.DatabaseSlug, "database");
            ValidateIdentity(membership.MemberAgentId, nameof(membership.MemberAgentId));
            if (!string.Equals(membership.TenantId, context.TenantId, StringComparison.Ordinal)
                || !string.Equals(membership.ProfileId, context.ProfileId, StringComparison.Ordinal))
                throw new AgentWorkspaceDatabaseAccessException("Workspace membership grant belongs to a different tenant or profile.");
            if (string.Equals(membership.OwnerAgentId, membership.MemberAgentId, StringComparison.OrdinalIgnoreCase)
                || (!membership.CanRead && membership.CanWrite))
                throw new AgentWorkspaceDatabaseValidationException("Workspace membership grant is invalid.");
        }
        if (context.Memberships.GroupBy(x => (x.TenantId, x.ProfileId, x.OwnerAgentId, x.DatabaseSlug, x.MemberAgentId),
                StringTupleComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new AgentWorkspaceDatabaseValidationException("Workspace memberships contain duplicate grants.");
    }

    public static void ValidateSlug(string slug, string kind)
    {
        if (string.IsNullOrWhiteSpace(slug) || slug.Length > MaximumDatabaseSlugLength
            || slug[0] is < 'a' or > 'z'
            || slug.Any(character => !(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
            || slug[^1] == '-')
            throw new AgentWorkspaceDatabaseValidationException(
                $"{kind} name must be a lower-case slug using letters, digits, and interior hyphens only.");
    }

    public static void ValidateOperationId(Guid operationId)
    {
        if (operationId == Guid.Empty)
            throw new AgentWorkspaceDatabaseValidationException("A stable non-empty operation id is required.");
    }

    public static void ValidateDatabaseCreate(
        AgentWorkspaceDatabaseAccessContext context,
        string databaseSlug,
        AgentWorkspaceDatabaseVisibility visibility,
        Guid operationId)
    {
        ValidateContext(context);
        ValidateSlug(databaseSlug, "Database");
        ValidateOperationId(operationId);
        if (!Enum.IsDefined(visibility))
            throw new AgentWorkspaceDatabaseValidationException("Database visibility is invalid.");
        if (visibility == AgentWorkspaceDatabaseVisibility.Shared
            && !context.Memberships.Any(grant => grant.CanCreateShared
                && grant.CanRead
                && string.Equals(grant.TenantId, context.TenantId, StringComparison.Ordinal)
                && string.Equals(grant.ProfileId, context.ProfileId, StringComparison.Ordinal)
                && string.Equals(grant.OwnerAgentId, context.AgentId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(grant.DatabaseSlug, databaseSlug, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(grant.MemberAgentId, context.AgentId, StringComparison.OrdinalIgnoreCase)))
            throw new AgentWorkspaceDatabaseAccessException(
                "Shared database creation requires a host-supplied membership grant for this database name.");
    }

    public static AgentWorkspaceDatabaseVisibility Authorize(
        AgentWorkspaceDatabaseAccessContext context,
        AgentWorkspaceDatabaseHandle database,
        bool write)
    {
        ValidateContext(context);
        ArgumentNullException.ThrowIfNull(database);
        ValidateIdentity(database.OwnerAgentId, nameof(database.OwnerAgentId));
        ValidateSlug(database.DatabaseSlug, "Database");
        if (string.Equals(context.AgentId, database.OwnerAgentId, StringComparison.OrdinalIgnoreCase))
            return AgentWorkspaceDatabaseVisibility.Private;

        var membership = context.Memberships.FirstOrDefault(grant =>
            string.Equals(grant.TenantId, context.TenantId, StringComparison.Ordinal)
            && string.Equals(grant.ProfileId, context.ProfileId, StringComparison.Ordinal)
            &&
            string.Equals(grant.OwnerAgentId, database.OwnerAgentId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(grant.DatabaseSlug, database.DatabaseSlug, StringComparison.OrdinalIgnoreCase)
            && string.Equals(grant.MemberAgentId, context.AgentId, StringComparison.OrdinalIgnoreCase));
        if (membership is null || !membership.CanRead || write && !membership.CanWrite)
            throw new AgentWorkspaceDatabaseAccessException("The current agent has no grant for this workspace database operation.");
        return AgentWorkspaceDatabaseVisibility.Shared;
    }

    public static void ValidateSchema(AgentWorkspaceTableSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ValidateSlug(schema.TableSlug, "Table");
        if (schema.Fields is null || schema.Fields.Count is < 1 or > MaximumFieldCount)
            throw new AgentWorkspaceDatabaseValidationException($"A table must define between 1 and {MaximumFieldCount} fields.");
        foreach (var field in schema.Fields)
        {
            if (field is null) throw new AgentWorkspaceDatabaseValidationException("Table fields cannot be null.");
            ValidateSlug(field.Name, "Field");
            if (!Enum.IsDefined(field.Type))
                throw new AgentWorkspaceDatabaseValidationException($"Field '{field.Name}' has an unsupported type.");
        }
        if (schema.Fields.GroupBy(field => field.Name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new AgentWorkspaceDatabaseValidationException("Table field names must be unique, ignoring case.");
    }

    public static void ValidateRead(int limit, AgentWorkspaceRowFilter? filter, AgentWorkspaceTableSchema schema)
    {
        if (limit is < 1 or > MaximumReadLimit)
            throw new AgentWorkspaceDatabaseValidationException($"Read limit must be between 1 and {MaximumReadLimit}.");
        if (filter is null) return;
        if (!Enum.IsDefined(filter.Operator))
            throw new AgentWorkspaceDatabaseValidationException("Filter operator is unsupported.");
        var field = schema.Fields.FirstOrDefault(value => string.Equals(
            value.Name, filter.FieldName, StringComparison.OrdinalIgnoreCase));
        if (field is null) throw new AgentWorkspaceDatabaseValidationException("Filter field is not in the table schema.");
        if (filter.Operator == AgentWorkspaceFilterOperator.Contains && field.Type != AgentWorkspaceFieldType.Text)
            throw new AgentWorkspaceDatabaseValidationException("Contains filters are available only for text fields.");
        ValidateValue(field, filter.Value);
    }

    public static void ValidateRow(AgentWorkspaceTableSchema schema, IReadOnlyDictionary<string, JsonElement> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (fields.Count > MaximumFieldCount)
            throw new AgentWorkspaceDatabaseValidationException("Row payload exceeds the bounded workspace data limit.");
        if (fields.Keys.Any(string.IsNullOrWhiteSpace))
            throw new AgentWorkspaceDatabaseValidationException("Row field names cannot be empty.");
        if (fields.Keys.GroupBy(name => name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new AgentWorkspaceDatabaseValidationException("Row contains duplicate field names.");
        foreach (var field in fields)
        {
            var definition = schema.Fields.FirstOrDefault(candidate => string.Equals(
                candidate.Name, field.Key, StringComparison.OrdinalIgnoreCase));
            if (definition is null)
                throw new AgentWorkspaceDatabaseValidationException($"Row field '{field.Key}' is not in the table schema.");
            ValidateValue(definition, field.Value);
        }
        foreach (var definition in schema.Fields.Where(field => field.Required))
            if (!fields.Keys.Contains(definition.Name, StringComparer.OrdinalIgnoreCase))
                throw new AgentWorkspaceDatabaseValidationException($"Required row field '{definition.Name}' is missing.");
        if (fields.Sum(pair => pair.Key.Length + pair.Value.GetRawText().Length) > MaximumRowJsonLength)
            throw new AgentWorkspaceDatabaseValidationException("Row payload exceeds the bounded workspace data limit.");
    }

    public static void ValidateRowId(string rowId)
    {
        if (string.IsNullOrWhiteSpace(rowId) || rowId.Length > MaximumRowIdLength
            || rowId.Any(character => char.IsControl(character) || character is '/' or '\\'))
            throw new AgentWorkspaceDatabaseValidationException("Row id must be a non-empty local identifier, not a path.");
    }

    public static string OperationFingerprint(params string?[] parts)
    {
        var canonical = string.Join("\n", parts.Select(part => part ?? "<null>"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    public static string CanonicalFields(IReadOnlyDictionary<string, JsonElement> fields)
        => "{" + string.Join(",", fields.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => JsonSerializer.Serialize(pair.Key.ToLowerInvariant()) + ":" + pair.Value.GetRawText())) + "}";

    private static void ValidateValue(AgentWorkspaceFieldDefinition field, JsonElement value)
    {
        var valid = field.Type switch
        {
            AgentWorkspaceFieldType.Text => value.ValueKind == JsonValueKind.String,
            AgentWorkspaceFieldType.Integer => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            AgentWorkspaceFieldType.Decimal => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out _),
            AgentWorkspaceFieldType.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            AgentWorkspaceFieldType.DateTime => value.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _),
            _ => false,
        };
        if (!valid) throw new AgentWorkspaceDatabaseValidationException($"Value for field '{field.Name}' does not match its declared type {field.Type}.");
    }

    private static void ValidateIdentity(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl))
            throw new AgentWorkspaceDatabaseValidationException($"{fieldName} is missing or invalid.");
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string TenantId, string ProfileId, string OwnerAgentId, string DatabaseSlug, string MemberAgentId)>
    {
        public static readonly StringTupleComparer OrdinalIgnoreCase = new();
        public bool Equals((string TenantId, string ProfileId, string OwnerAgentId, string DatabaseSlug, string MemberAgentId) x,
            (string TenantId, string ProfileId, string OwnerAgentId, string DatabaseSlug, string MemberAgentId) y)
            => string.Equals(x.TenantId, y.TenantId, StringComparison.OrdinalIgnoreCase)
               && string.Equals(x.ProfileId, y.ProfileId, StringComparison.OrdinalIgnoreCase)
               && string.Equals(x.OwnerAgentId, y.OwnerAgentId, StringComparison.OrdinalIgnoreCase)
               && string.Equals(x.DatabaseSlug, y.DatabaseSlug, StringComparison.OrdinalIgnoreCase)
               && string.Equals(x.MemberAgentId, y.MemberAgentId, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string TenantId, string ProfileId, string OwnerAgentId, string DatabaseSlug, string MemberAgentId) value)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.TenantId),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.ProfileId),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.OwnerAgentId),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.DatabaseSlug),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.MemberAgentId));
    }
}
