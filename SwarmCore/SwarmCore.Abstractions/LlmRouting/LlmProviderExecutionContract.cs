using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SwarmCore.Abstractions.LlmRouting;

public static class LlmProviderExecutionContractIds
{
    public const string OpenAIChatCompletionsV2 = "openai.chat-completions.semantic.v2";
    public const string OpenRouterChatCompletionsV2 = "openrouter.chat-completions.semantic.v2";
    public const string DeepSeekChatCompletionsV1 = "deepseek.chat-completions.semantic.v1";
    public const string LMStudioChatCompletionsV2 = "lmstudio.chat-completions.semantic.v2";
    public const string OllamaNativeChatV2 = "ollama.native-chat.semantic.v2";
    // v3 adds backend-specific structured JSON for llama.cpp while preserving
    // typed omission on QAIRT. The value change deliberately invalidates v2 proof.
    public const string GenieXNativeV3 = "geniex.native-openai-semantic.v3";

    public static string? ForProvider(string? provider) => provider?.Trim().ToUpperInvariant() switch
    {
        "OPENAI" => OpenAIChatCompletionsV2,
        "OPENROUTER" => OpenRouterChatCompletionsV2,
        "DEEPSEEK" => DeepSeekChatCompletionsV1,
        "LMSTUDIO" or "LM STUDIO" => LMStudioChatCompletionsV2,
        "OLLAMA" => OllamaNativeChatV2,
        "GENIEX" => GenieXNativeV3,
        // An Ollama@node LAN provider runs the same native-chat contract as the
        // local daemon; only the endpoint differs.
        _ when OllamaProviderFamily.IsLanNode(provider) => OllamaNativeChatV2,
        _ => null,
    };
}

public enum LlmReasoningMode
{
    ProviderDefault = 0,
    Disabled = 1,
    Low = 2,
    Medium = 3,
    High = 4,
    XHigh = 5,
    Max = 6,
}

public enum LlmStructuredOutputMode
{
    None = 0,
    JsonObject = 1,
    JsonSchema = 2,
}

public enum LlmExecutionParameterDisposition
{
    Applied = 0,
    Omitted = 1,
    Rejected = 2,
}

public sealed record LlmExecutionParameterDiagnostic(
    string Parameter,
    LlmExecutionParameterDisposition Disposition,
    string Reason);

/// <summary>Provider-neutral generation semantics. No provider payload keys belong here.</summary>
public sealed record LlmGenerationParameters
{
    public int? MaxOutputTokens { get; init; }
    public double? Temperature { get; init; }
    public double? TopP { get; init; }
    public long? Seed { get; init; }
    public LlmReasoningMode Reasoning { get; init; } = LlmReasoningMode.ProviderDefault;
    public LlmStructuredOutputMode StructuredOutput { get; init; }
    public string? JsonSchema { get; init; }
    public bool? Stream { get; init; }
    public TimeSpan? KeepAlive { get; init; }
}

/// <summary>
/// Request controls are intentionally separate from sampling. They govern the
/// physical call and proof/scheduling boundaries and must never become vendor payload.
/// </summary>
public sealed record LlmRequestControl
{
    private LlmPhysicalRequestBudget? _physicalRequestBudget;
    public LlmRequestControl() { }
    private LlmRequestControl(LlmRequestControl original)
    {
        MaxPhysicalRequests = original.MaxPhysicalRequests;
        _physicalRequestBudget = original.PhysicalRequestBudget;
        CancellationToken = original.CancellationToken;
        RequireExactModel = original.RequireExactModel;
        ExpectedArtifactDigest = original.ExpectedArtifactDigest;
        PrivacyClass = original.PrivacyClass;
        Interactivity = original.Interactivity;
        Urgency = original.Urgency;
    }
    /// <summary>Maximum physical inference submissions, including every retry or degradation. Null preserves the provider's normal policy.</summary>
    public int? MaxPhysicalRequests { get; init; }
    public LlmPhysicalRequestBudget? PhysicalRequestBudget
    {
        get
        {
            if (MaxPhysicalRequests is not { } maximum)
            {
                if (Volatile.Read(ref _physicalRequestBudget) is not null)
                    throw new InvalidOperationException("A bounded request budget cannot be removed from a copied control.");
                return null;
            }
            if (maximum < 1) throw new ArgumentOutOfRangeException(nameof(MaxPhysicalRequests));
            var existing = Volatile.Read(ref _physicalRequestBudget);
            if (existing is not null)
            {
                if (existing.MaximumRequests != maximum)
                    throw new InvalidOperationException("A bounded request budget cannot change its limit in a copied control.");
                return existing;
            }
            var created = new LlmPhysicalRequestBudget(maximum);
            return Interlocked.CompareExchange(ref _physicalRequestBudget, created, null) ?? created;
        }
    }
    public CancellationToken CancellationToken { get; init; }
    public bool RequireExactModel { get; init; }
    public string? ExpectedArtifactDigest { get; init; }
    public LlmPrivacyClass PrivacyClass { get; init; } = LlmPrivacyClass.Unspecified;
    public LlmInteractivity Interactivity { get; init; } = LlmInteractivity.Unspecified;
    public LlmUrgency Urgency { get; init; } = LlmUrgency.Unspecified;
}

/// <summary>One request-owned budget shared by adapter retries and router copies. Never serialized into vendor payloads.</summary>
public sealed class LlmPhysicalRequestBudget(int maximumRequests)
{
    private int _consumed;
    public int MaximumRequests { get; } = maximumRequests > 0 ? maximumRequests : throw new ArgumentOutOfRangeException(nameof(maximumRequests));
    public int ConsumedRequests => Volatile.Read(ref _consumed);
    public bool Exhausted => ConsumedRequests >= MaximumRequests;
    public bool TryConsume()
    {
        while (true)
        {
            var before = Volatile.Read(ref _consumed);
            if (before >= MaximumRequests) return false;
            if (Interlocked.CompareExchange(ref _consumed, before + 1, before) == before) return true;
        }
    }
}

public sealed record LlmProviderExecutionCapabilities(
    bool MaxOutputTokens,
    bool Temperature,
    bool TopP,
    bool Seed,
    bool Reasoning,
    bool StructuredOutput,
    bool Streaming,
    bool KeepAlive);

public sealed record LlmProviderExecutionContractDescriptor(
    string ExecutionContractId,
    LlmProviderExecutionCapabilities Capabilities)
{
    public string CanonicalHash => LlmExecutionContractIdentity.Compute(this);
}

public sealed record LlmProviderExecutionTranslation(
    IReadOnlyDictionary<string, object> WireSettings,
    IReadOnlyList<LlmExecutionParameterDiagnostic> Diagnostics)
{
    public bool IsRejected => Diagnostics.Any(d => d.Disposition == LlmExecutionParameterDisposition.Rejected);
}

public sealed class LlmExecutionParameterRejectedException(
    string provider,
    string executionContractId,
    IReadOnlyList<LlmExecutionParameterDiagnostic> diagnostics)
    : InvalidOperationException($"{provider} execution contract {executionContractId} rejected one or more semantic parameters.")
{
    public string Provider { get; } = provider;
    public string ExecutionContractId { get; } = executionContractId;
    public IReadOnlyList<LlmExecutionParameterDiagnostic> Diagnostics { get; } = diagnostics;
}

/// <summary>Implemented by provider adapters; the router never translates wire keys.</summary>
public interface ILlmProviderExecutionContract
{
    LlmProviderExecutionContractDescriptor ExecutionContract { get; }
    LlmProviderExecutionContractDescriptor GetExecutionContract(string model) => ExecutionContract;

    LlmProviderExecutionTranslation TranslateExecution(
        string model,
        LlmGenerationParameters parameters,
        LlmRequestControl control,
        IReadOnlyDictionary<string, object>? providerExperimentalSettings = null);
}

public static class LlmExecutionContractIdentity
{
    public static string Compute(LlmProviderExecutionContractDescriptor descriptor)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            id = descriptor.ExecutionContractId.Trim(),
            capabilities = descriptor.Capabilities,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
