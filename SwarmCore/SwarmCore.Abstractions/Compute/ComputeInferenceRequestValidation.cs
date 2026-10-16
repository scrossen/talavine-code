using System.Text;

namespace SwarmCore.Abstractions.Compute;

/// <summary>Version-one wire limits, shared by coordinator and receiver. No paid API fallback.</summary>
public static class ComputeInferenceRequestValidation
{
    public static void Validate(ComputeInferenceRequest request, DateTime nowUtc)
    {
        if (request.Id == Guid.Empty || string.IsNullOrWhiteSpace(request.JobId) || request.JobId.Length > 128
            || string.IsNullOrWhiteSpace(request.FunctionType) || request.FunctionType.Length > 128
            || string.IsNullOrWhiteSpace(request.Prompt)
            || Encoding.UTF8.GetByteCount(request.Prompt) + Encoding.UTF8.GetByteCount(request.SystemPrompt ?? "") > 128 * 1024
            || request.MaxTokens is < 1 or > 16384 || request.Temperature is < 0 or > 2
            || (request.Temperature.HasValue && !double.IsFinite(request.Temperature.Value))
            || request.ExpiresUtc <= nowUtc || request.ExpiresUtc > nowUtc.AddDays(7)
            || request.MaxApiCostUsd != 0 || (request.LocalOnly && request.AllowSubscription)
            || (request.ProviderName is null) != (request.Model is null)
            || request.ProviderName is { Length: 0 or > 128 } || request.Model is { Length: 0 or > 256 }
            || (request.ProviderName is not null && (string.IsNullOrWhiteSpace(request.ProviderName) || string.IsNullOrWhiteSpace(request.Model))))
            throw new ArgumentException("Invalid bounded inference request; paid API fallback is not enabled.", nameof(request));
    }
}
