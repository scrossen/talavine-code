using System.Text.RegularExpressions;

namespace SwarmCore.Skills.FileSkills;

/// <summary>
/// Parses SKILL.md manifest files into SkillManifest objects.
/// Handles YAML frontmatter (between --- delimiters) and markdown body.
/// </summary>
public static partial class SkillManifestParser
{
    /// <summary>
    /// Largest SKILL.md the parser will read. A manifest is frontmatter plus a prompt body;
    /// anything past this is either a mistake or an attempt to exhaust memory, and the
    /// file is refused before a single byte is read.
    /// </summary>
    public const long MaxManifestBytes = 1024 * 1024;

    /// <summary>
    /// Parse a SKILL.md file from disk.
    /// </summary>
    public static SkillManifest ParseFile(string filePath)
    {
        EnsureWithinSizeCap(filePath);
        var content = File.ReadAllText(filePath);
        var manifest = Parse(content);
        manifest.SourcePath = Path.GetFullPath(filePath);
        return manifest;
    }

    /// <summary>
    /// Parse a SKILL.md file from disk asynchronously.
    /// </summary>
    public static async Task<SkillManifest> ParseFileAsync(string filePath, CancellationToken ct = default)
    {
        EnsureWithinSizeCap(filePath);
        var content = await File.ReadAllTextAsync(filePath, ct);
        var manifest = Parse(content);
        manifest.SourcePath = Path.GetFullPath(filePath);
        return manifest;
    }

    private static void EnsureWithinSizeCap(string filePath)
    {
        var info = new FileInfo(filePath);
        if (info.Exists && info.Length > MaxManifestBytes)
        {
            throw new InvalidDataException(
                $"Refusing to read SKILL.md at {filePath}: it is {info.Length:N0} bytes, above the {MaxManifestBytes:N0} byte manifest cap.");
        }
    }

    /// <summary>
    /// Parse SKILL.md content (frontmatter + markdown body).
    /// </summary>
    public static SkillManifest Parse(string content)
    {
        var manifest = new SkillManifest();

        // A SKILL.md saved on Windows and the same file saved on Linux are the same skill.
        // Normalizing here keeps the instruction body — which feeds both the execution
        // prompt and the registry's content-drift signature — independent of the editor's
        // line-ending setting, so a CRLF/LF flip is never mistaken for a content change.
        content = content.Replace("\r\n", "\n");

        var (frontmatter, body) = ExtractFrontmatter(content);
        manifest.Instructions = body.Trim();

        if (!string.IsNullOrWhiteSpace(frontmatter))
        {
            ParseFrontmatter(frontmatter, manifest);
        }

        // If no functions defined, create a default "Execute" function
        if (manifest.Functions.Count == 0)
        {
            manifest.Functions.Add(new SkillManifestFunction
            {
                Name = "Execute",
                Description = manifest.Description,
                ReturnType = "string",
                Parameters =
                [
                    new() { Name = "input", Type = "string", Description = "The input to process", Required = true }
                ]
            });
        }

        return manifest;
    }

    /// <summary>
    /// Extract YAML frontmatter and markdown body from content.
    /// Frontmatter opens with <c>---</c> on the first line and closes at the next line that
    /// consists of <em>exactly</em> <c>---</c> (trailing whitespace allowed). A line that
    /// merely starts with three dashes — <c>----</c>, <c>---foo</c> — is frontmatter
    /// content, not a delimiter; treating it as one used to cut the frontmatter short and
    /// push the remaining keys into the instruction body.
    /// </summary>
    private static (string Frontmatter, string Body) ExtractFrontmatter(string content)
    {
        var trimmed = content.TrimStart();
        if (!trimmed.StartsWith("---"))
            return ("", content);

        var openingLineEnd = trimmed.IndexOf('\n');
        if (openingLineEnd < 0)
            return ("", content);

        var lineStart = openingLineEnd + 1;
        while (lineStart <= trimmed.Length)
        {
            var lineEnd = trimmed.IndexOf('\n', lineStart);
            var line = lineEnd < 0 ? trimmed[lineStart..] : trimmed[lineStart..lineEnd];

            if (line.Trim() == "---")
            {
                var frontmatter = trimmed[openingLineEnd..lineStart].Trim();
                var body = lineEnd < 0 ? "" : trimmed[(lineEnd + 1)..];
                return (frontmatter, body);
            }

            if (lineEnd < 0)
                break;

            lineStart = lineEnd + 1;
        }

        // No closing delimiter: the whole document is body, exactly as before.
        return ("", content);
    }

    /// <summary>
    /// Parse YAML-like frontmatter into the manifest.
    /// Supports: scalar values, lists (- item), nested maps, and lists of maps.
    ///
    /// Recognizes both the Agent Skills specification vocabulary (name, description,
    /// license, compatibility, metadata, allowed-tools) and Talavine's client
    /// extensions. Extensions are accepted at the top level (legacy Talavine layout)
    /// or nested under <c>metadata:</c>, which is where the spec says client-specific
    /// values belong. Top-level always wins over metadata when both are present.
    ///
    /// Anything left unrecognized is recorded in <see cref="SkillManifest.UnknownKeys"/>
    /// rather than dropped, so the import review can show the user what Talavine
    /// ignored instead of hiding a declaration the author considered meaningful.
    /// </summary>
    private static void ParseFrontmatter(string yaml, SkillManifest manifest)
    {
        var lines = yaml.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var i = 0;
        var explicitKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (i < lines.Count)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
            {
                i++;
                continue;
            }

            var indent = GetIndent(line);
            if (indent > 0) { i++; continue; } // Skip unexpected indented lines at root

            var colonIdx = line.IndexOf(':');
            if (colonIdx < 0) { i++; continue; }

            var rawKey = line[..colonIdx].Trim();
            var key = NormalizeKey(rawKey);
            var value = line[(colonIdx + 1)..].Trim();

            if (string.IsNullOrEmpty(value))
            {
                // Could be a list or nested object — peek at next lines
                i++;
                switch (key)
                {
                    case "keywords":
                        manifest.Keywords = ParseStringList(lines, ref i);
                        explicitKeys.Add(key);
                        break;
                    case "permissions":
                        manifest.Permissions = ParseStringList(lines, ref i);
                        explicitKeys.Add(key);
                        break;
                    case "functions":
                        manifest.Functions = ParseFunctions(lines, ref i);
                        explicitKeys.Add(key);
                        break;
                    case "allowed-tools":
                        manifest.AllowedTools = ParseStringList(lines, ref i);
                        explicitKeys.Add(key);
                        break;
                    case "metadata":
                        foreach (var pair in ParseStringMap(lines, ref i))
                            manifest.Metadata[pair.Key] = pair.Value;
                        explicitKeys.Add(key);
                        break;
                    default:
                        manifest.UnknownKeys.Add(rawKey);
                        SkipBlock(lines, ref i);
                        break;
                }
            }
            else
            {
                // Scalar value
                value = UnquoteYaml(value);
                switch (key)
                {
                    case "name": manifest.Name = value; break;
                    case "description": manifest.Description = value; break;
                    case "version": manifest.Version = value; break;
                    case "category": manifest.Category = value; break;
                    case "trust_level": manifest.TrustLevel = value; break;
                    case "function_type": manifest.FunctionType = value; break;
                    case "license": manifest.License = value; break;
                    case "compatibility": manifest.Compatibility = value; break;
                    case "allowed-tools":
                        // Spec form is a single space-separated string.
                        manifest.AllowedTools = SplitAllowedTools(value);
                        break;
                    case "temperature":
                        if (float.TryParse(value, out var temp)) manifest.Temperature = temp;
                        break;
                    case "max_tokens":
                        if (int.TryParse(value, out var maxTok)) manifest.MaxTokens = maxTok;
                        break;
                    default:
                        manifest.UnknownKeys.Add(rawKey);
                        break;
                }

                explicitKeys.Add(key);
                i++;
            }
        }

        ApplyMetadataExtensions(manifest, explicitKeys);
        NoteUnrecognizedPermissions(manifest);
    }

    /// <summary>
    /// A <c>permissions:</c> entry that maps to no known permission grants nothing, so a
    /// typo like <c>email.snd</c> silently produced a skill with fewer capabilities than
    /// its author declared. Keep the raw declaration (so the import review shows what was
    /// written) and record a diagnostic instead of dropping it without a trace.
    /// </summary>
    private static void NoteUnrecognizedPermissions(SkillManifest manifest)
    {
        foreach (var id in manifest.Permissions)
        {
            if (FileBasedSkill.TryMapPermission(id) is null)
            {
                manifest.Diagnostics.Add(new SkillManifestDiagnostic(
                    "permissions",
                    $"Permission '{id}' is not a recognized permission id and grants nothing; it will be ignored."));
            }
        }
    }

    /// <summary>
    /// Lift Talavine extension values out of the spec's <c>metadata</c> map, but only
    /// where the manifest did not already declare them at the top level.
    /// </summary>
    private static void ApplyMetadataExtensions(SkillManifest manifest, HashSet<string> explicitKeys)
    {
        if (manifest.Metadata.Count == 0) return;

        foreach (var (rawKey, value) in manifest.Metadata)
        {
            var key = NormalizeKey(rawKey);
            if (explicitKeys.Contains(key) || string.IsNullOrWhiteSpace(value)) continue;

            switch (key)
            {
                case "version": manifest.Version = value; break;
                case "category": manifest.Category = value; break;
                case "trust_level": manifest.TrustLevel = value; break;
                case "function_type": manifest.FunctionType = value; break;
                case "keywords":
                    manifest.Keywords = SplitAllowedTools(value.Replace(',', ' '));
                    break;
                case "temperature":
                    if (float.TryParse(value, out var temp)) manifest.Temperature = temp;
                    break;
                case "max_tokens":
                    if (int.TryParse(value, out var maxTok)) manifest.MaxTokens = maxTok;
                    break;
            }
        }
    }

    /// <summary>
    /// Fold the spec's hyphenated keys and Talavine's snake_case keys onto one
    /// spelling so both forms of a manifest parse identically.
    /// </summary>
    private static string NormalizeKey(string rawKey)
    {
        var key = rawKey.Trim().ToLowerInvariant();
        return key switch
        {
            "allowed_tools" => "allowed-tools",
            "trust-level" => "trust_level",
            "function-type" => "function_type",
            "max-tokens" => "max_tokens",
            _ => key,
        };
    }

    private static List<string> SplitAllowedTools(string value) =>
        value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(UnquoteYaml)
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .ToList();

    /// <summary>
    /// Parse an indented block of <c>key: value</c> pairs (the spec's <c>metadata</c> shape).
    /// </summary>
    private static Dictionary<string, string> ParseStringMap(List<string> lines, ref int i)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        while (i < lines.Count)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) { i++; continue; }
            if (GetIndent(line) == 0) break; // Back to root level

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#')) { i++; continue; }

            var colonIdx = trimmed.IndexOf(':');
            if (colonIdx > 0)
            {
                var key = trimmed[..colonIdx].Trim();
                var value = UnquoteYaml(trimmed[(colonIdx + 1)..].Trim());
                if (!string.IsNullOrWhiteSpace(key))
                    map[key] = value;
            }

            i++;
        }

        return map;
    }

    /// <summary>
    /// Parse a YAML list of strings (- item format). The items may sit at the key's own
    /// indentation (`permissions:` then `- email.send` at column 0 is valid YAML); the first
    /// item sets the list's indentation and the list ends at the first line that is not an
    /// item at that indentation.
    /// </summary>
    private static List<string> ParseStringList(List<string> lines, ref int i)
    {
        var result = new List<string>();
        int? listIndent = null;
        while (i < lines.Count)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) { i++; continue; }

            var trimmed = line.TrimStart();
            var indent = GetIndent(line);
            if (trimmed.StartsWith('#') && indent > 0) { i++; continue; }
            if (!trimmed.StartsWith('-'))
                break; // End of list

            listIndent ??= indent;
            if (indent != listIndent) break;

            var value = trimmed[1..].Trim();
            if (!string.IsNullOrEmpty(value))
                result.Add(UnquoteYaml(value));
            i++;
        }
        return result;
    }

    /// <summary>
    /// Parse a YAML list of function objects.
    /// </summary>
    private static List<SkillManifestFunction> ParseFunctions(List<string> lines, ref int i)
    {
        var functions = new List<SkillManifestFunction>();

        while (i < lines.Count)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) { i++; continue; }

            var indent = GetIndent(line);
            if (indent == 0) break; // Back to root level

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("- "))
            {
                // Start of a new function object
                var func = new SkillManifestFunction();

                // Parse "- name: value" on the same line
                var firstProp = trimmed[2..].Trim();
                if (!string.IsNullOrEmpty(firstProp))
                    ParseFunctionProperty(firstProp, func);
                i++;

                // Parse remaining properties at deeper indent
                var propIndent = indent + 2;
                while (i < lines.Count)
                {
                    var propLine = lines[i];
                    if (string.IsNullOrWhiteSpace(propLine)) { i++; continue; }

                    var pIndent = GetIndent(propLine);
                    if (pIndent < propIndent)
                    {
                        // Check if this is a new list item at the function list level
                        var pTrimmed = propLine.TrimStart();
                        if (pTrimmed.StartsWith("- ") && pIndent == indent)
                            break; // New function
                        if (pIndent <= indent)
                            break; // Back to parent
                    }

                    var propTrimmed = propLine.TrimStart();

                    // Check if this is the parameters sub-list
                    if (propTrimmed.StartsWith("parameters:"))
                    {
                        i++;
                        func.Parameters = ParseParameters(lines, ref i, pIndent + 2);
                    }
                    else
                    {
                        ParseFunctionProperty(propTrimmed, func);
                        i++;
                    }
                }

                functions.Add(func);
            }
            else
            {
                i++;
            }
        }

        return functions;
    }

    private static void ParseFunctionProperty(string propText, SkillManifestFunction func)
    {
        var colonIdx = propText.IndexOf(':');
        if (colonIdx < 0) return;

        var key = propText[..colonIdx].Trim().ToLowerInvariant();
        var value = UnquoteYaml(propText[(colonIdx + 1)..].Trim());

        switch (key)
        {
            case "name": func.Name = value; break;
            case "description": func.Description = value; break;
            case "return_type": func.ReturnType = value; break;
        }
    }

    /// <summary>
    /// Parse a YAML list of parameter objects.
    /// </summary>
    private static List<SkillManifestParameter> ParseParameters(List<string> lines, ref int i, int expectedIndent)
    {
        var parameters = new List<SkillManifestParameter>();

        while (i < lines.Count)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) { i++; continue; }

            var indent = GetIndent(line);
            if (indent < expectedIndent) break;

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("- "))
            {
                var param = new SkillManifestParameter();

                // Parse "- name: value"
                var firstProp = trimmed[2..].Trim();
                if (!string.IsNullOrEmpty(firstProp))
                    ParseParameterProperty(firstProp, param);
                i++;

                // Parse remaining properties
                while (i < lines.Count)
                {
                    var propLine = lines[i];
                    if (string.IsNullOrWhiteSpace(propLine)) { i++; continue; }

                    var pIndent = GetIndent(propLine);
                    if (pIndent <= indent) break;

                    var propText = propLine.TrimStart();
                    if (TryGetParameterListProperty(propText, out var listProperty)
                        && string.IsNullOrWhiteSpace(GetPropertyValue(propText)))
                    {
                        i++;
                        var values = ParseNestedStringList(lines, ref i, pIndent + 2);
                        SetParameterListProperty(param, listProperty, values);
                    }
                    else
                    {
                        ParseParameterProperty(propText, param);
                        i++;
                    }
                }

                parameters.Add(param);
            }
            else
            {
                i++;
            }
        }

        return parameters;
    }

    private static void ParseParameterProperty(string propText, SkillManifestParameter param)
    {
        var colonIdx = propText.IndexOf(':');
        if (colonIdx < 0) return;

        var key = NormalizeParameterKey(propText[..colonIdx]);
        var rawValue = propText[(colonIdx + 1)..].Trim();
        var value = UnquoteYaml(rawValue);

        switch (key)
        {
            case "name": param.Name = value; break;
            case "type": param.Type = value; break;
            case "description": param.Description = value; break;
            case "required":
                param.Required = value.Equals("true", StringComparison.OrdinalIgnoreCase);
                break;
            case "default":
            case "default_value":
                param.DefaultValue = value;
                break;
            case "valid_values":
                param.ValidValues = ParseInlineStringList(rawValue);
                break;
            case "options_source":
                param.OptionsSource = value;
                break;
            case "depends_on":
                param.DependsOn = ParseInlineStringList(rawValue);
                break;
        }
    }

    private static bool TryGetParameterListProperty(string propText, out string property)
    {
        var colonIdx = propText.IndexOf(':');
        property = colonIdx < 0 ? "" : NormalizeParameterKey(propText[..colonIdx]);
        return property is "valid_values" or "depends_on";
    }

    private static string GetPropertyValue(string propText)
    {
        var colonIdx = propText.IndexOf(':');
        return colonIdx < 0 ? "" : propText[(colonIdx + 1)..].Trim();
    }

    private static string NormalizeParameterKey(string rawKey) =>
        rawKey.Trim().ToLowerInvariant() switch
        {
            "valid-values" => "valid_values",
            "options-source" => "options_source",
            "depends-on" => "depends_on",
            var key => key,
        };

    private static void SetParameterListProperty(
        SkillManifestParameter parameter,
        string property,
        List<string> values)
    {
        if (property == "valid_values")
            parameter.ValidValues = values;
        else if (property == "depends_on")
            parameter.DependsOn = values;
    }

    private static List<string> ParseInlineStringList(string rawValue)
    {
        var value = rawValue.Trim();
        if (value.StartsWith('[') && value.EndsWith(']'))
            value = value[1..^1];

        if (string.IsNullOrWhiteSpace(value)) return [];

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(UnquoteYaml)
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .ToList();
    }

    private static List<string> ParseNestedStringList(
        List<string> lines,
        ref int i,
        int expectedIndent)
    {
        var result = new List<string>();
        while (i < lines.Count)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) { i++; continue; }

            var indent = GetIndent(line);
            var trimmed = line.TrimStart();
            if (indent < expectedIndent || !trimmed.StartsWith("- ", StringComparison.Ordinal))
                break;

            var value = trimmed[2..].Trim();
            if (!string.IsNullOrWhiteSpace(value))
                result.Add(UnquoteYaml(value));
            i++;
        }

        return result;
    }

    private static void SkipBlock(List<string> lines, ref int i)
    {
        while (i < lines.Count)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) { i++; continue; }
            if (GetIndent(line) == 0) break;
            i++;
        }
    }

    private static int GetIndent(string line)
    {
        int count = 0;
        foreach (var c in line)
        {
            if (c == ' ') count++;
            else if (c == '\t') count += 2;
            else break;
        }
        return count;
    }

    private static string UnquoteYaml(string value)
    {
        if (value.Length >= 2)
        {
            if ((value[0] == '"' && value[^1] == '"') ||
                (value[0] == '\'' && value[^1] == '\''))
            {
                return value[1..^1];
            }
        }
        return value;
    }
}
