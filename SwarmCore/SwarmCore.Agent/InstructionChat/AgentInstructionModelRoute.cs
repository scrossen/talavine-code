using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SwarmCore.Agent.InstructionChat;

/// <summary>
/// A model route compiled from an agent's human-readable operating manual. Hosts persist this
/// structured value beside their agent record so the route is available before the first model
/// call of a work cycle; the manual remains the source of truth.
/// </summary>
public sealed record AgentInstructionModelRoute(
    string Provider,
    string Model,
    string InstructionFingerprint,
    string DirectiveText);

public enum AgentInstructionModelRouteStatus
{
    NotSpecified,
    Valid,
    Invalid,
}

public sealed record AgentInstructionModelRouteParseResult(
    AgentInstructionModelRouteStatus Status,
    AgentInstructionModelRoute? Route = null,
    string? Error = null);

/// <summary>
/// Deterministically compiles a per-agent model directive without asking a model to choose the
/// model that must run that same turn. Supported manual forms are intentionally small:
/// <c>Model: OpenRouter/z-ai/glm-5.2</c>, <c>Use model OpenRouter/z-ai/glm-5.2</c>, and
/// <c>Use GLM-5.2 on OpenRouter</c>. A host may canonicalize aliases against its provider catalog
/// before persisting the result.
/// </summary>
public static partial class AgentInstructionModelRouteParser
{
    [GeneratedRegex(@"(?im)^\s*(?:[-*]\s*)?model\s*:\s*(?<provider>[A-Za-z][A-Za-z0-9 ._-]{1,49})\s*/\s*(?<model>[^\r\n`]+?)\s*$")]
    private static partial Regex LabelDirective();

    [GeneratedRegex(@"(?im)^\s*(?:[-*]\s*)?use\s+(?:the\s+)?(?:model\s+)?(?<provider>[A-Za-z][A-Za-z0-9 ._-]{1,49})\s*/\s*(?<model>[^\r\n`]+?)(?:\s+as\s+(?:the\s+)?model)?[.!]?\s*$")]
    private static partial Regex SlashDirective();

    [GeneratedRegex(@"(?im)^\s*(?:[-*]\s*)?use\s+(?:the\s+)?(?:model\s+)?(?<model>[^\r\n`]+?)\s+on\s+(?<provider>[A-Za-z][A-Za-z0-9 ._-]{1,49})[.!]?\s*$")]
    private static partial Regex OnDirective();

    public static AgentInstructionModelRouteParseResult Parse(string? instructionText)
    {
        if (string.IsNullOrWhiteSpace(instructionText))
            return new(AgentInstructionModelRouteStatus.NotSpecified);

        var matches = new List<(int Index, string Provider, string Model, string Text)>();
        AddMatches(LabelDirective(), instructionText, matches);
        AddMatches(SlashDirective(), instructionText, matches);
        AddMatches(OnDirective(), instructionText, matches);

        if (matches.Count == 0)
            return new(AgentInstructionModelRouteStatus.NotSpecified);

        var distinct = matches
            .Select(match => (Provider: NormalizeProvider(match.Provider), Model: NormalizeModel(match.Model)))
            .Distinct()
            .ToList();
        if (distinct.Count > 1)
        {
            return new(
                AgentInstructionModelRouteStatus.Invalid,
                Error: "The operating manual contains conflicting model directives. Keep exactly one provider/model rule.");
        }

        var selected = matches.OrderBy(match => match.Index).Last();
        var provider = NormalizeProvider(selected.Provider);
        var model = NormalizeModel(selected.Model);
        if (provider.Length is < 2 or > 50 || model.Length is < 1 or > 300)
        {
            return new(
                AgentInstructionModelRouteStatus.Invalid,
                Error: "The model directive must name a provider (2-50 characters) and model (1-300 characters).");
        }

        var fingerprint = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{provider}\n{model}")));
        return new(
            AgentInstructionModelRouteStatus.Valid,
            new AgentInstructionModelRoute(provider, model, fingerprint, selected.Text.Trim()));
    }

    private static void AddMatches(
        Regex regex,
        string text,
        ICollection<(int Index, string Provider, string Model, string Text)> output)
    {
        foreach (Match match in regex.Matches(text))
        {
            if (!match.Success) continue;
            output.Add((
                match.Index,
                match.Groups["provider"].Value,
                match.Groups["model"].Value,
                match.Value));
        }
    }

    private static string Normalize(string value) =>
        string.Join(' ', value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string NormalizeProvider(string value) =>
        Normalize(value).TrimEnd('.', '!', ';');

    private static string NormalizeModel(string value) =>
        Normalize(value).TrimEnd('.', '!', ';');
}
