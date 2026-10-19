using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.Configuration;

namespace SwarmCore.LlmRouting.ProviderPacks;

/// <summary>
/// A reviewed, app-shipped provider pack. Packs contain volatile provider facts,
/// never executable code or routing authority. Their exact bytes are hashed so
/// qualification checkpoints can be invalidated when those facts change.
/// </summary>
public sealed record ProviderPackDocument
{
    public int SchemaVersion { get; init; }
    public required string PackId { get; init; }
    public required string Version { get; init; }
    public required string ProviderName { get; init; }
    public required string ModelPrefix { get; init; }
    public required string ExecutionDialect { get; init; }
    public required ProviderModelDefaults ModelDefaults { get; init; }
    public SubscriptionCliProviderPack? SubscriptionCli { get; init; }
    public OpenRouterProviderPack? OpenRouter { get; init; }
    public DirectApiProviderPack? DirectApi { get; init; }

    public Dictionary<string, List<string>> ModelReasoningEfforts { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public string ContentSha256 { get; internal set; } = string.Empty;
    public string Identity => $"{PackId}@{Version}:{ContentSha256}";
}

public sealed record ProviderModelDefaults
{
    public string? Frontier { get; init; }
    public required string Balanced { get; init; }
    public string? Economy { get; init; }
    public string? Embedding { get; init; }
}

public sealed record SubscriptionCliProviderPack
{
    public required bool AutomaticRoutingEnabled { get; init; }
    public int DiscoveryPriority { get; init; } = 100;
    public int CatalogRefreshDays { get; init; } = 7;
    public required string Command { get; init; }
    public required string NpmPackage { get; init; }
    public required IReadOnlyList<string> EnvironmentVariables { get; init; }
    public required IReadOnlyList<string> LoginArguments { get; init; }
    public required IReadOnlyList<string> LoginStatusArguments { get; init; }
    public required IReadOnlyList<string> AuthenticatedStatusMarkers { get; init; }
    public required IReadOnlyList<string> RecognizedStatusMarkers { get; init; }
    public required string DefaultModel { get; init; }
    public required IReadOnlyList<string> BuiltInModels { get; init; }
    public required IReadOnlyList<string> ReasoningEfforts { get; init; }
    public required IReadOnlyList<string> DiscoveryReasoningEfforts { get; init; }
    public required IReadOnlyList<string> HelpArguments { get; init; }
    public required IReadOnlyList<string> ModelConfigPaths { get; init; }
    public required IReadOnlyList<string> InferenceArguments { get; init; }
    /// <summary>Optional reviewed metadata used only by an explicitly admitted host launch.</summary>
    public SubscriptionCliControlledLaunchPack? ControlledLaunch { get; init; }
    public required string ModelArgument { get; init; }
    public required string ReasoningConfigFormat { get; init; }
    public IReadOnlyList<string> FoundationProviders { get; init; } = [];
    public IReadOnlyDictionary<string, decimal> RelativeCapacityWeights { get; init; } =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> CapabilityAliases { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed record SubscriptionCliControlledLaunchPack
{
    public required string HomeEnvironmentVariable { get; init; }
    public required IReadOnlyList<string> ConfigurationRoots { get; init; }
    public required IReadOnlyList<string> FileAuthenticationArguments { get; init; }
    public required string AuthenticatedMode { get; init; }
    public IReadOnlyDictionary<string, string> NonSecretConfigurationTemplates { get; init; } = new Dictionary<string, string>();
}

public sealed record OpenRouterProviderPack
{
    // Reviewed models that require thinking even for a bounded background call.
    public IReadOnlyDictionary<string, string> MinimumReasoningEffort { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public int CatalogRefreshDays { get; init; } = 7;
    public required string BaseUrl { get; init; }
    public IReadOnlyList<string> ExecutionEndpoints { get; init; } = [];
    public string CredentialValidationPath { get; init; } = "/key";
    public required string ModelsPath { get; init; }
    public required string DefaultModel { get; init; }
    public required IReadOnlyList<string> DefaultModels { get; init; }
    public required IReadOnlyList<string> VisionModelPatterns { get; init; }
    public required string CatalogOutputModalities { get; init; }
    public required IReadOnlyList<string> CatalogSorts { get; init; }
    public required string ScoutApiSort { get; init; }
    public required decimal ScoutMaxPromptPricePerMillion { get; init; }
    public required decimal ScoutMaxCompletionPricePerMillion { get; init; }
    public required int ScoutMinimumContextTokens { get; init; }
    public required int ScoutCostQualityTradeoff { get; init; }
    public required int ScoutShortlistSize { get; init; }
    public required bool ScoutRequireStructuredOutputs { get; init; }
    public required IReadOnlyList<string> NonChatModelMarkers { get; init; }
}

public sealed record DirectApiProviderPack
{
    public required string BaseUrl { get; init; }
    public IReadOnlyList<string> ExecutionEndpoints { get; init; } = [];
    public bool HasListModelsEndpoint { get; init; } = true;
    public required string DefaultModel { get; init; }
    public string? DefaultEmbeddingModel { get; init; }
    public required IReadOnlyList<string> DefaultModels { get; init; }
    public IReadOnlyDictionary<string, ProviderModelFacts> ModelFacts { get; init; } =
        new Dictionary<string, ProviderModelFacts>(StringComparer.OrdinalIgnoreCase);
    /// <summary>Reviewed billing aliases only; these do not add selectable models or qualification evidence.</summary>
    public IReadOnlyDictionary<string, string> PricingAliases { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> PreferredModels { get; init; } = [];
    public required IReadOnlyList<string> VisionModelPatterns { get; init; }
    public IReadOnlyList<string> NonVisionModelPatterns { get; init; } = [];
    public IReadOnlyList<string> CatalogModelPrefixes { get; init; } = [];
    public IReadOnlyList<string> ModelScoutCandidates { get; init; } = [];
    public IReadOnlyList<string> ModelScoutSuggested { get; init; } = [];
    public IReadOnlyList<string> AutomaticMeteredCandidates { get; init; } = [];
    public int QualificationPriority { get; init; } = 100;
}

/// <summary>
/// Reviewed bootstrap facts for a model listed by a provider pack. Live catalog
/// observations and measured benchmark evidence supersede these display/pricing
/// hints; they never constitute route qualification proof.
/// </summary>
public sealed record ProviderModelFacts
{
    /// <summary>Explicit reviewed transport; null retains the provider's legacy contract.</summary>
    public string? ExecutionTransport { get; init; }
    public IReadOnlyList<ModelTransportCapabilities> Transports { get; init; } = [];
    public string? DisplayName { get; init; }
    public bool? SupportsChat { get; init; }
    public bool? SupportsEmbeddings { get; init; }
    public bool? SupportsVision { get; init; }
    public int? MaxContextTokens { get; init; }
    public decimal? InputCostPer1K { get; init; }
    public decimal? OutputCostPer1K { get; init; }
    public string? Description { get; init; }
    public string? RecommendedFor { get; init; }
}

/// <summary>
/// Loads immutable provider packs embedded in this assembly. A future Model
/// Scout learning overlay may add observed models, failures, and performance,
/// but it must not replace these reviewed execution/authentication controls.
/// </summary>
public static class ProviderPackCatalog
{
    private const int CurrentSchemaVersion = 1;
    private static readonly Lazy<IReadOnlyDictionary<string, ProviderPackDocument>> Packs =
        new(LoadEmbeddedPacks, LazyThreadSafetyMode.ExecutionAndPublication);

    public static ProviderPackDocument GetRequired(string providerName)
    {
        if (Packs.Value.TryGetValue(CanonicalizeProviderName(providerName), out var pack))
        {
            return pack;
        }

        throw new InvalidOperationException($"No provider pack is available for '{providerName}'.");
    }

    public static bool TryGet(string? providerName, out ProviderPackDocument? pack)
    {
        pack = null;
        return !string.IsNullOrWhiteSpace(providerName)
               && Packs.Value.TryGetValue(CanonicalizeProviderName(providerName), out pack);
    }

    public static string CanonicalizeProviderName(string? providerName)
    {
        var normalized = providerName?.Trim() ?? string.Empty;
        return normalized.ToLowerInvariant() switch
        {
            "anthropic" => "Claude",
            "google" => "Gemini",
            "xai" => "Grok",
            "openrouterai" => "OpenRouter",
            "hf" => "HuggingFace",
            _ => normalized,
        };
    }

    public static IReadOnlyList<ProviderPackDocument> GetSubscriptionCliPacks() => Packs.Value.Values
        .Where(pack => pack.SubscriptionCli is not null)
        .OrderBy(pack => pack.SubscriptionCli!.DiscoveryPriority)
        .ThenBy(pack => pack.ProviderName, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public static IReadOnlyList<ProviderPackDocument> GetAutomaticSubscriptionCliPacks()
        => GetSubscriptionCliPacks()
            .Where(pack => pack.SubscriptionCli!.AutomaticRoutingEnabled)
            .ToList();

    public static bool IsAutomaticRoutingAllowed(string? providerName)
        => !TryGet(providerName, out var pack)
           || pack!.SubscriptionCli is not { AutomaticRoutingEnabled: false };

    /// <summary>
    /// Returns the reviewed relative subscription-window cost for an exact
    /// provider/model tier. These are not dollars: they are within-provider
    /// capacity ratios used to conserve a finite subscription allowance. A
    /// reasoning-effort suffix scales the reviewed base tier, while an unknown
    /// future model falls back to the conservative categorical heuristic.
    /// </summary>
    public static decimal ResolveSubscriptionCapacityWeight(
        string? providerName,
        string? modelId)
    {
        var normalizedModel = (modelId ?? string.Empty).Trim();
        var effortMultiplier = SubscriptionModelTierWeights.ResolveReasoningEffortMultiplier(
            normalizedModel);
        var effortSeparator = normalizedModel.LastIndexOf('@');
        var baseModel = effortSeparator > 0
            ? normalizedModel[..effortSeparator]
            : normalizedModel;

        if (TryGet(providerName, out var pack)
            && pack!.SubscriptionCli is { } cli)
        {
            var reviewed = cli.RelativeCapacityWeights.FirstOrDefault(entry =>
                entry.Key.Equals(baseModel, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(reviewed.Key) && reviewed.Value > 0m)
            {
                return reviewed.Value * effortMultiplier;
            }
        }

        return SubscriptionModelTierWeights.Resolve(normalizedModel);
    }

    public static bool IsOfficialEndpoint(string? providerName, string? configuredEndpoint)
    {
        if (!TryGet(providerName, out var pack))
        {
            return true;
        }

        var expectedBase = pack!.OpenRouter?.BaseUrl ?? pack.DirectApi?.BaseUrl;
        if (string.IsNullOrWhiteSpace(expectedBase) || string.IsNullOrWhiteSpace(configuredEndpoint))
        {
            return true;
        }

        if (!TryNormalizeEndpoint(configuredEndpoint, out var normalizedConfigured))
        {
            return false;
        }

        var reviewedEndpoints = pack.OpenRouter is { } openRouter
            ? openRouter.ExecutionEndpoints.Prepend(openRouter.BaseUrl)
            : pack.DirectApi!.ExecutionEndpoints.Prepend(pack.DirectApi.BaseUrl);
        return reviewedEndpoints.Any(endpoint =>
            TryNormalizeEndpoint(endpoint, out var normalizedExpected)
            && normalizedExpected.Equals(normalizedConfigured, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryNormalizeEndpoint(string endpoint, out string normalized)
    {
        normalized = string.Empty;
        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http")
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return normalized.Length > 0;
    }

    public static IReadOnlyList<ProviderPackDocument> GetDirectApiPacks() => Packs.Value.Values
        .Where(pack => pack.DirectApi is not null)
        .OrderBy(pack => pack.DirectApi!.QualificationPriority)
        .ThenBy(pack => pack.ProviderName, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public static bool TryGetModelFacts(
        string? providerName,
        string? modelId,
        out ProviderModelFacts? facts)
    {
        facts = null;
        return TryGet(providerName, out var pack)
               && pack!.DirectApi is { } direct
               && !string.IsNullOrWhiteSpace(modelId)
               && direct.ModelFacts.TryGetValue(modelId.Trim(), out facts);
    }

    /// <summary>
    /// Binds durable qualification evidence to the reviewed provider facts that
    /// were active during execution. The readable contract prefix is retained
    /// when bounded; the pack hash is always authoritative.
    /// </summary>
    public static string ResolveExecutionContractId(
        string? providerName,
        string? baseExecutionContractId = null)
    {
        if (!TryGet(providerName, out var document))
        {
            return baseExecutionContractId?.Trim() ?? string.Empty;
        }

        var pack = document!;
        var root = string.IsNullOrWhiteSpace(baseExecutionContractId)
            ? pack.ExecutionDialect.Trim()
            : baseExecutionContractId!.Trim();
        if (root.Length > 80)
        {
            root = "contract-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)))
                .ToLowerInvariant()[..16];
        }

        return $"{root}.pack.{pack.ContentSha256}";
    }

    public static bool IsExecutionContractCurrent(
        string? providerName,
        string? persistedExecutionContractId,
        string? baseExecutionContractId = null)
        => !string.IsNullOrWhiteSpace(persistedExecutionContractId)
           && persistedExecutionContractId.Trim().Equals(
               ResolveExecutionContractId(providerName, baseExecutionContractId),
               StringComparison.Ordinal);

    internal static ProviderPackDocument ParseAndValidate(ReadOnlySpan<byte> bytes)
    {
        // Git also pins provider packs to LF, but canonicalize here so an
        // externally supplied/test pack cannot produce a different proof
        // identity solely because it crossed an OS line-ending boundary.
        var canonicalText = Encoding.UTF8.GetString(bytes)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var canonicalBytes = Encoding.UTF8.GetBytes(canonicalText);
        ProviderPackDocument pack;
        try
        {
            pack = JsonSerializer.Deserialize<ProviderPackDocument>(canonicalBytes, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            }) ?? throw new InvalidDataException("Provider pack is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Provider pack JSON is malformed.", ex);
        }

        Validate(pack);
        pack.ContentSha256 = Convert.ToHexString(SHA256.HashData(canonicalBytes)).ToLowerInvariant();
        return pack;
    }

    private static IReadOnlyDictionary<string, ProviderPackDocument> LoadEmbeddedPacks()
    {
        var assembly = typeof(ProviderPackCatalog).Assembly;
        var result = new Dictionary<string, ProviderPackDocument>(StringComparer.OrdinalIgnoreCase);
        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(name => name.StartsWith(
                         "SwarmCore.LlmRouting.ProviderPacks.",
                         StringComparison.Ordinal)
                         && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            using var stream = assembly.GetManifestResourceStream(resourceName)
                               ?? throw new InvalidDataException($"Provider pack resource '{resourceName}' could not be opened.");
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var pack = ParseAndValidate(memory.ToArray());
            if (!result.TryAdd(pack.ProviderName, pack))
            {
                throw new InvalidDataException($"Duplicate provider pack for '{pack.ProviderName}'.");
            }
        }

        return result;
    }

    private static void Validate(ProviderPackDocument pack)
    {
        if (pack.SchemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported provider-pack schema {pack.SchemaVersion}.");
        RequireIdentifier(pack.PackId, nameof(pack.PackId));
        RequireIdentifier(pack.Version, nameof(pack.Version));
        RequireIdentifier(pack.ProviderName, nameof(pack.ProviderName));
        RequireIdentifier(pack.ModelPrefix, nameof(pack.ModelPrefix));
        RequireIdentifier(pack.ExecutionDialect, nameof(pack.ExecutionDialect));

        var packKindCount = (pack.SubscriptionCli is null ? 0 : 1)
                            + (pack.OpenRouter is null ? 0 : 1)
                            + (pack.DirectApi is null ? 0 : 1);
        if (packKindCount != 1)
        {
            throw new InvalidDataException("A provider pack must define exactly one approved provider kind.");
        }
        ValidateModelDefaults(pack);

        if (pack.ProviderName.Equals(SubscriptionCliProviders.CodexCli, StringComparison.OrdinalIgnoreCase))
        {
            RequireExclusiveKind(pack, subscriptionCli: true);
            ValidateCodexCli(pack);
        }
        else if (pack.ProviderName.Equals(SubscriptionCliProviders.ClaudeCode, StringComparison.OrdinalIgnoreCase))
        {
            RequireExclusiveKind(pack, subscriptionCli: true);
            ValidateClaudeCode(pack);
        }
        else if (pack.ProviderName.Equals(SubscriptionCliProviders.GeminiCli, StringComparison.OrdinalIgnoreCase))
        {
            RequireExclusiveKind(pack, subscriptionCli: true);
            ValidateGeminiCli(pack);
        }
        else if (pack.ProviderName.Equals("OpenRouter", StringComparison.OrdinalIgnoreCase))
        {
            RequireExclusiveKind(pack, openRouter: true);
            ValidateOpenRouter(pack);
        }
        else if (pack.DirectApi is not null)
        {
            RequireExclusiveKind(pack, directApi: true);
            ValidateDirectApi(pack);
        }
        else
        {
            throw new InvalidDataException($"Provider '{pack.ProviderName}' has no approved provider-pack mapping.");
        }
    }

    private static void ValidateModelDefaults(ProviderPackDocument pack)
    {
        var values = new[]
        {
            pack.ModelDefaults.Frontier,
            pack.ModelDefaults.Balanced,
            pack.ModelDefaults.Economy,
            pack.ModelDefaults.Embedding,
        };
        if (string.IsNullOrWhiteSpace(pack.ModelDefaults.Balanced)
            || values.Where(value => value is not null).Any(InvalidOpenRouterModelValue))
        {
            throw new InvalidDataException($"{pack.ProviderName} model tier defaults are invalid.");
        }

        var inventory = pack.SubscriptionCli?.BuiltInModels
                        ?? pack.OpenRouter?.DefaultModels
                        ?? pack.DirectApi?.DefaultModels
                        ?? [];
        if (values.Where(value => value is not null)
            .Any(value => !inventory.Contains(value!, StringComparer.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                $"{pack.ProviderName} model tier defaults must belong to the reviewed inventory.");
        }

        var kindDefault = pack.SubscriptionCli?.DefaultModel
                          ?? pack.OpenRouter?.DefaultModel
                          ?? pack.DirectApi?.DefaultModel;
        if (!string.Equals(
                pack.ModelDefaults.Balanced,
                kindDefault,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"{pack.ProviderName} balanced tier must equal the provider-pack default model.");
        }
    }

    private static void RequireExclusiveKind(
        ProviderPackDocument pack,
        bool subscriptionCli = false,
        bool openRouter = false,
        bool directApi = false)
    {
        if ((pack.SubscriptionCli is not null) != subscriptionCli
            || (pack.OpenRouter is not null) != openRouter
            || (pack.DirectApi is not null) != directApi)
        {
            throw new InvalidDataException(
                $"Provider '{pack.ProviderName}' does not match its approved exclusive provider-pack kind.");
        }
    }

    private static void ValidateCodexCli(ProviderPackDocument pack)
    {
        if (!pack.ExecutionDialect.Equals("codex-cli-exec-v1", StringComparison.Ordinal))
            throw new InvalidDataException("Codex CLI pack has an unsupported execution dialect.");

        var cli = pack.SubscriptionCli
                  ?? throw new InvalidDataException("Codex CLI pack is missing subscriptionCli settings.");
        ValidateSubscriptionCliCommon(pack, cli);
        if (!cli.AutomaticRoutingEnabled
            || !cli.Command.Equals("codex", StringComparison.Ordinal)
            || !cli.NpmPackage.Equals("@openai/codex", StringComparison.Ordinal)
            || !cli.EnvironmentVariables.SequenceEqual(
                ["SWARMCORE_CODEX_CLI", "TALAVINE_CODEX_CLI", "CODEX_CLI"],
                StringComparer.Ordinal)
            || !cli.LoginArguments.SequenceEqual(["login"], StringComparer.Ordinal)
            || !cli.LoginStatusArguments.SequenceEqual(["login", "status"], StringComparer.Ordinal)
            || !cli.AuthenticatedStatusMarkers.SequenceEqual(["Logged in using ChatGPT"], StringComparer.Ordinal)
            || !cli.RecognizedStatusMarkers.SequenceEqual(
                ["Logged in using", "not logged in"],
                StringComparer.Ordinal)
            || !cli.BuiltInModels.SequenceEqual(
                [
                    "codex-cli:gpt-5.6-luna",
                    "codex-cli:gpt-5.6-terra",
                    "codex-cli:gpt-5.6-sol",
                    "codex-cli:gpt-6-astra",
                    "codex-cli:default",
                ],
                StringComparer.Ordinal)
            || cli.RelativeCapacityWeights.Count != 5
            || !HasRelativeCapacityWeight(cli, "codex-cli:gpt-5.6-luna", 1m)
            || !HasRelativeCapacityWeight(cli, "codex-cli:gpt-5.6-terra", 10m)
            || !HasRelativeCapacityWeight(cli, "codex-cli:gpt-5.6-sol", 20m)
            || !HasRelativeCapacityWeight(cli, "codex-cli:gpt-6-astra", 50m)
            || !HasRelativeCapacityWeight(cli, "codex-cli:default", 10m))
        {
            throw new InvalidDataException("Codex CLI provider identity or authentication contract is invalid.");
        }

        var allowedEfforts = new HashSet<string>(["minimal", "low", "medium", "high", "xhigh"], StringComparer.Ordinal);
        if (cli.ReasoningEfforts.Count == 0
            || cli.ReasoningEfforts.Any(effort => !allowedEfforts.Contains(effort))
            || cli.DiscoveryReasoningEfforts.Any(effort => !cli.ReasoningEfforts.Contains(effort, StringComparer.Ordinal)))
        {
            throw new InvalidDataException("Codex CLI reasoning tiers are invalid.");
        }

        string[] expectedArguments =
        {
            "-c",
            "approval_policy='never'",
            "--disable",
            "apps",
            "--disable",
            "browser_annotation_api",
            "--disable",
            "browser_use",
            "--disable",
            "browser_use_external",
            "--disable",
            "browser_use_full_cdp_access",
            "--disable",
            "computer_use",
            "--disable",
            "hooks",
            "--disable",
            "code_mode",
            "--disable",
            "code_mode_host",
            "--disable",
            "code_mode_only",
            "--disable",
            "shell_tool",
            "--disable",
            "unified_exec",
            "--disable",
            "shell_snapshot",
            "--disable",
            "tool_call_mcp_elicitation",
            "--disable",
            "mcp_2026_07_28",
            "--disable",
            "plugins",
            "--disable",
            "remote_plugin",
            "--disable",
            "plugin_sharing",
            "--disable",
            "multi_agent",
            "--disable",
            "multi_agent_v2",
            "--disable",
            "multi_agent_v2_dynamic_tools",
            "--disable",
            "standalone_web_search",
            "--disable",
            "view_image",
            "--disable",
            "image_generation",
            "--disable",
            "workspace_dependencies",
            "--disable",
            "skill_mcp_dependency_install",
            "--disable",
            "skill_search",
            "--disable",
            "tool_suggest",
            "--disable",
            "in_app_browser",
            "--disable",
            "in_app_local_automation",
            "--disable",
            "in_app_chat",
            "--disable",
            "in_app_voice",
            "--disable",
            "in_app_dictation",
            "--disable",
            "in_app_updates",
            "--disable",
            "auth_elicitation",
            "--disable",
            "request_permissions_tool",
            "--disable",
            "realtime_conversation",
            "exec",
            "--ignore-user-config",
            "--ignore-rules",
            "--cd",
            "{workingDirectory}",
            "--skip-git-repo-check",
            "--ephemeral",
            "--sandbox",
            "read-only",
            "--output-last-message",
            "{outputPath}",
        };
        if (!cli.InferenceArguments.SequenceEqual(expectedArguments, StringComparer.Ordinal)
            || !cli.HelpArguments.SequenceEqual(["--help"], StringComparer.Ordinal)
            || !cli.ModelConfigPaths.SequenceEqual([".codex/config.toml"], StringComparer.Ordinal))
        {
            throw new InvalidDataException("Codex CLI inference template does not preserve required containment controls.");
        }

        if (cli.ModelArgument != "--model"
            || cli.ReasoningConfigFormat != "model_reasoning_effort='{0}'")
        {
            throw new InvalidDataException("Codex CLI model argument contract is invalid.");
        }

        if (cli.ModelConfigPaths.Any(path => string.IsNullOrWhiteSpace(path)
                                             || Path.IsPathRooted(path)
                                             || path.Split(['/', '\\']).Contains("..", StringComparer.Ordinal)))
        {
            throw new InvalidDataException("Codex CLI model evidence paths must stay relative to the user profile.");
        }
    }

    private static bool HasRelativeCapacityWeight(
        SubscriptionCliProviderPack cli,
        string model,
        decimal expected)
        => cli.RelativeCapacityWeights.Any(entry =>
            entry.Key.Equals(model, StringComparison.Ordinal)
            && entry.Value == expected);

    private static void ValidateClaudeCode(ProviderPackDocument pack)
    {
        if (!pack.ExecutionDialect.Equals("claude-code-print-v1", StringComparison.Ordinal))
            throw new InvalidDataException("Claude Code pack has an unsupported execution dialect.");
        var cli = pack.SubscriptionCli
                  ?? throw new InvalidDataException("Claude Code pack is missing subscriptionCli settings.");
        ValidateSubscriptionCliCommon(pack, cli);
        string[] expectedArguments =
        {
            "-p", "--input-format", "text", "--output-format", "text",
            "--max-turns", "{maxTurns}", "--setting-sources", "",
            "--tools", "", "--strict-mcp-config", "--no-chrome", "--no-session-persistence",
        };
        if (!cli.AutomaticRoutingEnabled
            || cli.Command != "claude"
            || cli.NpmPackage != "@anthropic-ai/claude-code"
            || !cli.EnvironmentVariables.SequenceEqual(
                ["SWARMCORE_CLAUDE_CODE_CLI", "TALAVINE_CLAUDE_CLI", "CLAUDE_CODE_CLI", "CLAUDE_CLI"],
                StringComparer.Ordinal)
            || !cli.LoginArguments.SequenceEqual(["auth", "login"], StringComparer.Ordinal)
            || !cli.LoginStatusArguments.SequenceEqual(["auth", "status"], StringComparer.Ordinal)
            || cli.AuthenticatedStatusMarkers.Count != 0
            || !cli.RecognizedStatusMarkers.SequenceEqual(["not logged in", "signed out"], StringComparer.Ordinal)
            || !cli.HelpArguments.SequenceEqual(["--help"], StringComparer.Ordinal)
            || cli.ModelConfigPaths.Count != 0
            || !cli.InferenceArguments.SequenceEqual(expectedArguments, StringComparer.Ordinal)
            || cli.ModelArgument != "--model"
            || cli.RelativeCapacityWeights.Count != 5
            || !HasRelativeCapacityWeight(cli, "claude-code:haiku", 1m)
            || !HasRelativeCapacityWeight(cli, "claude-code:sonnet", 3m)
            || !HasRelativeCapacityWeight(cli, "claude-code:default", 3m)
            || !HasRelativeCapacityWeight(cli, "claude-code:opus", 10m)
            || !HasRelativeCapacityWeight(cli, "claude-code:claude-fable-5", 25m))
        {
            throw new InvalidDataException("Claude Code pack does not preserve its contained print-mode contract.");
        }
    }

    private static void ValidateGeminiCli(ProviderPackDocument pack)
    {
        if (!pack.ExecutionDialect.Equals("gemini-cli-prompt-v1", StringComparison.Ordinal))
            throw new InvalidDataException("Gemini CLI pack has an unsupported execution dialect.");
        var cli = pack.SubscriptionCli
                  ?? throw new InvalidDataException("Gemini CLI pack is missing subscriptionCli settings.");
        ValidateSubscriptionCliCommon(pack, cli);
        if (cli.AutomaticRoutingEnabled
            || cli.Command != "gemini"
            || cli.NpmPackage != "@google/gemini-cli"
            || !cli.EnvironmentVariables.SequenceEqual(
                ["SWARMCORE_GEMINI_CLI", "TALAVINE_GEMINI_CLI", "GEMINI_CLI"],
                StringComparer.Ordinal)
            || cli.LoginArguments.Count != 0
            || cli.LoginStatusArguments.Count != 0
            || cli.AuthenticatedStatusMarkers.Count != 0
            || cli.RecognizedStatusMarkers.Count != 0
            || !cli.HelpArguments.SequenceEqual(["--help"], StringComparer.Ordinal)
            || cli.ModelConfigPaths.Count != 0
            || !cli.InferenceArguments.SequenceEqual(["--skip-trust", "--prompt", "{prompt}"])
            || cli.ModelArgument != "--model")
        {
            throw new InvalidDataException("Gemini CLI pack does not preserve its prompt-mode contract.");
        }
    }

    private static void ValidateSubscriptionCliCommon(
        ProviderPackDocument pack,
        SubscriptionCliProviderPack cli)
    {
        if (cli.DiscoveryPriority is < 0 or > 1000
            || cli.CatalogRefreshDays is < 1 or > 30
            || string.IsNullOrWhiteSpace(cli.Command)
            || string.IsNullOrWhiteSpace(cli.NpmPackage)
            || cli.EnvironmentVariables.Count == 0
            || cli.BuiltInModels.Count == 0
            || !cli.BuiltInModels.Contains(cli.DefaultModel, StringComparer.OrdinalIgnoreCase)
            || cli.BuiltInModels.Any(model => !model.StartsWith(
                pack.ModelPrefix + ":",
                StringComparison.OrdinalIgnoreCase))
            || cli.FoundationProviders.Count == 0
            || cli.RelativeCapacityWeights.Any(entry =>
                string.IsNullOrWhiteSpace(entry.Key)
                || entry.Value <= 0m
                || !cli.BuiltInModels.Contains(entry.Key, StringComparer.OrdinalIgnoreCase))
            || cli.CapabilityAliases.Any(alias => string.IsNullOrWhiteSpace(alias.Key)
                                                   || alias.Value.Count == 0
                                                   || alias.Value.Any(string.IsNullOrWhiteSpace)))
        {
            throw new InvalidDataException($"{pack.ProviderName} subscription CLI metadata is invalid.");
        }
        if (cli.ControlledLaunch is { } controlled
            && (string.IsNullOrWhiteSpace(controlled.HomeEnvironmentVariable)
                || controlled.HomeEnvironmentVariable.Any(character => !char.IsAsciiLetterUpper(character) && !char.IsAsciiDigit(character) && character != '_')
                || controlled.ConfigurationRoots.Count == 0 || controlled.ConfigurationRoots.Any(path =>
                    string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains("..", StringComparison.Ordinal))
                || controlled.FileAuthenticationArguments.Count == 0 || controlled.FileAuthenticationArguments.Any(argument =>
                    string.IsNullOrWhiteSpace(argument) || argument.Contains('{') || argument.Contains('}'))
                || string.IsNullOrWhiteSpace(controlled.AuthenticatedMode)))
            throw new InvalidDataException($"{pack.ProviderName} controlled launch metadata is invalid.");
    }

    private static void ValidateOpenRouter(ProviderPackDocument pack)
    {
        if (!pack.ExecutionDialect.Equals("openrouter-chat-completions-v2", StringComparison.Ordinal))
            throw new InvalidDataException("OpenRouter pack has an unsupported execution dialect.");

        var openRouter = pack.OpenRouter
                         ?? throw new InvalidDataException("OpenRouter pack is missing openRouter settings.");
        if (!Uri.TryCreate(openRouter.BaseUrl, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme != Uri.UriSchemeHttps
            || baseUri.Host is not ("openrouter.ai" or "eu.openrouter.ai")
            || !baseUri.AbsolutePath.TrimEnd('/').EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase)
            || openRouter.ModelsPath != "/models"
            || openRouter.CredentialValidationPath != "/key")
        {
            throw new InvalidDataException("OpenRouter pack must use an approved HTTPS API/catalog endpoint.");
        }

        if (openRouter.DefaultModels.Count == 0
            || !openRouter.DefaultModels.Contains(openRouter.DefaultModel, StringComparer.OrdinalIgnoreCase)
            || openRouter.DefaultModels.Any(InvalidOpenRouterModelValue)
            || openRouter.VisionModelPatterns.Any(InvalidOpenRouterModelValue)
            || openRouter.MinimumReasoningEffort.Any(pair =>
                !openRouter.DefaultModels.Contains(pair.Key, StringComparer.OrdinalIgnoreCase)
                || pair.Value is not ("low" or "medium" or "high"))
            || openRouter.NonChatModelMarkers.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException("OpenRouter model catalog defaults are invalid.");
        }

        if (openRouter.ExecutionEndpoints.Any(endpoint =>
                !IsApprovedEndpoint(endpoint, baseUri.Host)))
        {
            throw new InvalidDataException("OpenRouter execution endpoints are invalid.");
        }

        if (openRouter.CatalogOutputModalities != "text"
            || openRouter.CatalogRefreshDays is < 1 or > 30
            || openRouter.CatalogSorts.Count == 0
            || !openRouter.CatalogSorts.Contains(openRouter.ScoutApiSort, StringComparer.Ordinal)
            || openRouter.CatalogSorts.Any(string.IsNullOrWhiteSpace)
            || openRouter.ScoutMaxPromptPricePerMillion is < 0m or > 100m
            || openRouter.ScoutMaxCompletionPricePerMillion is < 0m or > 500m
            || openRouter.ScoutMinimumContextTokens is < 1024 or > 10_000_000
            || openRouter.ScoutCostQualityTradeoff is < 0 or > 10
            || openRouter.ScoutShortlistSize is < 1 or > 1000)
        {
            throw new InvalidDataException("OpenRouter discovery/qualification bounds are invalid.");
        }
    }

    private static bool InvalidOpenRouterModelValue(string? value)
        => string.IsNullOrWhiteSpace(value)
           || value.Length > 200
           || value.Any(ch => char.IsControl(ch) || char.IsWhiteSpace(ch));

    private static bool IsApprovedEndpoint(string endpoint, string approvedHost)
        => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
           && uri.Scheme == Uri.UriSchemeHttps
           && uri.Host.Equals(approvedHost, StringComparison.OrdinalIgnoreCase)
           && string.IsNullOrEmpty(uri.Query)
           && string.IsNullOrEmpty(uri.Fragment)
           && !string.IsNullOrWhiteSpace(uri.AbsolutePath);

    private static void ValidateDirectApi(ProviderPackDocument pack)
    {
        var direct = pack.DirectApi!;
        var approvedHosts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["OpenAI"] = "api.openai.com",
            ["Claude"] = "api.anthropic.com",
            ["Gemini"] = "generativelanguage.googleapis.com",
            ["Grok"] = "api.x.ai",
            ["DeepSeek"] = "api.deepseek.com",
            ["HuggingFace"] = "router.huggingface.co",
        };
        if (!approvedHosts.TryGetValue(pack.ProviderName, out var approvedHost)
            || !Uri.TryCreate(direct.BaseUrl, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme != Uri.UriSchemeHttps
            || !baseUri.Host.Equals(approvedHost, StringComparison.OrdinalIgnoreCase)
            || direct.ExecutionEndpoints.Any(endpoint => !IsApprovedEndpoint(endpoint, approvedHost))
            || direct.DefaultModels.Count == 0
            || !direct.DefaultModels.Contains(direct.DefaultModel, StringComparer.OrdinalIgnoreCase)
            || direct.DefaultModels.Any(InvalidOpenRouterModelValue)
            || direct.ModelFacts.Any(item =>
                InvalidOpenRouterModelValue(item.Key)
                || !direct.DefaultModels.Contains(item.Key, StringComparer.OrdinalIgnoreCase)
                || !ValidModelFacts(item.Value))
            || direct.PricingAliases.Any(item =>
                InvalidOpenRouterModelValue(item.Key)
                || direct.ModelFacts.ContainsKey(item.Key)
                || !direct.ModelFacts.TryGetValue(item.Value, out var pricedFacts)
                || pricedFacts.InputCostPer1K is null
                || pricedFacts.OutputCostPer1K is null)
            || direct.VisionModelPatterns.Any(InvalidOpenRouterModelValue)
            || direct.NonVisionModelPatterns.Any(InvalidOpenRouterModelValue)
            || direct.CatalogModelPrefixes.Any(InvalidOpenRouterModelValue)
            || direct.PreferredModels.Any(model => !direct.DefaultModels.Contains(model, StringComparer.OrdinalIgnoreCase))
            || direct.ModelScoutCandidates.Any(model => !direct.DefaultModels.Contains(model, StringComparer.OrdinalIgnoreCase))
            || direct.ModelScoutSuggested.Any(model => !direct.ModelScoutCandidates.Contains(model, StringComparer.OrdinalIgnoreCase))
            || direct.AutomaticMeteredCandidates.Any(model =>
                !direct.ModelScoutCandidates.Contains(model, StringComparer.OrdinalIgnoreCase)
                || IsMutableModelAlias(model)
                || !direct.ModelFacts.TryGetValue(model, out var facts)
                || facts.InputCostPer1K is null or <= 0m
                || facts.OutputCostPer1K is null or <= 0m)
            || direct.ModelFacts.Any(item =>
                IsMutableModelAlias(item.Key)
                && (item.Value.InputCostPer1K is not null || item.Value.OutputCostPer1K is not null))
            || direct.QualificationPriority is < 0 or > 1000)
        {
            throw new InvalidDataException($"{pack.ProviderName} direct API provider pack is invalid.");
        }
    }

    private static bool ValidModelFacts(ProviderModelFacts? facts)
        => facts is not null
           && (facts.ExecutionTransport is null or "responses")
           && facts.Transports.Select(t => t.Transport).Distinct(StringComparer.Ordinal).Count() == facts.Transports.Count
           && facts.Transports.All(t => t.Transport is "responses" or "chat-completions"
               && Enum.IsDefined(t.Text) && Enum.IsDefined(t.Streaming) && Enum.IsDefined(t.Tools)
               && Enum.IsDefined(t.Images) && Enum.IsDefined(t.StructuredOutput))
           && (facts.ExecutionTransport is null || facts.Transports.Any(t => t.Transport == facts.ExecutionTransport))
           && (facts.MaxContextTokens is null or >= 1 and <= 10_000_000)
           && (facts.InputCostPer1K is null or >= 0m and <= 10_000m)
           && (facts.OutputCostPer1K is null or >= 0m and <= 10_000m)
           && IsBoundedText(facts.DisplayName, 200)
           && IsBoundedText(facts.Description, 500)
           && IsBoundedText(facts.RecommendedFor, 300);

    private static bool IsBoundedText(string? value, int maxLength)
        => value is null
           || (value.Length <= maxLength && !value.Any(char.IsControl));

    private static bool IsMutableModelAlias(string model)
        => model.EndsWith("-latest", StringComparison.OrdinalIgnoreCase)
           || model.EndsWith("/latest", StringComparison.OrdinalIgnoreCase)
           || model.EndsWith(":latest", StringComparison.OrdinalIgnoreCase);

    private static void RequireIdentifier(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 128
            || value.Any(ch => char.IsControl(ch)))
        {
            throw new InvalidDataException($"Provider pack field '{field}' is invalid.");
        }
    }
}
