using System.Text.Json.Nodes;

namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>Host protocol binding. Opaque items are never interpretable user evidence.</summary>
public sealed record ProviderContextBinding(string Provider, string Model, Guid ProfileId, Guid? MissionId,
    string AuthorityDigest, string RetentionPolicyVersion, DateTimeOffset ExpiresAtUtc)
{
    public string? ConversationId { get; init; }
    public string? Endpoint { get; init; }
    public string? CredentialReference { get; init; }
    public bool IsCompatibleWith(ProviderContextBinding current, DateTimeOffset now)
        => ExpiresAtUtc > now && !string.IsNullOrWhiteSpace(CredentialReference)
            && CredentialReference == current.CredentialReference && !string.IsNullOrWhiteSpace(Endpoint)
            && Endpoint == current.Endpoint && Provider.Equals(current.Provider, StringComparison.OrdinalIgnoreCase)
            && ConversationId == current.ConversationId && Model == current.Model && ProfileId == current.ProfileId && MissionId == current.MissionId
            && AuthorityDigest == current.AuthorityDigest && RetentionPolicyVersion == current.RetentionPolicyVersion;
}

/// <summary>The complete canonical output of /responses/compact, retained without pruning.</summary>
public sealed record ResponsesCompactionPart(JsonArray Items, ProviderContextBinding Binding) : ChatContentPart
{
    public override string ToString() => "ResponsesCompactionPart(opaque)";
}

public sealed record ProviderContextCapability(bool Supported, string Mode, string Reason);
public static class ProviderContextCapabilities
{
    public static ProviderContextCapability StandaloneCompaction(string provider, string model,
        bool activeSteering, bool pendingTools, bool configurationUpdates, bool multiAgent = false)
    {
        if (!provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) || model != "gpt-6-astra")
            return new(false, "application-checkpoint", "No reviewed native compaction contract for this exact route.");
        if (activeSteering || pendingTools || configurationUpdates || multiAgent)
            return new(false, "application-checkpoint", "Retain active steering/tool/configuration lineage; standalone compaction requires a quiescent compatible boundary.");
        return new(true, "responses-standalone-compact", "Documented stateless canonical compaction; account execution access is checked separately.");
    }
}

public sealed class ProviderContextReconstructionRequiredException(string reason) : InvalidOperationException(reason);

/// <summary>Complete ordered response output, including phase and provider item IDs.
/// Host text/tool parts accompany this item; the native adapter emits this array exactly once.</summary>
public sealed record ResponsesOutputPart(JsonArray Items, ProviderContextBinding Binding) : ChatContentPart
{
    public override string ToString() => "ResponsesOutputPart(protected protocol)";
}
