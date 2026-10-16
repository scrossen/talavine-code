using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace SwarmCore.Agent.Catalog;

public sealed record ToolDispatchDescriptorEvidence(string ContractJson,string InvokeStamp,string EffectResolverStamp);
public sealed record ToolDispatchPlan(string RawName,string? CanonicalName,string InputSha256,string? DescriptorSha256,
    ToolSideEffect? SideEffect,bool InputValidated,string Outcome, bool RequiredInputsChecked = false,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] ToolDispatchDescriptorEvidence? DescriptorEvidence=null,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? TypedInputSha256=null);

/// <summary>Pure canonical resolution and declared input checks. A plan is evidence metadata, never an invocation grant.</summary>
public static class ToolDispatchPolicy
{
    public static ToolDescriptor? Resolve(IEnumerable<ToolDescriptor> tools,string name)
    {
        var candidates=tools.ToArray();var exact=candidates.Where(t=>t.ToolId.Equals(name,StringComparison.OrdinalIgnoreCase)).ToArray();
        if(exact.Length!=0)return exact.Length==1?exact[0]:null;
        var alias=candidates.Where(t=>t.ToolId.Replace('.','_').Equals(name,StringComparison.OrdinalIgnoreCase)).ToArray();
        return alias.Length==1?alias[0]:null;
    }
    public static object DeserializeValidated(JsonObject input,ToolDescriptor descriptor,JsonSerializerOptions options, bool requireDeclaredInputs = true)
    {
        if (requireDeclaredInputs) ValidateRequired(input,descriptor.InputSchema,options.PropertyNameCaseInsensitive,"",null);
        var typed=descriptor.InputType==typeof(JsonObject)?input.DeepClone():JsonSerializer.Deserialize(input.ToJsonString(options),descriptor.InputType,options)
            ??throw new JsonException("Tool input has no typed value.");
        var errors=new List<ValidationResult>();
        if(!Validator.TryValidateObject(typed,new ValidationContext(typed),errors,true))throw new JsonException("Tool arguments violate declared validation constraints.");
        return typed;
    }
    // The message names the argument's full path. When the failing member sits inside an optional object, the
    // model is told to omit that object: a small worker otherwise fills every schema property with null and
    // concludes from a bare member name that the call can never be made.
    private static void ValidateRequired(JsonNode? input,JsonNode? schema,bool ignoreCase,string path,string? optionalAncestor)
    {
        if(schema is not JsonObject shape)return;
        if(input is JsonObject obj)
        {
            if(shape["required"] is JsonArray required)
                foreach(var name in required){var key=name?.GetValue<string>()??throw new JsonException("Invalid tool required-input metadata.");
                    var matches=obj.Where(p=>p.Key.Equals(key,ignoreCase?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal)).ToArray();
                    if(matches.Length!=1||matches[0].Value is null)throw new JsonException($"Required tool argument '{path}{key}' is missing, ambiguous or null."
                        +(optionalAncestor is null?"":$" The enclosing '{optionalAncestor}' argument is optional: omit it entirely unless every value inside it is known."));}
            if(shape["properties"] is JsonObject props)
                foreach(var pair in obj)
                {
                    var known=props.Where(p=>p.Key.Equals(pair.Key,ignoreCase?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal)).ToArray();
                    if(known.Length!=1)continue;
                    var isRequired=shape["required"] is JsonArray r&&r.Any(n=>n?.GetValue<string>() is {} rn&&rn.Equals(known[0].Key,StringComparison.Ordinal));
                    ValidateRequired(pair.Value,known[0].Value,ignoreCase,path+known[0].Key+".",optionalAncestor??(isRequired?null:path+known[0].Key));
                }
        }
        else if(input is JsonArray items&&shape["items"] is {} itemSchema)foreach(var item in items)ValidateRequired(item,itemSchema,ignoreCase,path+"[].",optionalAncestor);
    }
    public static ToolDispatchPlan Capture(string rawName,JsonObject input,ToolDescriptor? descriptor,JsonSerializerOptions options, bool requireDeclaredInputs = true)
    {
        // Retained Started.Input uses the default JsonNode wire representation.
        var sha=Hash(input.ToJsonString());
        if(descriptor is null)return new(rawName,null,sha,null,null,false,"unresolved-tool");
        var evidence=Evidence(descriptor);var fingerprint=Fingerprint(evidence);
        try{var typed=DeserializeValidated(input,descriptor,options, requireDeclaredInputs);return new(rawName,descriptor.ToolId,sha,fingerprint,descriptor.EffectiveSideEffect(typed),true,"validated-input",requireDeclaredInputs,evidence,TypedInputHash(descriptor,typed,options));}
        catch(Exception e)when(e is JsonException or ArgumentException or InvalidOperationException or ValidationException)
        {return new(rawName,descriptor.ToolId,sha,fingerprint,null,false,"invalid-input",requireDeclaredInputs,evidence);}
    }
    public static string Fingerprint(ToolDescriptor tool)
        => Fingerprint(Evidence(tool));
    public static string TypedInputHash(ToolDescriptor tool,object input,JsonSerializerOptions options)
        => Hash(JsonSerializer.Serialize(input,tool.InputType,options));
    public static string Fingerprint(ToolDispatchDescriptorEvidence evidence)
        => Hash(Hash(evidence.ContractJson).ToLowerInvariant()+"|"+evidence.InvokeStamp+"|"+evidence.EffectResolverStamp);
    public static ToolDispatchDescriptorEvidence Evidence(ToolDescriptor tool)
    {
        string Stamp(Delegate? method)=>method is null?"none":method.Method.Module.ModuleVersionId+":"+method.Method.MetadataToken;
        return new(AdmittedCapabilityCatalog.DescriptorContractJson(tool),Stamp(tool.Invoke),Stamp(tool.ResolveSideEffect));
    }
    public static string CorrelatedResult(string callId, string toolId, string result, bool isError)
    {
        JsonNode? content;
        try { content = JsonNode.Parse(result); } catch (JsonException) { content = JsonValue.Create(result); }
        return new JsonObject { ["toolCallId"] = callId, ["canonicalToolId"] = toolId,
            ["sourceReceiptId"] = "tool:" + callId, ["isError"] = isError, ["result"] = content }.ToJsonString();
    }
    public static string Hash(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
