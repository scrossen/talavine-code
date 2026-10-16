using SwarmCore.LlmRouting.Configuration;

namespace SwarmCode.Hosting;

/// <summary>Resolves named source keys from the process environment or a repository's ignored .env.</summary>
public sealed class ModelSourceEnvironmentKeys(string repositoryRoot, IReadOnlyList<ConfiguredModelSource> sources)
{
    public string? Get(string provider)
    {
        var variable = sources.FirstOrDefault(s => s.Enabled && s.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase))
            ?.ApiKeyEnvironmentVariable;
        if (string.IsNullOrWhiteSpace(variable)) return null;
        var processValue = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrWhiteSpace(processValue)) return processValue;

        var path = Path.Combine(repositoryRoot, ".env");
        if (!File.Exists(path)) return null;
        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#') continue;
            if (trimmed.StartsWith("export ", StringComparison.Ordinal)) trimmed = trimmed[7..].TrimStart();
            var separator = trimmed.IndexOf('=');
            if (separator < 1 || !trimmed[..separator].Trim().Equals(variable, StringComparison.Ordinal)) continue;
            var value = trimmed[(separator + 1)..].Trim();
            if (value.Length >= 2 && value[0] == value[^1] && value[0] is '\'' or '"') value = value[1..^1];
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        return null;
    }
}
