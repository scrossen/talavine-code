using System.Reflection;
using System.Text.Json;

namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// DI-free view of the reviewed provider-pack model tiers.
///
/// This is the deep fallback — used by sites that must run synchronously and
/// cannot consult <see cref="IModelScout"/> (DI-free option constructors,
/// deeply-nested router branches, fresh-install wizard UI before any background
/// service has fired, etc.). The scout, when available, overrides this with
/// live, function-aware data via <see cref="IModelScout.RecommendAsync"/>.
///
/// Cloud and subscription model ids are read from the exact same embedded JSON
/// sources as the execution packs. Updating a pack therefore updates even the
/// deepest synchronous fallback without maintaining a second model table.
///
/// Capability tiers:
///   - <see cref="Frontier"/> — the most capable model, used when accuracy matters more than cost
///   - <see cref="Balanced"/> — the recommended default; good cost/quality tradeoff (used as <see cref="ForProvider"/>)
///   - <see cref="Economy"/> — the cheap/fast tier
/// </summary>
public static class ModelDefaults
{
    private static readonly Lazy<IReadOnlyDictionary<string, PackModelDefaults>> PackDefaults =
        new(LoadPackDefaults, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Recommended default model per provider. Picks the "balanced" tier — fast
    /// and cheap enough for routine work, capable enough that most users won't
    /// need to override. Returns <c>null</c> if the provider has no known default
    /// (i.e. an unrecognised provider name).
    /// </summary>
    public static string? ForProvider(string providerName) => Balanced(providerName);

    public static string? Frontier(string providerName)
        => TryGetPackDefaults(providerName)?.Frontier ?? LocalFrontier(providerName);

    public static string? Balanced(string providerName)
        => TryGetPackDefaults(providerName)?.Balanced ?? LocalBalanced(providerName);

    public static string? Economy(string providerName)
        => TryGetPackDefaults(providerName)?.Economy ?? LocalEconomy(providerName);

    /// <summary>
    /// Embedding model default per provider. Most chat providers don't supply
    /// embeddings; those return <c>null</c> and callers fall back to the local
    /// <c>EmbeddingModelDefaults.Model</c>.
    /// </summary>
    public static string? EmbeddingForProvider(string providerName)
        => TryGetPackDefaults(providerName)?.Embedding;

    private static PackModelDefaults? TryGetPackDefaults(string? providerName)
    {
        var canonical = CanonicalProviderName(providerName);
        return canonical is not null && PackDefaults.Value.TryGetValue(canonical, out var defaults)
            ? defaults
            : null;
    }

    private static string? CanonicalProviderName(string? providerName)
        => providerName?.Trim().ToUpperInvariant() switch
        {
            "ANTHROPIC" => "CLAUDE",
            "GOOGLE" => "GEMINI",
            "XAI" => "GROK",
            "OPENROUTERAI" => "OPENROUTER",
            "HF" => "HUGGINGFACE",
            "CLAUDE CODE" => "CLAUDECODE",
            "CODEX CLI" => "CODEXCLI",
            "GEMINI CLI" => "GEMINICLI",
            { Length: > 0 } value => value,
            _ => null,
        };

    private static string? LocalFrontier(string providerName)
        => OllamaProviderFamily.CanonicalCatalogName(providerName).Trim().ToUpperInvariant() switch
        {
            "OLLAMA" => "qwen3-coder:30b",
            _ => null,
        };

    private static string? LocalBalanced(string providerName)
        => OllamaProviderFamily.CanonicalCatalogName(providerName).Trim().ToUpperInvariant() switch
        {
            "OLLAMA" => "qwen2.5:7b",
            "LMSTUDIO" => "local-model",
            _ => null,
        };

    private static string? LocalEconomy(string providerName)
        => OllamaProviderFamily.CanonicalCatalogName(providerName).Trim().ToUpperInvariant() switch
        {
            "OLLAMA" => "qwen2.5:3b",
            _ => null,
        };

    private static IReadOnlyDictionary<string, PackModelDefaults> LoadPackDefaults()
    {
        var result = new Dictionary<string, PackModelDefaults>(StringComparer.OrdinalIgnoreCase);
        var assembly = typeof(ModelDefaults).Assembly;
        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(name => name.StartsWith(
                         "SwarmCore.Abstractions.ProviderPacks.",
                         StringComparison.Ordinal)
                         && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(stream);
                var root = document.RootElement;
                if (!root.TryGetProperty("providerName", out var providerProperty)
                    || providerProperty.ValueKind != JsonValueKind.String
                    || !root.TryGetProperty("modelDefaults", out var defaultsProperty)
                    || defaultsProperty.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var provider = CanonicalProviderName(providerProperty.GetString());
                var balanced = ReadOptionalString(defaultsProperty, "balanced");
                if (provider is null || balanced is null)
                {
                    continue;
                }

                result[provider] = new PackModelDefaults(
                    ReadOptionalString(defaultsProperty, "frontier"),
                    balanced,
                    ReadOptionalString(defaultsProperty, "economy"),
                    ReadOptionalString(defaultsProperty, "embedding"));
            }
            catch (JsonException)
            {
                // The authoritative provider-pack validator fails the app build/use
                // path. This low-level view stays fail-closed if malformed bytes are
                // encountered before SwarmCore.LlmRouting is loaded.
            }
        }

        return result;
    }

    private static string? ReadOptionalString(JsonElement parent, string propertyName)
        => parent.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private sealed record PackModelDefaults(
        string? Frontier,
        string Balanced,
        string? Economy,
        string? Embedding);

    /// <summary>
    /// Map an OpenRouter model id (e.g. <c>anthropic/claude-opus-4.7</c>) to the
    /// provider-native id (<c>claude-opus-4-7</c>). OpenRouter uses dots in
    /// version segments; Anthropic's API uses dashes. OpenAI chat aliases use a
    /// short OpenRouter id such as <c>gpt-5-chat</c>, while the native API exposes
    /// the rolling alias as <c>gpt-5-chat-latest</c>.
    /// </summary>
    public static string NormalizeModelId(string providerName, string modelId)
    {
        if (string.IsNullOrEmpty(modelId)) return modelId;

        return providerName.ToUpperInvariant() switch
        {
            // Anthropic native API uses dashes: claude-opus-4-7, not claude-opus-4.7.
            // Convert dots between digits to dashes, leaving other dots alone.
            "CLAUDE" or "ANTHROPIC" => DotsToDashesInVersions(modelId),
            "OPENAI" => NormalizeOpenAIModelId(modelId),
            _ => modelId
        };
    }

    private static string NormalizeOpenAIModelId(string id)
    {
        var lower = id.ToLowerInvariant();
        if (lower.StartsWith("gpt-", StringComparison.Ordinal)
            && lower.EndsWith("-chat", StringComparison.Ordinal)
            && lower.Length > "gpt--chat".Length
            && char.IsDigit(lower["gpt-".Length]))
        {
            return id + "-latest";
        }

        return id;
    }

    private static string DotsToDashesInVersions(string id)
    {
        // Replace ".N" with "-N" when preceded by a digit, i.e. convert
        // "claude-opus-4.7" -> "claude-opus-4-7" without touching
        // names that legitimately contain dots in non-version positions.
        var chars = id.ToCharArray();
        for (int i = 1; i < chars.Length - 1; i++)
        {
            if (chars[i] == '.' && char.IsDigit(chars[i - 1]) && char.IsDigit(chars[i + 1]))
                chars[i] = '-';
        }
        return new string(chars);
    }
}
