using System.ComponentModel;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;
using SwarmCore.Skills.Abstractions;
using SwarmCore.Skills.FileSkills;
using SwarmCore.Skills.Models;

namespace SwarmCore.Agent.Skills;

/// <summary>
/// Tool ids for the shared Agent Skills surface. Stable across host applications so a skill
/// authored once reads the same whether it runs in Talavine, Vibes, or the CRM.
/// </summary>
public static class AgentSkillToolIds
{
    public const string Search = "skills.search";
    public const string Run = "skills.run";
    public const string ReadResource = "skills.read_resource";
}

/// <summary>
/// Builds the three-tool Agent Skills surface — discover, run, read reference material —
/// for any host with a skill registry and executor.
///
/// This lives in SwarmCore rather than in one app because the whole point of the
/// standard (https://agentskills.io) is that a skill is portable. A skill that only the
/// email client can run is just a prompt template; the same SKILL.md reachable from every
/// app in the family is a capability the user installs once.
///
/// Hosts with their own privacy or account-scoping rules pass an <paramref name="authorize"/>
/// delegate; Talavine supplies its agent-gateway guard that way rather than forking these
/// tools. Hosts without one (Vibes today) get the default allow.
/// </summary>
public static class AgentSkillTools
{
    public sealed class SearchInput
    {
        [Description("Natural-language description of the goal — e.g. 'draft a follow-up after a meeting'. Names, keywords, and descriptions are all matched.")]
        public required string Query { get; set; }

        [Description("Max number of matches. Default 5, max 20.")]
        public int? TopK { get; set; }
    }

    public sealed class SkillMatch
    {
        public required string SkillId { get; init; }
        public required string Name { get; init; }
        public required string Description { get; init; }
        public required double Relevance { get; init; }
        public bool Enabled { get; init; }
        public string TrustLevel { get; init; } = "";
    }

    public sealed class SearchOutput
    {
        public required bool Success { get; init; }
        public required List<SkillMatch> Matches { get; init; }
        public int TotalRegistered { get; init; }
        public string? ErrorMessage { get; init; }
    }

    public sealed class RunInput
    {
        [Description("Skill id to invoke, from skills.search.")]
        public required string SkillId { get; set; }

        [Description("Function name within the skill. Omit to use its only function.")]
        public string? FunctionName { get; set; }

        [Description("Arguments for the skill function, as a name/value map.")]
        public Dictionary<string, string>? Arguments { get; set; }
    }

    public sealed class RunOutput
    {
        public required bool Success { get; init; }
        public required string SkillId { get; init; }
        public string FunctionName { get; init; } = "";
        public object? Result { get; init; }
        public string? ErrorMessage { get; init; }
    }

    public sealed class ReadResourceInput
    {
        [Description("Skill id that owns the resource.")]
        public required string SkillId { get; set; }

        [Description("Bundled file path relative to the skill root, e.g. 'references/policy.md'. Omit to list what the skill bundles.")]
        public string? Path { get; set; }
    }

    public sealed class ResourceEntry
    {
        public required string Path { get; init; }
        public required string Kind { get; init; }
        public required long SizeBytes { get; init; }
    }

    public sealed class ReadResourceOutput
    {
        public required bool Success { get; init; }
        public string? Path { get; init; }
        public string? Content { get; init; }
        public bool Truncated { get; init; }
        public List<ResourceEntry> Available { get; init; } = [];
        public string? ErrorMessage { get; init; }
    }

    /// <summary>Host authorization hook. Return null to allow, or a message to refuse.</summary>
    public delegate string? SkillAuthorization(string toolId, ISkill? skill);

    /// <param name="registry">Skill registry to search and resolve from.</param>
    /// <param name="executor">Executor that runs skills under the host's policy and consent flow.</param>
    /// <param name="authorize">Optional host authorization hook.</param>
    /// <param name="ownerAgentId">Owner id stamped on the descriptors.</param>
    /// <param name="sessionIdProvider">
    /// Resolves the conversation id for the current call, used as
    /// <see cref="SkillExecutionContext.SessionId"/> so a "this conversation" consent
    /// grant is honoured on the next call instead of prompting again. Defaults to the
    /// ambient <see cref="AsyncLocalCurrentTurnContextAccessor"/> set by the turn runner.
    /// </param>
    public static IReadOnlyList<ToolDescriptor> Build(
        ISkillRegistry registry,
        ISkillExecutor executor,
        SkillAuthorization? authorize = null,
        string ownerAgentId = "skills",
        Func<string?>? sessionIdProvider = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(executor);

        sessionIdProvider ??= static () => AsyncLocalCurrentTurnContextAccessor.Ambient?.ConversationId;

        return
        [
            BuildSearch(registry, authorize, ownerAgentId),
            BuildRun(registry, executor, authorize, ownerAgentId, sessionIdProvider),
            BuildReadResource(registry, authorize, ownerAgentId),
        ];
    }

    private static ToolDescriptor BuildSearch(
        ISkillRegistry registry,
        SkillAuthorization? authorize,
        string ownerAgentId) =>
        ToolDescriptor.Create<SearchInput, SearchOutput>(
            toolId: AgentSkillToolIds.Search,
            ownerAgentId: ownerAgentId,
            description:
                "Search installed skills for a vetted playbook matching a goal. A skill composes "
                + "several steps into a tested workflow, so check here before working out a "
                + "recurring task from scratch. Disabled skills are returned too, so you can tell "
                + "the user a skill exists and offer to turn it on.",
            cost: ToolCostClass.Trivial,
            sideEffect: ToolSideEffect.ReadOnly,
            keywords: ["skill", "playbook", "workflow", "recipe", "how do i", "search"],
            invoke: async (input, ct) =>
            {
                if (authorize?.Invoke(AgentSkillToolIds.Search, null) is { } refusal)
                    return new SearchOutput { Success = false, Matches = [], ErrorMessage = refusal };

                var results = await registry.SearchAsync(
                    input.Query,
                    new SkillSearchOptions
                    {
                        Limit = Math.Clamp(input.TopK ?? 5, 1, 20),
                        OnlyEnabled = false,
                        MinRelevance = 0.1,
                    },
                    ct);

                return new SearchOutput
                {
                    Success = true,
                    TotalRegistered = registry.GetAllSkills().Count,
                    Matches = results.Select(r => new SkillMatch
                    {
                        SkillId = r.SkillId,
                        Name = r.Name,
                        Description = r.Description,
                        Relevance = r.Relevance,
                        Enabled = registry.IsEnabled(r.SkillId),
                        TrustLevel = r.TrustLevel.ToString(),
                    }).ToList(),
                };
            });

    private static ToolDescriptor BuildRun(
        ISkillRegistry registry,
        ISkillExecutor executor,
        SkillAuthorization? authorize,
        string ownerAgentId,
        Func<string?> sessionIdProvider) =>
        ToolDescriptor.Create<RunInput, RunOutput>(
            toolId: AgentSkillToolIds.Run,
            ownerAgentId: ownerAgentId,
            description:
                "Run an installed skill by id, with arguments. Find the id with skills.search first. "
                + "A one-shot skill returns its finished result. An operational SKILL.md returns a workflow activation; "
                + "continue the same turn, follow its WorkflowInstructions, and call the governed AllowedTools before reporting an outcome.",
            cost: ToolCostClass.Moderate,
            sideEffect: ToolSideEffect.WritesLocal,
            keywords: ["skill", "run", "invoke", "playbook"],
            invoke: async (input, ct) =>
            {
                var skill = registry.GetSkill(input.SkillId);
                var functionName = ResolveFunctionName(skill, input.FunctionName);

                if (authorize?.Invoke(AgentSkillToolIds.Run, skill) is { } refusal)
                {
                    return new RunOutput
                    {
                        Success = false,
                        SkillId = input.SkillId,
                        FunctionName = functionName,
                        ErrorMessage = refusal,
                    };
                }

                if (skill is null)
                {
                    return new RunOutput
                    {
                        Success = false,
                        SkillId = input.SkillId,
                        FunctionName = functionName,
                        ErrorMessage = $"No skill is installed with id '{input.SkillId}'.",
                    };
                }

                // Carry the conversation id so a consent grant scoped to "this
                // conversation" is found again on the next call. Without it the
                // consent service had nothing to key the grant on and the user was
                // re-prompted every time.
                string? sessionId;
                try
                {
                    sessionId = sessionIdProvider();
                }
                catch
                {
                    sessionId = null;
                }

                var context = new SkillExecutionContext
                {
                    SessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId,
                    Parameters = input.Arguments?.ToDictionary(kv => kv.Key, object (kv) => kv.Value) ?? [],
                    CancellationToken = ct,
                };

                var result = await executor.ExecuteAsync(input.SkillId, functionName, context, ct)
                             ?? SkillResult.Fail("The skill executor returned no result.");

                return new RunOutput
                {
                    Success = result.Success,
                    SkillId = input.SkillId,
                    FunctionName = functionName,
                    Result = result.Result,
                    ErrorMessage = result.ErrorMessage,
                };
            });

    private static ToolDescriptor BuildReadResource(
        ISkillRegistry registry,
        SkillAuthorization? authorize,
        string ownerAgentId) =>
        ToolDescriptor.Create<ReadResourceInput, ReadResourceOutput>(
            toolId: AgentSkillToolIds.ReadResource,
            ownerAgentId: ownerAgentId,
            description:
                "Read a reference or asset file bundled with a skill. Skills list their supporting "
                + "files without loading them; call this only when the task needs one.",
            cost: ToolCostClass.Trivial,
            sideEffect: ToolSideEffect.ReadOnly,
            keywords: ["skill", "reference", "resource", "documentation"],
            invoke: async (input, ct) =>
            {
                var skill = registry.GetSkill(input.SkillId);

                if (authorize?.Invoke(AgentSkillToolIds.ReadResource, skill) is { } refusal)
                    return new ReadResourceOutput { Success = false, ErrorMessage = refusal };

                if (skill is not FileBasedSkill fileSkill)
                {
                    return new ReadResourceOutput
                    {
                        Success = false,
                        ErrorMessage = $"Skill '{input.SkillId}' bundles no files.",
                    };
                }

                var manifest = fileSkill.Manifest;
                var available = manifest.Resources
                    .Where(r => r.Kind is SkillResourceKind.Reference or SkillResourceKind.Asset)
                    .Select(r => new ResourceEntry
                    {
                        Path = r.RelativePath,
                        Kind = r.Kind.ToString(),
                        SizeBytes = r.SizeBytes,
                    })
                    .ToList();

                if (string.IsNullOrWhiteSpace(input.Path))
                    return new ReadResourceOutput { Success = true, Available = available };

                var read = await SkillResourceReader.ReadAsync(manifest, input.Path, ct);

                return new ReadResourceOutput
                {
                    Success = read.Success,
                    Path = read.RelativePath,
                    Content = read.Success ? read.Content : null,
                    Truncated = read.Truncated,
                    Available = available,
                    ErrorMessage = read.ErrorMessage,
                };
            });

    private static string ResolveFunctionName(ISkill? skill, string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return requested.Trim();

        var functions = skill?.GetFunctions().ToList();
        return functions is { Count: 1 } ? functions[0].Name : "Invoke";
    }
}
