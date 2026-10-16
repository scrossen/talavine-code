using SwarmCore.Abstractions.Budgets;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.ProviderPacks;

namespace SwarmCore.LlmRouting;

public partial class LlmRouter
{
    // The holder is created synchronously at each public call boundary. Updating
    // its attempt from an awaited helper therefore survives ExecutionContext restore.
    private static readonly AsyncLocal<PhysicalInvocation?> PhysicalInvocationSlot = new();
    private sealed class PhysicalInvocation : IAsyncDisposable
    {
        private readonly PhysicalInvocation? previous = PhysicalInvocationSlot.Value;
        public readonly MissionBudgetExecutionContext? Budget = MissionBudgetExecutionScope.Current;
        public PhysicalAttempt? Attempt;
        public PhysicalInvocation() => PhysicalInvocationSlot.Value = this;
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (Attempt is { Dispatched: false, Reserved: true } attempt && Budget is { } budget)
                    await budget.Ledger.ReleaseUnusedAsync(budget.ProfileId, budget.Reference, attempt.Id.ToString("N"), CancellationToken.None).ConfigureAwait(false);
            }
            finally { PhysicalInvocationSlot.Value = previous; }
        }
    }
    private sealed class PhysicalAttempt
    {
        public Guid Id { get; } = Guid.NewGuid();
        public bool Reserved;
        public bool Dispatched;
        public decimal? InputPrice;
        public decimal? OutputPrice;
    }

    private async Task ReservePhysicalAttemptAsync(LlmModelSelection model, Dictionary<string, object>? settings, int inputCount, bool embedding)
    {
        var invocation = PhysicalInvocationSlot.Value;
        if (invocation is null)
        {
            if (MissionBudgetExecutionScope.Current is not null)
                throw new MissionBudgetExceededException("Physical call has no isolated invocation context.");
            return;
        }
        if (invocation.Attempt is { Dispatched: false, Reserved: true } unused && invocation.Budget is { } owner)
            await owner.Ledger.ReleaseUnusedAsync(owner.ProfileId, owner.Reference, unused.Id.ToString("N"), CancellationToken.None).ConfigureAwait(false);
        var attempt = new PhysicalAttempt();
        invocation.Attempt = attempt;
        if (invocation.Budget is not { } budget) return;
        ProviderPackCatalog.TryGetModelFacts(model.Provider, model.Model, out var facts);
        // A context ceiling covers multimodal content, tool schemas and retry
        // expansion. Character/token heuristics are not admissible hard bounds.
        long? input = facts?.MaxContextTokens is > 0 ? checked((long)facts.MaxContextTokens.Value * inputCount) : null;
        long? output = embedding ? 0 : settings is not null && TryGetMaxTokens(settings) is > 0 and var cap ? cap : null;
        attempt.InputPrice = facts?.InputCostPer1K;
        attempt.OutputPrice = embedding ? 0 : facts?.OutputCostPer1K;
        // Cache creation can cost more than ordinary input; unknown tariff
        // envelopes must remain unknown instead of borrowing a fallback price.
        decimal? cost = model.Provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
            && input.HasValue && output.HasValue && attempt.InputPrice.HasValue && attempt.OutputPrice.HasValue
            ? (input.Value * attempt.InputPrice.Value + output.Value * attempt.OutputPrice.Value) / 1000m : null;
        await budget.Ledger.ReserveAsync(budget.ProfileId, budget.Reference, budget.MissionId,
            attempt.Id.ToString("N"), new(cost, input, output, ToolCalls: 0, RuntimeMilliseconds: null),
            cost.HasValue ? $"provider-pack:{model.Provider}:{model.Model}:{attempt.InputPrice}:{attempt.OutputPrice}" : null,
            CancellationToken.None).ConfigureAwait(false);
        attempt.Reserved = true;
    }

    private static Guid CurrentPhysicalCallId => PhysicalInvocationSlot.Value?.Attempt?.Id ?? Guid.NewGuid();
    private static bool HasMissionPhysicalBudget => PhysicalInvocationSlot.Value?.Budget is not null;

    private async Task SettlePhysicalAttemptAsync(LlmUsageRecord record, bool durable, bool complete)
    {
        var invocation = PhysicalInvocationSlot.Value;
        if (invocation?.Budget is not { } budget || invocation.Attempt is not { Reserved: true, Dispatched: true } attempt
            || attempt.Id != record.PhysicalCallId) return;
        decimal? cost = record.ActualCost;
        if (!cost.HasValue && attempt.InputPrice.HasValue && attempt.OutputPrice.HasValue
            && record.Provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
            cost = (record.InputTokens * attempt.InputPrice.Value + record.OutputTokens * attempt.OutputPrice.Value) / 1000m;
        await budget.Ledger.SettleAsync(budget.ProfileId, budget.Reference, attempt.Id.ToString("N"),
            new(cost, complete ? record.InputTokens : null, complete ? record.OutputTokens : null, 0, record.ResponseTimeMs),
            durable && complete, record.UsageId?.ToString(), CancellationToken.None).ConfigureAwait(false);
    }
}
