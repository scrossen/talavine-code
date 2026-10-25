using System.Text;

namespace SwarmCore.Skills.FileSkills;

/// <summary>A parameter of an authored skill function.</summary>
public sealed record AuthoredSkillParameter(string Name, string Type, string? Description, bool Required);

/// <summary>A function declared by an authored skill.</summary>
public sealed record AuthoredSkillFunction(
    string Name,
    string? Description,
    string ReturnType,
    IReadOnlyList<AuthoredSkillParameter> Parameters);

/// <summary>Everything a user or agent supplies when authoring a new SKILL.md.</summary>
public sealed record AuthoredSkillManifest
{
    /// <summary>Spec-valid skill name; also the folder name.</summary>
    public required string Name { get; init; }
    public required string Description { get; init; }
    public string Category { get; init; } = "External";
    public string FunctionType { get; init; } = "default";
    public IReadOnlyList<string> Keywords { get; init; } = [];
    public IReadOnlyList<string> AllowedTools { get; init; } = [];
    public IReadOnlyList<AuthoredSkillFunction> Functions { get; init; } = [];

    /// <summary>Markdown body that guides the model when the skill runs.</summary>
    public required string Body { get; init; }
}

/// <summary>
/// The one writer for authored SKILL.md files. Front matter is line-based, so every
/// scalar is folded onto one line: a description containing a newline must never be
/// able to add manifest keys such as <c>permissions:</c> or <c>allowed-tools:</c>.
/// </summary>
public static class SkillManifestWriter
{
    public static string Write(AuthoredSkillManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"name: {Scalar(manifest.Name)}");
        sb.AppendLine($"description: {Scalar(manifest.Description)}");
        sb.AppendLine("version: \"1.0.0\"");
        sb.AppendLine($"category: {Scalar(manifest.Category)}");
        sb.AppendLine("trust_level: Community");
        sb.AppendLine($"function_type: {Scalar(manifest.FunctionType)}");

        AppendList(sb, "keywords", manifest.Keywords);
        AppendList(sb, "allowed-tools", manifest.AllowedTools);

        if (manifest.Functions.Count > 0)
        {
            sb.AppendLine("functions:");
            foreach (var function in manifest.Functions)
            {
                sb.AppendLine($"  - name: {Scalar(function.Name)}");
                if (!string.IsNullOrWhiteSpace(function.Description))
                    sb.AppendLine($"    description: {Scalar(function.Description)}");
                sb.AppendLine($"    return_type: {Scalar(function.ReturnType)}");
                if (function.Parameters.Count == 0)
                    continue;

                sb.AppendLine("    parameters:");
                foreach (var parameter in function.Parameters)
                {
                    sb.AppendLine($"      - name: {Scalar(parameter.Name)}");
                    sb.AppendLine($"        type: {Scalar(parameter.Type)}");
                    if (!string.IsNullOrWhiteSpace(parameter.Description))
                        sb.AppendLine($"        description: {Scalar(parameter.Description)}");
                    sb.AppendLine($"        required: {(parameter.Required ? "true" : "false")}");
                }
            }
        }

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine(manifest.Body.Trim());
        return sb.ToString();
    }

    /// <summary>
    /// One front-matter scalar: control characters (including CR/LF) become spaces, and a
    /// value a strict YAML reader would not take as the same plain string (a leading
    /// indicator such as <c>#</c>, <c>-</c> or a quote, an embedded <c>: </c> or <c> #</c>,
    /// or a word YAML reads as a boolean, null or number) is written double-quoted with
    /// <c>\</c> and <c>"</c> escaped. <see cref="SkillManifestParser"/> reads it back exactly.
    /// </summary>
    public static string Scalar(string? value)
    {
        var sb = new StringBuilder((value ?? "").Length);
        foreach (var ch in value ?? "")
            sb.Append(char.IsControl(ch) ? ' ' : ch);
        var folded = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));

        return NeedsQuotes(folded)
            ? "\"" + folded.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : folded;
    }

    private const string LeadingIndicators = "#-?:,[]{}&*!|>'\"%@`";

    private static readonly HashSet<string> ReservedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "true", "false", "yes", "no", "on", "off", "y", "n", "null", "~",
    };

    private static bool NeedsQuotes(string value)
    {
        if (value.Length == 0)
            return false;
        if (LeadingIndicators.Contains(value[0]))
            return true;
        if (value.Contains(": ", StringComparison.Ordinal) || value.EndsWith(':')
            || value.Contains(" #", StringComparison.Ordinal))
            return true;
        if (ReservedWords.Contains(value))
            return true;
        return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);
    }

    private static void AppendList(StringBuilder sb, string key, IReadOnlyList<string> items)
    {
        var values = items
            .Select(Scalar)
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (values.Count == 0)
            return;

        sb.AppendLine($"{key}:");
        foreach (var item in values)
            sb.AppendLine($"  - {item}");
    }
}
