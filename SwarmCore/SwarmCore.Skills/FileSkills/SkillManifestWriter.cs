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
    /// value that is itself wrapped in quotes is wrapped in the other quote so the parser's
    /// unquoting returns it unchanged.
    /// </summary>
    public static string Scalar(string? value)
    {
        var sb = new StringBuilder((value ?? "").Length);
        foreach (var ch in value ?? "")
            sb.Append(char.IsControl(ch) ? ' ' : ch);
        var folded = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));

        if (folded.Length >= 2
            && (folded[0] == '"' && folded[^1] == '"' || folded[0] == '\'' && folded[^1] == '\''))
        {
            return folded[0] == '"' ? $"'{folded}'" : $"\"{folded}\"";
        }

        return folded;
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
