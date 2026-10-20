using Microsoft.Extensions.DependencyInjection;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.Catalog;
using SwarmCore.LlmRouting.LocalRuntime;
using SwarmCore.LlmRouting.Services;

namespace SwarmCore.Coding.Tools;

/// <summary>Fresh diagnostics and host-verified synthetic tests, shared by API and native lanes.</summary>
public sealed class ModelCapabilityTools(IServiceProvider services)
{
    public sealed record Input(string Provider, string Model);
    public IReadOnlyList<ToolDescriptor> Descriptors =>
    [
        ToolDescriptor.Create<Input, VisionRepairResult>(ModelVisionRecovery.ToolId, "coding",
            "Discover a matching vision projector using every installed weight hash and a pinned publisher revision, install a recoverable candidate, test two real images, and activate only after proof. Original model settings are preserved. Local on-device Ollama only; ambiguous provenance fails without replacing the model.",
            ToolCostClass.External, ToolSideEffect.Exec, RepairAsync),
        ToolDescriptor.Create<Input, LlmModelCapabilityEvidence?>("models.inspect", "coding",
            "Inspect fresh exact-model runtime capability metadata and artifact identity. Metadata is a declaration, not a functional test. Use this when a preferred model lacks a requested capability or after repairing its components.",
            ToolCostClass.Cheap, ToolSideEffect.ReadOnly, InspectAsync),
        ToolDescriptor.Create<Input, LocalVisionProbe>("models.test_vision", "coding",
            "Test an installed on-device Ollama model with two synthetic images whose visual layouts are reversed, using the shared runtime lease. No private image is sent. This proves actual image handling rather than a metadata flag. Use after a matching-component repair; it does not grant general model quality clearance.",
            ToolCostClass.External, ToolSideEffect.Exec, TestAsync),
    ];

    private Task<VisionRepairResult> RepairAsync(Input input, CancellationToken ct)
    {
        var profile = services.GetService<SwarmCore.Agent.Runtime.ICurrentTurnContextAccessor>()?.Current?.ProfileId
            ?? throw new InvalidOperationException("A host-owned profile scope is required for model installation.");
        return services.GetRequiredService<ModelVisionRecovery>().RepairAsync(input.Provider, input.Model, profile.ToString("N"), ct);
    }

    private async Task<LlmModelCapabilityEvidence?> InspectAsync(Input input, CancellationToken ct)
    {
        var provider = services.GetRequiredService<ILlmProviderRegistry>().GetProvider(input.Provider)
            ?? throw new InvalidOperationException("Choose a registered provider.");
        return await LlmModelCapabilityEvidenceResolver.ResolveAsync(provider, input.Model, ct).ConfigureAwait(false);
    }

    private async Task<LocalVisionProbe> TestAsync(Input input, CancellationToken ct)
    {
        var evidence = await InspectAsync(input, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Fresh exact-model metadata is unavailable; no image was sent.");
        if (evidence.Transport != "ollama-native-v1" || !new Uri(evidence.Endpoint).IsLoopback)
            throw new InvalidOperationException("This image probe supports the local Ollama native protocol.");
        var client = new OllamaSetupClient(services.GetRequiredService<IHttpClientFactory>());
        return await client.TestVisionAsync(evidence.Endpoint, input.Model,
            services.GetRequiredService<ILocalLlmRuntimeManager>(), ct).ConfigureAwait(false);
    }
}
