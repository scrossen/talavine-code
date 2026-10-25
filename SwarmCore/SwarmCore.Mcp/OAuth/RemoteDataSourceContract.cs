using System.Text.Json;
using SwarmCore.Abstractions.Navigation;
using SwarmCore.Abstractions.OAuth;

namespace SwarmCore.Mcp.OAuth;

/// <summary>Host-independent rules for a read-only, profile-bound remote data source.</summary>
public static class RemoteDataSourceContract
{
    public const string Scope = "context.read";
    public const string ProfileScope = "profile.access";

    /// <summary>
    /// Page size of the remote <c>search</c> tool: the limit forwarded to the
    /// desktop, the number of hits the gateway relays and the count its tool
    /// description promises.
    /// </summary>
    public const int SearchResultLimit = 20;
    public static bool IsValidScope(string? scope)
    {
        var values = (scope ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return values.Length > 0 && values.Length == values.Distinct(StringComparer.Ordinal).Count()
            && values.All(x => x is Scope or ProfileScope);
    }
    public static bool HasProfileAccess(string? scope) => (scope ?? "").Split(' ').Contains(ProfileScope, StringComparer.Ordinal);
    public static string? ValidateAuthorization(string? responseType, string? method, string? challenge,
        string? scope, string? resource, string expectedResource)
    {
        if (responseType != "code") return "unsupported_response_type";
        if (method != "S256" || !OAuthPkce.IsValidS256Challenge(challenge)) return "invalid_request";
        if (!IsValidScope(scope)) return "invalid_scope";
        if (resource != expectedResource) return "invalid_target";
        return null;
    }

    public static string SourceId(Guid accountId, Guid messageId) => SourceLinks.MessageId(accountId, messageId);
    public static bool TryReadSourceId(string? id, out Guid accountId, out Guid messageId)
    {
        accountId = messageId = Guid.Empty;
        var parts = (id ?? "").Split('.');
        return parts.Length == 2 && Guid.TryParseExact(parts[0], "N", out accountId)
            && Guid.TryParseExact(parts[1], "N", out messageId) && accountId != Guid.Empty && messageId != Guid.Empty;
    }

    public static readonly string[] RecordKinds = SourceLinks.RecordKinds;
    public static string RecordId(string kind, Guid id)
    {
        if (!RecordKinds.Contains(kind) || id == Guid.Empty) throw new ArgumentException("Invalid record reference.");
        return $"{kind}.{id:N}";
    }
    public static bool TryReadRecordId(string? value, out string kind, out Guid id)
    {
        kind = ""; id = Guid.Empty;
        var parts = (value ?? "").Split('.');
        if (parts.Length != 2 || !RecordKinds.Contains(parts[0]) || !Guid.TryParseExact(parts[1], "N", out id) || id == Guid.Empty) return false;
        kind = parts[0]; return true;
    }

    /// <summary>Only translate the standard retrieval contract to the host's bounded read tools.</summary>
    public static (string Tool, object Arguments)? Translate(string? name, JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) return null;
        var properties = arguments.EnumerateObject().ToArray();
        if (properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length) return null;
        if (name == "list_sources" && properties.Length == 0) return ("data_source_discover", new { });
        if (name == "list_records")
        {
            if (properties.Any(p => p.Name is not ("kind" or "skip" or "start" or "end"))) return null;
            if (!arguments.TryGetProperty("kind", out var type) || type.ValueKind != JsonValueKind.String || !RecordKinds.Contains(type.GetString())) return null;
            int skip = 0;
            if (arguments.TryGetProperty("skip", out var offset) && (!offset.TryGetInt32(out skip) || skip < 0 || skip > 100_000)) return null;
            string? start = null, end = null;
            if (arguments.TryGetProperty("start", out var from))
            {
                if (from.ValueKind != JsonValueKind.String || !DateTimeOffset.TryParse(from.GetString(), out _)) return null;
                start = from.GetString();
            }
            if (arguments.TryGetProperty("end", out var to))
            {
                if (to.ValueKind != JsonValueKind.String || !DateTimeOffset.TryParse(to.GetString(), out _)) return null;
                end = to.GetString();
            }
            if (type.GetString() != "calendar" && (start is not null || end is not null)) return null;
            if ((start is null) != (end is null)) return null;
            if (start is not null && (DateTimeOffset.Parse(end!) <= DateTimeOffset.Parse(start) || DateTimeOffset.Parse(end!) - DateTimeOffset.Parse(start) > TimeSpan.FromDays(366))) return null;
            return ("data_source_list", new { kind = type.GetString(), skip, start, end });
        }
        if (name == "discover_tools")
        {
            if (properties.Any(p => p.Name is not ("query" or "skip"))) return null;
            string queryText = ""; int skip = 0;
            if (arguments.TryGetProperty("query", out var q) && (q.ValueKind != JsonValueKind.String || (queryText = q.GetString()!).Length > 2000)) return null;
            if (arguments.TryGetProperty("skip", out var s) && (!s.TryGetInt32(out skip) || skip < 0 || skip > 100000)) return null;
            return ("profile_tools_discover", new { query = queryText, skip });
        }
        if (name is "read_tool" or "execute_tool")
        {
            if (properties.Any(p => p.Name is not ("tool" or "input" or "user_request" or "operation_id"))) return null;
            if (!arguments.TryGetProperty("tool", out var t) || t.ValueKind != JsonValueKind.String || t.GetString() is not { Length: > 0 and <= 120 } tool) return null;
            if (!arguments.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object) return null;
            if (!arguments.TryGetProperty("user_request", out var u) || u.ValueKind != JsonValueKind.String || u.GetString() is not { Length: > 0 and <= 2000 } userRequest) return null;
            if (!arguments.TryGetProperty("operation_id", out var op) || op.ValueKind != JsonValueKind.String || !Guid.TryParse(op.GetString(), out var operation) || operation == Guid.Empty) return null;
            return (name == "read_tool" ? "profile_tool_read" : "profile_tool_execute", new { tool, input = input.Clone(), userRequest, operationId = operation });
        }
        if (properties.Length != 1) return null;
        if (name == "search" && arguments.TryGetProperty("query", out var query)
            && query.ValueKind == JsonValueKind.String && query.GetString() is { Length: > 0 and <= 2000 } text
            && !string.IsNullOrWhiteSpace(text))
            return ("data_source_search", new { query = text.Trim(), limit = SearchResultLimit });
        if (name is "build_context" or "query_knowledge_graph" && arguments.TryGetProperty("query", out var contextQuery)
            && contextQuery.ValueKind == JsonValueKind.String && contextQuery.GetString() is { Length: > 0 and <= 2000 } question
            && !string.IsNullOrWhiteSpace(question))
            return (name == "build_context" ? "data_source_context" : "data_source_graph", new { query = question.Trim() });
        if (name == "fetch" && arguments.TryGetProperty("id", out var attachment) && attachment.ValueKind == JsonValueKind.String
            && SourceLinks.TryReadId(attachment.GetString(), out var attachmentLink) && attachmentLink.Kind == "attachment")
            return ("data_source_fetch", new { id = attachment.GetString() });
        if (name == "fetch" && arguments.TryGetProperty("id", out var record) && record.ValueKind == JsonValueKind.String
            && TryReadRecordId(record.GetString(), out _, out _))
            return ("data_source_fetch", new { id = record.GetString() });
        if (name == "fetch" && arguments.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
            && TryReadSourceId(id.GetString(), out var account, out var message))
            return ("get_message", new { accountId = account.ToString(), messageId = message.ToString() });
        return null;
    }
}
