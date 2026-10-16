using SwarmCore.Abstractions.Context;
namespace SwarmCore.Knowledge;
public static class KnowledgeQueryPlanPolicy
{
    public static ContextQueryPlan Normalize(ContextQueryPlan? requestedPlan, ContextPolicy policy, string? query = null)
    {
        var input = requestedPlan ?? new ContextQueryPlan();
        if (policy.MaxWireTokens is < 128 or > 32000)
            throw new ArgumentOutOfRangeException(nameof(policy), "Wire budget must be between 128 and 32000 estimated tokens.");
        if (input.TemporalIntent is not ("current" or "history"))
            throw new ArgumentException("Unsupported temporal intent.", nameof(requestedPlan));
        if (input.From > input.To) throw new ArgumentException("Invalid retrieval time window.", nameof(requestedPlan));
        var identifiers = input.Identifiers.Concat(System.Text.RegularExpressions.Regex.Matches(query ?? "", @"\b[\p{L}\p{N}][\p{L}\p{N}_-]{2,63}\b")
            .Select(m => m.Value).Where(s => s.Any(char.IsLetter) && s.Any(char.IsDigit)));
        var plan = input with
        {
            QueryFingerprint = query is null ? null : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(query))).ToLowerInvariant(),
            Entities = input.Entities.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).Select(s => s[..Math.Min(s.Length, 128)]).ToArray(),
            Identifiers = identifiers.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).Select(s => s[..Math.Min(s.Length, 128)]).ToArray(),
            RequiredClaimSlots = input.RequiredClaimSlots.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct(StringComparer.Ordinal).Take(8).Select(s => s[..Math.Min(s.Length, 128)]).ToArray(),
            AccountIds = policy.AllowedSourceAccountIds?.Order().ToArray() ?? [],
            MaximumSources = Math.Clamp(policy.MaxSources, 1, 64),
            MaximumTokens = Math.Clamp(policy.MaxTokens, 1, 12000),
            MaximumWireTokens = policy.MaxWireTokens,
            MaximumExpansionRounds = Math.Clamp(input.MaximumExpansionRounds, 0, 1)
        };
        return plan;
    }

}
