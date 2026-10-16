using System.Text.Json;
using System.Text.Json.Nodes;
using SwarmCore.Abstractions.Navigation;

namespace SwarmCore.Mcp.OAuth;

/// <summary>Give typed action targets the same profile-constrained citations as retrieval.</summary>
public static class RemoteSourceLinkProjection
{
    public static JsonObject Project(JsonElement receipt, string origin, Guid connection)
    {
        var output = JsonNode.Parse(receipt.GetRawText())!.AsObject();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (output["result"] is JsonValue value && value.TryGetValue<string>(out var result))
        {
            try
            {
                var data = JsonNode.Parse(result);
                Visit(data, 0);
                output["result"] = data?.ToJsonString();
            }
            catch (JsonException) { /* Plain result text is not a structured target. */ }
        }
        output["sources"] = new JsonArray(ids.Order(StringComparer.Ordinal).Take(100).Select(id => (JsonNode)new JsonObject
            { ["id"] = id, ["url"] = SourceLinks.PublicUrl(origin, id, connection) }).ToArray());
        return output;

        void Visit(JsonNode? node, int depth)
        {
            if (depth > 32 || ids.Count >= 100) return;
            if (node is JsonArray array) { foreach (var child in array) Visit(child, depth + 1); return; }
            if (node is not JsonObject obj) return;
            foreach (var property in obj.ToArray())
            {
                if (property.Value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
                {
                    var kind = property.Key.ToLowerInvariant() switch
                    {
                        "noteid" => "note", "taskid" => "task", "contactid" => "contact", "documentid" => "document",
                        "eventid" or "calendareventid" => "calendar", "matterid" => "matter", _ => null
                    };
                    if (kind is not null && Guid.TryParse(text, out var key) && key != Guid.Empty) ids.Add($"{kind}.{key:N}");
                    if (property.Key.ToLowerInvariant() is "url" or "sourceurl" && Uri.TryCreate(text, UriKind.Absolute, out var uri)
                        && SourceLinks.TryReadUri(uri, out var link))
                    {
                        ids.Add(link.Id);
                        obj[property.Key] = SourceLinks.PublicUrl(origin, link.Id, connection);
                    }
                }
                Visit(property.Value, depth + 1);
            }
        }
    }
}
