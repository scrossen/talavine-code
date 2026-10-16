using System.Text.Json.Nodes;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.ProviderPacks;

namespace SwarmCore.LlmRouting.Providers;

/// <summary>Reviewed model/transport contract. Catalog facts never grant account access or admission.</summary>
public static class OpenAIResponsesContract
{
    public const string ContractId = "openai.responses.astra.v1";
    public static bool IsSelected(string provider, string model) =>
        provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
        && ProviderPackCatalog.TryGetModelFacts(provider, model, out var facts)
        && facts?.ExecutionTransport == "responses";

    public static LlmProviderExecutionTranslation Translate(
        LlmGenerationParameters parameters, LlmRequestControl control)
    {
        var wire = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var diagnostics = new List<LlmExecutionParameterDiagnostic>();
        if (parameters.Reasoning == LlmReasoningMode.Disabled)
            throw new LlmExecutionParameterRejectedException("OpenAI", ContractId,
                [new("reasoning", LlmExecutionParameterDisposition.Rejected, "Astra requires reasoning; select low or higher.")]);
        if (parameters.MaxOutputTokens is { } max) wire["max_output_tokens"] = max;
        if (parameters.Reasoning != LlmReasoningMode.ProviderDefault)
            wire["reasoning_effort"] = parameters.Reasoning.ToString().ToLowerInvariant();
        foreach (var (name, supplied) in new[] { ("temperature", parameters.Temperature is not null),
                     ("top_p", parameters.TopP is not null), ("seed", parameters.Seed is not null),
                     ("keep_alive", parameters.KeepAlive is not null) })
            if (supplied) diagnostics.Add(new(name, LlmExecutionParameterDisposition.Omitted, "Unsupported by the Astra Responses contract."));
        if (parameters.StructuredOutput != LlmStructuredOutputMode.None)
        {
            JsonObject format;
            if (parameters.StructuredOutput == LlmStructuredOutputMode.JsonSchema)
            {
                var schema = JsonNode.Parse(parameters.JsonSchema ?? "null") as JsonObject
                    ?? throw new ArgumentException("JSON-schema mode requires an object schema.");
                format = new() { ["type"] = "json_schema", ["name"] = "response", ["strict"] = true, ["schema"] = schema };
            }
            else format = new() { ["type"] = "json_object" };
            wire["text"] = new JsonObject { ["format"] = format };
        }
        LlmProviderWireControls.Apply(wire, control);
        return new(wire, diagnostics);
    }
}
