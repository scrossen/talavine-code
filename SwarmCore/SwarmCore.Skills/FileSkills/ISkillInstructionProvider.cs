using SwarmCore.Skills.Abstractions;

namespace SwarmCore.Skills.FileSkills;

/// <summary>
/// Provides skill instructions for injection into LLM prompts.
/// This is the primary integration point between the file-based skills system and agents.
/// Agents call this to discover and include relevant skill instructions in their prompts.
/// </summary>
public interface ISkillInstructionProvider
{
    /// <summary>
    /// Get skill instructions relevant to a task description, ranked by relevance.
    /// </summary>
    /// <param name="taskDescription">Description of the task being performed.</param>
    /// <param name="maxSkills">Maximum number of skills to return.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<SkillInstruction>> GetRelevantInstructionsAsync(
        string taskDescription,
        int maxSkills = 5,
        CancellationToken ct = default);

    /// <summary>
    /// Get the raw instructions for a specific skill by ID.
    /// </summary>
    Task<string?> GetInstructionsAsync(string skillId, CancellationToken ct = default);

    /// <summary>
    /// Augment a base prompt with relevant skill instructions based on task description.
    /// </summary>
    /// <param name="basePrompt">The original prompt.</param>
    /// <param name="taskDescription">Description of the task (used to find relevant skills).</param>
    /// <param name="maxSkills">Maximum skills to include.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<string> BuildAugmentedPromptAsync(
        string basePrompt,
        string taskDescription,
        int maxSkills = 3,
        CancellationToken ct = default);

    /// <summary>
    /// Get all available skill instructions (for enumeration/UI).
    /// </summary>
    IReadOnlyList<SkillInstruction> GetAllInstructions();
}

/// <summary>
/// Default implementation that sources instructions from file-based skills in the registry.
/// </summary>
public class SkillInstructionProvider : ISkillInstructionProvider
{
    private readonly ISkillRegistry _registry;

    public SkillInstructionProvider(ISkillRegistry registry)
    {
        _registry = registry;
    }

    public async Task<IReadOnlyList<SkillInstruction>> GetRelevantInstructionsAsync(
        string taskDescription,
        int maxSkills = 5,
        CancellationToken ct = default)
    {
        // Use the registry's built-in search to find relevant skills
        var searchResults = await _registry.SearchAsync(taskDescription, new Models.SkillSearchOptions
        {
            Limit = maxSkills,
            MinRelevance = 0.1,
            OnlyEnabled = true
        }, ct);

        var instructions = new List<SkillInstruction>();

        foreach (var result in searchResults)
        {
            var skill = _registry.GetSkill(result.SkillId);
            if (skill is null) continue;

            var instructionText = GetSkillInstructions(skill);
            if (string.IsNullOrWhiteSpace(instructionText)) continue;

            instructions.Add(new SkillInstruction(
                SkillId: result.SkillId,
                Name: result.Name,
                Description: result.Description,
                Instructions: instructionText,
                Relevance: (float)result.Relevance));
        }

        return instructions;
    }

    public Task<string?> GetInstructionsAsync(string skillId, CancellationToken ct = default)
    {
        var skill = _registry.GetSkill(skillId);
        if (skill is null) return Task.FromResult<string?>(null);

        return Task.FromResult<string?>(GetSkillInstructions(skill));
    }

    public async Task<string> BuildAugmentedPromptAsync(
        string basePrompt,
        string taskDescription,
        int maxSkills = 3,
        CancellationToken ct = default)
    {
        var relevantSkills = await GetRelevantInstructionsAsync(taskDescription, maxSkills, ct);
        return SkillPromptBuilder.AugmentPrompt(basePrompt, relevantSkills);
    }

    public IReadOnlyList<SkillInstruction> GetAllInstructions()
    {
        var instructions = new List<SkillInstruction>();

        foreach (var skill in _registry.GetAllSkills())
        {
            if (!_registry.IsEnabled(skill.SkillId)) continue;

            var instructionText = GetSkillInstructions(skill);
            if (string.IsNullOrWhiteSpace(instructionText)) continue;

            instructions.Add(new SkillInstruction(
                SkillId: skill.SkillId,
                Name: skill.Name,
                Description: skill.Description,
                Instructions: instructionText,
                Relevance: 1.0f));
        }

        return instructions;
    }

    /// <summary>
    /// Extract instructions from a skill. File-based skills have markdown instructions;
    /// C# skills get a generated description from their function definitions.
    /// </summary>
    private static string GetSkillInstructions(ISkill skill)
    {
        // File-based skills have rich markdown instructions
        if (skill is FileBasedSkill fileSkill)
            return fileSkill.Instructions;

        // For C# skills, generate a description from the function definitions
        var functions = skill.GetFunctions().ToList();
        if (functions.Count == 0)
            return skill.Description;

        var lines = new List<string> { skill.Description, "" };
        foreach (var func in functions)
        {
            lines.Add($"- **{func.Name}**: {func.Description}");
            foreach (var param in func.Parameters)
            {
                var req = param.IsRequired ? "required" : "optional";
                lines.Add($"  - {param.Name} ({param.Type}, {req}): {param.Description}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }
}
