using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCode.Ui;

/// <summary>Probes every registered provider and prints availability plus a sample of models.</summary>
public static class ModelLister
{
    public static async Task PrintAsync(ILlmProviderRegistry registry, CancellationToken ct)
    {
        var providers = registry.GetAllProviders().OrderBy(p => p.ProviderName, StringComparer.OrdinalIgnoreCase).ToList();
        var probes = providers.Select(p => ProbeAsync(p, ct)).ToList();
        var results = await Task.WhenAll(probes).ConfigureAwait(false);

        foreach (var result in results)
        {
            if (result.Healthy)
            {
                Console.WriteLine($"{result.Provider,-14} available   {result.Models.Count} model(s)");
                foreach (var model in result.Models.Take(10)) Console.WriteLine($"    {model}");
                if (result.Models.Count > 10) Console.WriteLine($"    ... {result.Models.Count - 10} more");
            }
            else
            {
                Console.WriteLine($"{result.Provider,-14} unavailable {Short(result.Message)}");
            }
        }
    }

    private sealed record Probe(string Provider, bool Healthy, string Message, List<string> Models);

    private static async Task<Probe> ProbeAsync(ILlmProvider provider, CancellationToken ct)
    {
        try
        {
            var work = Task.Run(async () =>
            {
                var (healthy, message) = await provider.TestConnectivityAsync().ConfigureAwait(false);
                var models = healthy ? await provider.GetAvailableModelsAsync().ConfigureAwait(false) : [];
                return new Probe(provider.ProviderName, healthy, message, models);
            }, ct);
            var finished = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(20), ct)).ConfigureAwait(false);
            return finished == work
                ? await work.ConfigureAwait(false)
                : new Probe(provider.ProviderName, false, "timed out", []);
        }
        catch (OperationCanceledException)
        {
            return new Probe(provider.ProviderName, false, "cancelled", []);
        }
        catch (Exception ex)
        {
            return new Probe(provider.ProviderName, false, ex.Message, []);
        }
    }

    private static string Short(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "";
        var line = message.ReplaceLineEndings(" ");
        return line.Length <= 120 ? line : line[..120] + "…";
    }
}
