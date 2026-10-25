using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting;

public sealed record LlmInputRetryRequest(string Function, string Prompt, string SourceHash, LlmRouterOptions Options)
{
    // Source text and credentials are never persisted in a retry receipt.
    public string Identity => Hash(new
    {
        Schema = "llm-input-retry-v1", Function, Prompt, SourceHash,
        Options.PrivacyClass, Options.OverrideProvider, Options.OverrideModel,
        Options.RequiresVision, Options.Interactivity, Options.GenerationParameters,
        Options.ModelRequest, Options.StrategyId, Options.OverrideProvenance,
        Options.MaxTokens, Options.SuppressOutputTokenLimit, Options.Temperature,
        Options.ExpectedFormat, Options.ForceJson, Options.SuppressStructuredOutputRetry,
        Options.RequiresToolUse, Options.RequiresStructuredOutput, Options.AllowTextJsonContractRepair,
        Options.RequireCurrentWorkloadSizing, Options.MinimumContextTokens,
        Options.DisableFallback, Options.Workload, Options.Urgency,
        Options.MinimumCapabilityScore, Options.RequireMeasuredCapabilityScore, Options.RequiredTier,
        Options.BlockWeakerPolicyFallback, Options.RequireCurrentFunctionEvidence,
        Settings = Options.ExecutionSettings?.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray()
    });

    public static string Hash(object value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
}

public sealed record LlmInputRetryRoute(string Provider, string Model, string EvidenceIdentity)
{
    public string Key => RouteKey(Provider, Model);
    public static string RouteKey(string provider, string model) =>
        LlmInputRetryRequest.Hash(new[] { provider.Trim().ToUpperInvariant(), model.Trim() });
}

/// <summary>A fresh, complete view of admission, configuration and runtime for the selected routes.</summary>
public sealed record LlmInputRetrySnapshot(IReadOnlyList<LlmInputRetryRoute> Routes, string AdmissionIdentity)
{
    public bool IsComplete => Routes.Count != 0 && !string.IsNullOrWhiteSpace(AdmissionIdentity)
        && Routes.All(r => !string.IsNullOrWhiteSpace(r.Provider) && !string.IsNullOrWhiteSpace(r.Model)
            && !string.IsNullOrWhiteSpace(r.EvidenceIdentity));
    public string Identity => LlmInputRetryRequest.Hash(new { Routes, AdmissionIdentity });
}

public interface ILlmInputRetryEvidence
{
    Task<LlmInputRetrySnapshot?> CaptureAsync(LlmInputRetryRequest request, CancellationToken ct);
}

public interface ILlmInputRetryStore
{
    string? Read(string requestIdentity);
    void Write(string requestIdentity, string? blockedEvidenceIdentity);
}

public sealed record LlmInputRetryResult(string? Response, bool WasAttempted, bool AwaitingChangedEvidence, Exception? Error = null);

/// <summary>
/// Defers a rejected input while its exact route evidence is unchanged. Transient
/// failures remain the router's health/capacity responsibility. No attempt count
/// authorizes dropping work, and an untried route never inherits another's failure.
/// Hosts provide current evidence and profile-owned persistence.
/// </summary>
public sealed class LlmInputRetryExecutor(ILlmInputRetryEvidence evidence, ILlmInputRetryStore store)
{
    public async Task<LlmInputRetryResult> ExecuteAsync(
        LlmInputRetryRequest request, Func<Task<string>> generate, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestIdentity = request.Identity;
        var before = await CaptureAsync(request, ct).ConfigureAwait(false);
        var blocked = store.Read(requestIdentity);
        if (!string.IsNullOrWhiteSpace(blocked) && (before is null || before.Identity == blocked))
            return new(null, false, true);

        try
        {
            ct.ThrowIfCancellationRequested();
            var response = await generate().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(response)) store.Write(requestIdentity, null);
            return new(response, true, false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var awaiting = false;
            if (before is not null && CoversRejectedRoutes(request.Function, before, ex))
            {
                var after = await CaptureAsync(request, ct).ConfigureAwait(false);
                if (after?.Identity == before.Identity)
                {
                    store.Write(requestIdentity, before.Identity);
                    awaiting = true;
                }
            }
            ct.ThrowIfCancellationRequested();
            return new(null, true, awaiting, ex);
        }
    }

    private async Task<LlmInputRetrySnapshot?> CaptureAsync(LlmInputRetryRequest request, CancellationToken ct)
    {
        try
        {
            var snapshot = await evidence.CaptureAsync(request, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return snapshot?.IsComplete == true ? snapshot : null;
        }
        catch (OperationCanceledException) { throw; }
        // Missing metadata cannot manufacture evidence that an input is permanently bad.
        catch { return null; }
    }

    private static bool CoversRejectedRoutes(string function, LlmInputRetrySnapshot snapshot, Exception error)
    {
        if (error is not LlmFailoverException failed || !string.Equals(function, failed.FunctionType, StringComparison.Ordinal)) return false;
        var attempts = failed.Attempts.Count > 0
            ? failed.Attempts.Select(a => (a.Provider, a.Model, a.Category, a.WasExecuted)).ToArray()
            : new[]
            {
                (failed.Primary.Provider, failed.Primary.Model, failed.PrimaryCategory, true),
                (failed.Fallback.Provider, failed.Fallback.Model, failed.FallbackCategory, true)
            };
        if (attempts.Any(a => !a.Item4 || a.Item3 is not (LlmFailureCategory.ContentPolicy or LlmFailureCategory.InvalidRequest or LlmFailureCategory.ContextOverflow)))
            return false;
        var rejected = attempts.Select(a => LlmInputRetryRoute.RouteKey(a.Item1, a.Item2)).ToHashSet();
        return snapshot.Routes.All(r => rejected.Contains(r.Key));
    }
}
