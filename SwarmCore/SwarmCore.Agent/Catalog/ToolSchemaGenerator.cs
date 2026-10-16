using System.Collections;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SwarmCore.Agent.Catalog;

/// <summary>
/// Generates provider-agnostic JSON Schema (OpenAPI 3.0 Schema subset)
/// describing a tool's C# input type. Emitted schemas feed Anthropic's
/// <c>input_schema</c>, OpenAI's <c>parameters</c>, and Gemini's
/// <c>functionDeclarations.parameters</c> verbatim.
/// <para>
/// We deliberately stay within the lowest common denominator: only
/// <c>type, properties, required, items, enum, description, format:"date-time"</c>.
/// No <c>additionalProperties</c>, no <c>$schema</c>, no string formats other
/// than <c>date-time</c>. Anything outside that subset gets rejected by at
/// least one provider, and none of it is load-bearing — descriptions carry
/// the semantic hints models actually use.
/// </para>
/// <para>
/// This is a deliberately minimal, reflection-based walker. It handles the
/// shapes used by tool inputs in this codebase: primitives, nullable value
/// types, enums (by name), strings, Guid, DateTime/DateTimeOffset, DateOnly/TimeOnly, arrays,
/// List&lt;T&gt; / IReadOnlyList&lt;T&gt; / IEnumerable&lt;T&gt;, Dictionary&lt;string, T&gt;,
/// and POCOs with public read/write properties. Types the walker can't
/// describe produce a permissive <c>{ "type": "object" }</c> schema rather
/// than throwing — registration should fail loudly elsewhere if this matters.
/// </para>
/// </summary>
public static class ToolSchemaGenerator
{
    /// <summary>
    /// Generate a JSON Schema object for the given CLR type. Returns
    /// <c>{ "type": "object", "properties": {} }</c> when the type is
    /// <c>typeof(object)</c> or null.
    /// </summary>
    public static JsonObject Generate(Type inputType)
    {
        ArgumentNullException.ThrowIfNull(inputType);
        var visited = new HashSet<Type>();
        return BuildSchema(inputType, visited);
    }

    private static JsonObject BuildSchema(Type type, HashSet<Type> visited)
    {
        // Unwrap Nullable<T>.
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
        {
            var inner = BuildSchema(underlying, visited);
            // Nullability is represented at the parent object's "required" list,
            // not by unioning the type here. Most providers reject type unions.
            return inner;
        }

        // Primitives and well-known simple types.
        if (TryBuildPrimitive(type, out var primitive))
        {
            return primitive!;
        }

        // Enum — emit as string with enum values (names).
        if (type.IsEnum)
        {
            var names = Enum.GetNames(type);
            if (type.IsDefined(typeof(FlagsAttribute), inherit: false))
            {
                // JsonStringEnumConverter accepts comma-separated flag combinations.
                // Restricting this to individual enum names makes combined authority
                // (for example Inspect, Modify) impossible to express on the wire.
                return new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "One flag name or multiple flag names separated by commas. Available names: "
                        + string.Join(", ", names) + "."
                };
            }
            var enumArray = new JsonArray();
            foreach (var name in names) enumArray.Add(name);
            return new JsonObject
            {
                ["type"] = "string",
                ["enum"] = enumArray
            };
        }

        // Array or enumerable.
        if (TryGetElementType(type, out var elementType))
        {
            return new JsonObject
            {
                ["type"] = "array",
                ["items"] = BuildSchema(elementType!, visited)
            };
        }

        // Dictionary<string, T> → opaque object. JSON Schema's
        // additionalProperties would carry the value type, but Gemini's
        // Schema subset rejects that field, and neither Anthropic nor OpenAI
        // act on it for tool calls. Callers that need the value shape should
        // put it in the property's [Description].
        if (TryGetDictionaryValueType(type, out _))
        {
            return new JsonObject
            {
                ["type"] = "object"
            };
        }

        // POCO — enumerate public properties.
        if (type.IsClass || type.IsValueType)
        {
            // Guard against self-referential types.
            if (!visited.Add(type))
            {
                return new JsonObject { ["type"] = "object" };
            }
            try
            {
                var properties = new JsonObject();
                var required = new JsonArray();
                var constructorParameters = DeserializationConstructorParameters(type);

                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (prop.GetCustomAttribute<JsonIgnoreAttribute>() is not null) continue;
                    if (prop.GetIndexParameters().Length > 0) continue;
                    if (!prop.CanRead) continue;
                    // Computed get-only members are never read from tool input, so
                    // advertising them would ask the model for ignored (often required) data.
                    if (!IsDeserializedInput(prop, constructorParameters)) continue;

                    var name = ResolvePropertyName(prop);
                    var schema = BuildSchema(prop.PropertyType, visited);

                    // Attach description from [System.ComponentModel.DescriptionAttribute] if present.
                    var description = prop
                        .GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description;
                    if (!string.IsNullOrWhiteSpace(description))
                    {
                        var typeDescription = (string?)schema["description"];
                        schema["description"] = string.IsNullOrWhiteSpace(typeDescription)
                            ? description : description + " " + typeDescription;
                    }

                    properties[name] = schema;

                    if (IsRequired(prop))
                    {
                        required.Add(name);
                    }
                }

                var obj = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = properties
                };
                if (required.Count > 0)
                {
                    obj["required"] = required;
                }
                return obj;
            }
            finally
            {
                visited.Remove(type);
            }
        }

        // Unknown — permissive.
        return new JsonObject { ["type"] = "object" };
    }

    private static bool TryBuildPrimitive(Type type, out JsonObject? schema)
    {
        if (type == typeof(string))
        {
            schema = new JsonObject { ["type"] = "string" };
            return true;
        }
        if (type == typeof(bool))
        {
            schema = new JsonObject { ["type"] = "boolean" };
            return true;
        }
        if (type == typeof(int) || type == typeof(short) || type == typeof(long) || type == typeof(byte) || type == typeof(sbyte) || type == typeof(ushort) || type == typeof(uint) || type == typeof(ulong))
        {
            schema = new JsonObject { ["type"] = "integer" };
            return true;
        }
        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
        {
            schema = new JsonObject { ["type"] = "number" };
            return true;
        }
        // Guid / TimeSpan / Uri / byte[] all serialize as strings. We deliberately
        // omit JSON Schema format hints ("uuid", "duration", "uri") and
        // contentEncoding here — Gemini's Schema subset rejects them and no
        // provider acts on them for tool calls. date-time is the only string
        // format Gemini accepts, so DateTime keeps it.
        if (type == typeof(Guid))
        {
            schema = new JsonObject { ["type"] = "string" };
            return true;
        }
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
        {
            schema = new JsonObject { ["type"] = "string", ["format"] = "date-time" };
            return true;
        }
        if (type == typeof(TimeSpan))
        {
            schema = new JsonObject { ["type"] = "string" };
            return true;
        }
        // System.Text.Json reads DateOnly/TimeOnly only from ISO strings. Gemini rejects
        // every string format except date-time, so the expected shape goes in the description.
        if (type == typeof(DateOnly))
        {
            schema = new JsonObject { ["type"] = "string", ["description"] = "Calendar date as YYYY-MM-DD." };
            return true;
        }
        if (type == typeof(TimeOnly))
        {
            schema = new JsonObject { ["type"] = "string", ["description"] = "Time of day as HH:mm:ss (24-hour)." };
            return true;
        }
        if (type == typeof(Uri))
        {
            schema = new JsonObject { ["type"] = "string" };
            return true;
        }
        if (type == typeof(byte[]))
        {
            schema = new JsonObject { ["type"] = "string" };
            return true;
        }
        if (type == typeof(object))
        {
            schema = new JsonObject { ["type"] = "object" };
            return true;
        }
        schema = null;
        return false;
    }

    private static bool TryGetElementType(Type type, out Type? elementType)
    {
        if (type.IsArray)
        {
            elementType = type.GetElementType();
            return elementType is not null;
        }

        if (type == typeof(string))
        {
            elementType = null;
            return false;
        }

        // Dictionary is also IEnumerable<KVP>; handle dictionary first elsewhere.
        if (TryGetDictionaryValueType(type, out _))
        {
            elementType = null;
            return false;
        }

        if (type.IsGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            if (def == typeof(List<>) || def == typeof(IList<>) || def == typeof(IEnumerable<>) ||
                def == typeof(IReadOnlyList<>) || def == typeof(ICollection<>) ||
                def == typeof(IReadOnlyCollection<>) || def == typeof(HashSet<>))
            {
                elementType = type.GetGenericArguments()[0];
                return true;
            }
        }

        // Non-generic IEnumerable → treat as array of object.
        if (typeof(IEnumerable).IsAssignableFrom(type))
        {
            elementType = typeof(object);
            return true;
        }

        elementType = null;
        return false;
    }

    private static bool TryGetDictionaryValueType(Type type, out Type? valueType)
    {
        if (!type.IsGenericType)
        {
            valueType = null;
            return false;
        }
        var def = type.GetGenericTypeDefinition();
        if (def == typeof(Dictionary<,>) || def == typeof(IDictionary<,>) || def == typeof(IReadOnlyDictionary<,>))
        {
            var args = type.GetGenericArguments();
            if (args[0] == typeof(string))
            {
                valueType = args[1];
                return true;
            }
        }
        valueType = null;
        return false;
    }

    /// <summary>
    /// Parameter names of the constructor System.Text.Json binds when deserializing
    /// <paramref name="type"/>: a [JsonConstructor], otherwise the single public constructor
    /// when no public parameterless one exists (positional records).
    /// </summary>
    private static HashSet<string> DeserializationConstructorParameters(Type type)
    {
        var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var constructor = constructors.FirstOrDefault(c => c.IsDefined(typeof(JsonConstructorAttribute), inherit: false));
        if (constructor is null)
        {
            var publicConstructors = constructors.Where(c => c.IsPublic).ToArray();
            if (publicConstructors.Length == 1 && publicConstructors[0].GetParameters().Length > 0)
                constructor = publicConstructors[0];
        }
        return constructor is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : constructor.GetParameters().Select(p => p.Name ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsDeserializedInput(PropertyInfo prop, HashSet<string> constructorParameters)
        => prop.SetMethod?.IsPublic == true
           || prop.IsDefined(typeof(JsonIncludeAttribute), inherit: true)
           || constructorParameters.Contains(prop.Name);

    private static string ResolvePropertyName(PropertyInfo prop)
    {
        var attr = prop.GetCustomAttribute<JsonPropertyNameAttribute>();
        if (attr is not null) return attr.Name;
        // Default: camelCase first letter to match typical provider expectations.
        var name = prop.Name;
        if (name.Length == 0) return name;
        if (char.IsUpper(name[0]))
        {
            return char.ToLowerInvariant(name[0]) + name[1..];
        }
        return name;
    }

    private static bool IsRequired(PropertyInfo prop)
    {
        // Non-nullable reference or value types are required by default.
        var propType = prop.PropertyType;
        if (Nullable.GetUnderlyingType(propType) is not null) return false;

        if (propType.IsValueType) return true;

        // Reference types: use the [Required] attribute when declared, otherwise
        // inspect the NullableContextAttribute / NullableAttribute encoding.
        if (prop.GetCustomAttribute<System.ComponentModel.DataAnnotations.RequiredAttribute>() is not null)
        {
            return true;
        }

        // Best-effort nullability read. If we can't tell, default to not-required
        // so providers don't fail on legitimately optional inputs.
        var nullabilityContext = new NullabilityInfoContext();
        try
        {
            var info = nullabilityContext.Create(prop);
            return info.WriteState == NullabilityState.NotNull || info.ReadState == NullabilityState.NotNull;
        }
        catch
        {
            return false;
        }
    }
}
