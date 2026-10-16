using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SwarmCore.Mcp.Abstractions;
using SwarmCore.Mcp.Models;
using SwarmCore.Skills.Abstractions;
using SwarmCore.Skills.Models;

namespace SwarmCore.Mcp.Core;

/// <summary>
/// Wraps a single MCP tool as a SwarmCore Skill so it can be discovered and invoked by agents.
/// </summary>
public class McpBridgedSkill : SkillBase
{
    private readonly Guid _connectionId;
    private readonly McpToolInfo _toolInfo;
    private readonly IMcpConnectorManager _connectorManager;
    private readonly string? _connectorName;
    private readonly Lazy<ParsedSchema> _schema;

    public McpBridgedSkill(
        Guid connectionId,
        McpToolInfo toolInfo,
        IMcpConnectorManager connectorManager,
        string? connectorName = null)
    {
        _connectionId = connectionId;
        _toolInfo = toolInfo;
        _connectorManager = connectorManager;
        _connectorName = string.IsNullOrWhiteSpace(connectorName) ? null : connectorName.Trim();
        _schema = new Lazy<ParsedSchema>(() => ParseSchema(toolInfo.InputSchemaJson));
    }

    /// <summary>The raw MCP tool name as the server reports it.</summary>
    public string ToolName => _toolInfo.Name;

    /// <summary>The connector this tool was bridged from, when known.</summary>
    public string? ConnectorName => _connectorName;

    /// <summary>
    /// Skill ids are compared case-insensitively by the registry while MCP tool
    /// names are case-sensitive, so a name containing upper-case characters gets a
    /// short stable hash appended to keep <c>Echo</c> and <c>echo</c> distinct.
    /// </summary>
    public override string SkillId => BuildSkillId(_connectionId, _toolInfo.Name);

    /// <summary>Connector-qualified name (<c>connector:tool</c>) so tools from different servers never collide.</summary>
    public override string Name => _connectorName is null ? _toolInfo.Name : $"{_connectorName}:{_toolInfo.Name}";

    public override string Description =>
        McpTextSanitizer.SanitizeDescription(_toolInfo.Description) ?? $"MCP tool: {_toolInfo.Name}";

    public override SkillCategory Category => SkillCategory.External;
    public override SkillTrustLevel TrustLevel => SkillTrustLevel.Community;

    public override IReadOnlyList<string> Keywords => _connectorName is null
        ? ["mcp", "external", _toolInfo.Name]
        : ["mcp", "external", _toolInfo.Name, _connectorName];

    public override IEnumerable<SkillFunction> GetFunctions() =>
    [
        new SkillFunction
        {
            Name = "Invoke",
            Description = McpTextSanitizer.SanitizeDescription(_toolInfo.Description) ?? $"Invoke MCP tool '{_toolInfo.Name}'",
            Parameters = _schema.Value.Parameters,
            ReturnType = "string",
        }
    ];

    public override async Task<SkillResult> ExecuteAsync(
        string functionName,
        SkillExecutionContext context,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var args = CoerceArguments(context.Parameters, _schema.Value.TypeMap);
            var result = await _connectorManager.InvokeToolAsync(
                _connectionId, _toolInfo.Name, args, ct);

            sw.Stop();

            if (result.IsError)
            {
                return SkillResult.Fail(result.Content) with
                {
                    SkillId = SkillId,
                    FunctionName = functionName,
                    ExecutionTime = sw.Elapsed,
                };
            }

            return SkillResult.Ok(result.Content, sw.Elapsed) with
            {
                SkillId = SkillId,
                FunctionName = functionName,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Let the executor's timeout / cancellation handling see real cancellation.
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return SkillResult.Fail(ex.Message, ex.ToString()) with
            {
                SkillId = SkillId,
                FunctionName = functionName,
                ExecutionTime = sw.Elapsed,
            };
        }
    }

    // ── Ids ─────────────────────────────────────────────────────────

    internal static string BuildSkillId(Guid connectionId, string toolName)
    {
        var id = $"mcp.{connectionId:N}.{toolName}";
        return toolName.Any(char.IsUpper) ? $"{id}.{ShortHash(toolName)}" : id;
    }

    /// <summary>Six hex characters of SHA-256 over the UTF-8 name — stable across processes.</summary>
    internal static string ShortHash(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(hash.AsSpan(0, 3));
    }

    // ── Argument coercion ───────────────────────────────────────────

    /// <summary>
    /// Coerces string-valued arguments to the schema's declared scalar type
    /// (integer / number / boolean) or parses JSON for array / object parameters.
    /// Values that are not strings, or that fail to parse, pass through unchanged.
    /// </summary>
    internal static Dictionary<string, object?> CoerceArguments(
        IReadOnlyDictionary<string, object> parameters,
        IReadOnlyDictionary<string, string> typeMap)
    {
        var args = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in parameters)
        {
            typeMap.TryGetValue(key, out var declaredType);
            args[key] = CoerceArgument(value, declaredType);
        }
        return args;
    }

    internal static object? CoerceArgument(object? value, string? declaredType)
    {
        if (value is JsonElement element)
            return UnwrapJsonElement(element);

        if (value is not string text || declaredType is null)
            return value;

        switch (declaredType)
        {
            case "integer":
                if (long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                    return l;
                if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var integral)
                    && Math.Floor(integral) == integral && Math.Abs(integral) < 9.2e18)
                    return (long)integral;
                return value;
            case "number":
                if (long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole))
                    return whole;
                if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    return d;
                return value;
            case "boolean":
                if (bool.TryParse(text.Trim(), out var b))
                    return b;
                return value;
            case "array":
            case "object":
                try
                {
                    var node = JsonNode.Parse(text);
                    return declaredType == "array" && node is not JsonArray
                        ? value
                        : declaredType == "object" && node is not JsonObject
                            ? value
                            : UnwrapJsonNode(node);
                }
                catch (JsonException)
                {
                    return value;
                }
            default:
                return value;
        }
    }

    internal static object? UnwrapJsonNode(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var s)) return s;
            if (value.TryGetValue<bool>(out var b)) return b;
            if (value.TryGetValue<long>(out var l)) return l;
            if (value.TryGetValue<double>(out var d)) return d;
            return value.ToString();
        }

        if (node is JsonArray array)
            return array.Select(UnwrapJsonNode).ToList();

        if (node is JsonObject obj)
        {
            return obj.ToDictionary(
                kv => kv.Key,
                kv => UnwrapJsonNode(kv.Value),
                StringComparer.Ordinal);
        }

        return node.ToJsonString();
    }

    private static object? UnwrapJsonElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var l)) return l;
                return element.GetDouble();
            default:
                return UnwrapJsonNode(JsonNode.Parse(element.GetRawText()));
        }
    }

    // ── Schema translation ──────────────────────────────────────────

    internal sealed record ParsedSchema(
        List<SkillParameter> Parameters,
        Dictionary<string, string> TypeMap);

    internal static List<SkillParameter> ParseParametersFromSchema(string? inputSchemaJson)
        => ParseSchema(inputSchemaJson).Parameters;

    internal static ParsedSchema ParseSchema(string? inputSchemaJson)
    {
        var parameters = new List<SkillParameter>();
        var typeMap = new Dictionary<string, string>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(inputSchemaJson))
            return new ParsedSchema(parameters, typeMap);

        try
        {
            using var doc = JsonDocument.Parse(inputSchemaJson);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("properties", out var properties)
                || properties.ValueKind != JsonValueKind.Object)
            {
                return new ParsedSchema(parameters, typeMap);
            }

            // JSON Schema property names are case-sensitive; so is "required".
            var required = new HashSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("required", out var requiredArray)
                && requiredArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in requiredArray.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { } name)
                        required.Add(name);
                }
            }

            foreach (var prop in properties.EnumerateObject())
            {
                SkillParameter parameter;
                try
                {
                    parameter = ParseProperty(root, prop.Name, prop.Value, required.Contains(prop.Name));
                }
                catch
                {
                    // One malformed property must not erase the rest of the tool's signature.
                    parameter = new SkillParameter
                    {
                        Name = prop.Name,
                        Type = "string",
                        Description = "",
                        IsRequired = required.Contains(prop.Name),
                    };
                }

                parameters.Add(parameter);
                typeMap[parameter.Name] = parameter.Type;
            }
        }
        catch
        {
            // Schema parsing is best-effort; an unparseable document yields no parameters.
        }

        return new ParsedSchema(parameters, typeMap);
    }

    private static SkillParameter ParseProperty(JsonElement root, string name, JsonElement rawSchema, bool isRequired)
    {
        // A local "$ref" ("#/$defs/Foo") is followed so the referenced shape drives
        // the parameter; an unresolvable one falls back to "string" and names the
        // reference in the description so the model still has a hint.
        var schema = ResolveRef(root, rawSchema);
        // ResolveRef returns the target (no "$ref" left) or the original when it could not follow it.
        var unresolvedRef = TryGetRef(schema, out var pointer) ? pointer : null;

        var type = ResolveType(root, schema);
        var description = new StringBuilder();
        if (unresolvedRef is not null)
            description.Append("Schema reference '").Append(unresolvedRef).Append("' could not be resolved.");

        if (schema.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty("description", out var desc)
            && desc.ValueKind == JsonValueKind.String)
        {
            description.Append(desc.GetString());
        }

        List<string>? validValues = null;
        if (schema.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty("enum", out var enumEl)
            && enumEl.ValueKind == JsonValueKind.Array)
        {
            validValues = enumEl.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.GetRawText())
                .ToList();
        }

        object? defaultValue = null;
        if (schema.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty("default", out var defaultEl))
        {
            defaultValue = UnwrapJsonElement(defaultEl);
        }

        // Nested shapes are flattened to one parameter; carry their structure in
        // the description so the model still knows what to send.
        var structure = DescribeStructure(root, schema, type);
        if (structure is not null)
        {
            if (description.Length > 0) description.Append(' ');
            description.Append(structure);
        }

        return new SkillParameter
        {
            Name = name,
            Type = type,
            Description = McpTextSanitizer.SanitizeDescription(description.ToString()) ?? "",
            IsRequired = isRequired,
            ValidValues = validValues,
            DefaultValue = defaultValue,
        };
    }

    /// <summary>
    /// Follows a local JSON-pointer <c>$ref</c> (<c>#/$defs/Foo</c>,
    /// <c>#/definitions/Foo</c>, …) against <paramref name="root"/>. Returns the
    /// referenced schema, or <paramref name="schema"/> unchanged when there is no
    /// ref, the pointer is not local, the path does not exist, or the chain is
    /// too deep (cycle guard).
    /// </summary>
    internal static JsonElement ResolveRef(JsonElement root, JsonElement schema, int depth = 0)
    {
        if (depth > 8 || !TryGetRef(schema, out var pointer))
            return schema;

        if (!pointer.StartsWith("#/", StringComparison.Ordinal))
            return schema;

        var current = root;
        foreach (var rawSegment in pointer[2..].Split('/'))
        {
            var segment = rawSegment.Replace("~1", "/").Replace("~0", "~");
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next))
                return schema;
            current = next;
        }

        if (current.ValueKind != JsonValueKind.Object)
            return schema;

        var resolved = ResolveRef(root, current, depth + 1);
        // A chain that dead-ends still counts as unresolved for the caller.
        return TryGetRef(resolved, out _) ? schema : resolved;
    }

    private static bool TryGetRef(JsonElement schema, out string pointer)
    {
        pointer = string.Empty;
        if (schema.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty("$ref", out var refEl)
            && refEl.ValueKind == JsonValueKind.String
            && refEl.GetString() is { Length: > 0 } text)
        {
            pointer = text;
            return true;
        }
        return false;
    }

    /// <summary>Resolves the type of a (possibly <c>$ref</c>'d) schema against <paramref name="root"/>.</summary>
    internal static string ResolveType(JsonElement root, JsonElement schema)
        => ResolveType(ResolveRef(root, schema));

    /// <summary>
    /// Resolves a property's JSON Schema type: a plain string, the first non-null
    /// entry of a type array (<c>["string","null"]</c>), or the first typed
    /// branch of <c>anyOf</c>/<c>oneOf</c>. Falls back to "string".
    /// </summary>
    internal static string ResolveType(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            return "string";

        if (schema.TryGetProperty("type", out var typeEl))
        {
            switch (typeEl.ValueKind)
            {
                case JsonValueKind.String:
                    return typeEl.GetString() is { Length: > 0 } t ? t : "string";
                case JsonValueKind.Array:
                    foreach (var candidate in typeEl.EnumerateArray())
                    {
                        if (candidate.ValueKind == JsonValueKind.String
                            && candidate.GetString() is { Length: > 0 } c
                            && !string.Equals(c, "null", StringComparison.Ordinal))
                        {
                            return c;
                        }
                    }
                    break;
            }
        }

        foreach (var combinator in new[] { "anyOf", "oneOf" })
        {
            if (schema.TryGetProperty(combinator, out var branches) && branches.ValueKind == JsonValueKind.Array)
            {
                foreach (var branch in branches.EnumerateArray())
                {
                    if (branch.ValueKind != JsonValueKind.Object) continue;
                    if (branch.TryGetProperty("type", out var bt)
                        && bt.ValueKind == JsonValueKind.String
                        && bt.GetString() is { Length: > 0 } b
                        && !string.Equals(b, "null", StringComparison.Ordinal))
                    {
                        return b;
                    }
                }
            }
        }

        if (schema.TryGetProperty("enum", out _))
            return "string";
        if (schema.TryGetProperty("properties", out _))
            return "object";
        if (schema.TryGetProperty("items", out _))
            return "array";

        return "string";
    }

    private static string? DescribeStructure(JsonElement root, JsonElement schema, string type)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            return null;

        if (type == "object" && schema.TryGetProperty("properties", out var nested) && nested.ValueKind == JsonValueKind.Object)
        {
            var fields = DescribeFields(root, schema, nested);
            return fields.Count == 0
                ? "Object."
                : $"Object with fields: {string.Join(", ", fields)}.";
        }

        if (type == "array" && schema.TryGetProperty("items", out var rawItems))
        {
            var items = ResolveRef(root, rawItems);
            var itemType = ResolveType(items);
            if (itemType == "object" && items.ValueKind == JsonValueKind.Object
                && items.TryGetProperty("properties", out var itemProps) && itemProps.ValueKind == JsonValueKind.Object)
            {
                var fields = DescribeFields(root, items, itemProps);
                return $"Array of objects with fields: {string.Join(", ", fields)}.";
            }
            return $"Array of {itemType}.";
        }

        return null;
    }

    /// <summary>
    /// Renders <c>name (type)</c> for each nested member, marking the ones the
    /// nested object lists in its own <c>required</c> array.
    /// </summary>
    private static List<string> DescribeFields(JsonElement root, JsonElement owner, JsonElement properties)
    {
        var required = new HashSet<string>(StringComparer.Ordinal);
        if (owner.TryGetProperty("required", out var requiredEl) && requiredEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in requiredEl.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } name)
                    required.Add(name);
            }
        }

        return properties.EnumerateObject()
            .Select(p => required.Contains(p.Name)
                ? $"{p.Name} ({ResolveType(root, p.Value)}, required)"
                : $"{p.Name} ({ResolveType(root, p.Value)})")
            .ToList();
    }
}
