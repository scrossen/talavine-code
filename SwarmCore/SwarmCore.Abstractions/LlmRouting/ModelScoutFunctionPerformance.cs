using System.Text.Json.Serialization;

namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>Supplementary function performance, bound to an already admitted model-quality proof.
/// This measurement never creates admission authority or changes a capability floor.</summary>
public sealed record ModelScoutFunctionPerformance(string FunctionType, string Provider, string Model,
    string SuiteSha256, [property: JsonPropertyName("proofIdentitySha256")] string QualificationProofIdentitySha256, decimal Score, int CaseCount,
    DateTime EvaluatedAtUtc, DateTime ExpiresAtUtc)
{
    public bool Matches(ModelScoutRecommendationRequest request, ModelScoutCandidate candidate, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        // Same function lane as every other evidence check: aliases and casing fold together.
        return string.Equals(
                ModelScoutFunctionEvidence.NormalizeFunctionType(FunctionType),
                ModelScoutFunctionEvidence.NormalizeFunctionType(request.FunctionType),
                StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(Provider) && !string.IsNullOrWhiteSpace(Model)
            && Provider.Equals(candidate.Provider, StringComparison.OrdinalIgnoreCase)
            && Model.Equals(candidate.Model, StringComparison.OrdinalIgnoreCase) && Score is >= 0 and <= 1 && CaseCount > 0
            && SuiteSha256 is { Length: 64 } && SuiteSha256.All(Uri.IsHexDigit)
            && EvaluatedAtUtc <= now && ExpiresAtUtc > now && ExpiresAtUtc > EvaluatedAtUtc
            && candidate.FunctionEvidence is { } proof && proof.ExpiresAtUtc > now
            && proof.ProofIdentitySha256 == QualificationProofIdentitySha256;
    }
}

public interface IModelScoutFunctionPerformanceSource
{
    Task<IReadOnlyList<ModelScoutFunctionPerformance>> ReadAsync(string functionType, CancellationToken ct = default);
}
