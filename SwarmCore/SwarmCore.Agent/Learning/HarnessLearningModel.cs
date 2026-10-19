using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.HarnessLearning;
using System.Text.Json;

namespace SwarmCore.Agent.Learning;

/// <summary>Ordinary host router, one physical request per completion, pinned route and complete measured usage.
/// No experimental tools, fallback, profile reads, credential copying or model eligibility changes.</summary>
public sealed class HarnessLearningModel(ILlmRouter router, string provider, string model,
    IReadOnlyDictionary<string, object>? settings = null, Func<bool>? isCurrent = null, bool research = false,
    string? contractIdentity = null) : IHarnessLearningModel
{
    public const string Function = FunctionTypes.HarnessLearning;
    private readonly Dictionary<string, object>? _settings = settings?.ToDictionary(p => p.Key, p => p.Value);
    public string Identity { get; } = string.Join('\n', provider, model, typeof(HarnessLearningModel).Assembly.ManifestModule.ModuleVersionId,
        typeof(SwarmCore.LlmRouting.LlmRouter).Assembly.ManifestModule.ModuleVersionId,
        typeof(AutomaticHarnessLearning).Assembly.ManifestModule.ModuleVersionId,
        contractIdentity,
        HarnessEvidence.Hash(JsonSerializer.Serialize(settings?.Where(p => !p.Key.StartsWith("__swarmcore", StringComparison.Ordinal)
            || p.Key == LlmReservedExecutionSettings.ExpectedModelArtifactDigest).OrderBy(p => p.Key, StringComparer.Ordinal))));
    public async Task<LearningModelReply> CompleteAsync(string prompt, CancellationToken ct)
    {
        var receipts = new List<LlmUsageCompletion>();
        var authorizedStart = 0;
        var start = new LlmProviderExecutionStartCallback(() =>
        {
            if (isCurrent?.Invoke() == false) throw new OperationCanceledException("The active learning scope changed.");
            Volatile.Write(ref authorizedStart, 1);
        });
        var options = new LlmRouterOptions
        {
            OverrideProvider = provider, OverrideModel = model, OverrideProvenance = research ? LlmOverrideProvenance.Benchmark : LlmOverrideProvenance.Policy,
            DisableFallback = true, MaxTokens = 512, Temperature = 0,
            Interactivity = LlmInteractivity.Background, StrategyId = StructuredPreparationContract.Version,
            SourceType = research ? "automatic-harness-learning" : "verified-structured-preparation", RequestCancellationToken = ct,
            ExecutionSettings = _settings is null ? null : new(_settings),
            ProviderExecutionStartCallback = start,
            RequestControl = new() { MaxPhysicalRequests = 1, CancellationToken = ct, RequireExactModel = true },
            UsageCompletionObserver = receipt => { lock (receipts) receipts.Add(receipt); return Task.CompletedTask; }
        };
        string text;
        try { text = await router.GenerateResponseAsync(research ? Function : FunctionTypes.AgentChat, prompt, options).ConfigureAwait(false); }
        catch (Exception ex) { throw new LearningModelException(ex.GetType().Name, Volatile.Read(ref authorizedStart) == 1, ex); }
        LlmUsageCompletion[] measured;
        lock (receipts) measured = receipts.ToArray();
        var valid = measured.Length == 1 && measured[0].FunctionType == (research ? Function : FunctionTypes.AgentChat) && measured[0].MeasurementComplete && measured[0].WasSuccessful
            && measured[0].Provider == provider && measured[0].Model == model
            && measured[0].InputTokens >= 0 && measured[0].OutputTokens >= 0;
        return new(text, valid ? (long)measured[0].InputTokens + measured[0].OutputTokens : null,
            valid && measured[0].EstimatedCost > 0 ? measured[0].EstimatedCost : null);
    }
}
