using System.Text.Json;
using SwarmCore.Abstractions.CodingTasks;

namespace SwarmCore.Abstractions.Security;

/// <summary>Bounded tool presentation only; persisted evidence remains unchanged.</summary>
public static class SecurityReportProjection
{
    public const int MaximumCharacters = 6000;
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    public static SecurityReport Fit(SecurityReport original, Func<SecurityReport, int>? measure = null)
    {
        measure ??= report => JsonSerializer.Serialize(report, WebJson).Length;
        var value = original with { Repair = original.Repair is null ? null : CodingTaskResultProjection.Fit(original.Repair) };
        value = value with { Truncated = value.Truncated || value.Repair?.Truncated == true };
        if (measure(value) <= MaximumCharacters) return value;
        value = value with { Truncated = true, Commands = value.Commands.Select(c => c with { Stdout = "", Stderr = "" }).ToArray() };
        while (measure(value) > MaximumCharacters)
        {
            if (value.Commands.Count > 0) value = value with { Commands = value.Commands.Take(value.Commands.Count - 1).ToArray() };
            else if (value.Findings.Count > 0) value = value with { Findings = value.Findings.Take(value.Findings.Count - 1).ToArray() };
            else if (value.Coverage.Count > 0) value = value with { Coverage = value.Coverage.Take(value.Coverage.Count - 1).ToArray() };
            else if (value.Repair is not null) value = value with { Repair = null };
            else if (value.Uncertainty.Count > 0) value = value with { Uncertainty = value.Uncertainty.Take(value.Uncertainty.Count - 1).ToArray() };
            else
            {
                value = value with { RepositoryPath = "", RequestHash = "", TargetTreeDigest = null,
                    Error = "Report details exceed the tool envelope. Inspect the persisted mission evidence." };
                if (measure(value) > MaximumCharacters) throw new InvalidOperationException("The report envelope exceeds its supported minimum.");
                break;
            }
        }
        return value;
    }
}
