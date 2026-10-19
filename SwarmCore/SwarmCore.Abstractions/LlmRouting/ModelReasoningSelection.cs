namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>Serialization of an explicit effort alongside an unchanged model identity.</summary>
public static class ModelReasoningSelection
{
    private static readonly HashSet<string> Levels = new(StringComparer.OrdinalIgnoreCase)
        { "minimal", "low", "medium", "high", "xhigh", "max", "ultra", "none" };
    public static (string Model, string? Effort) Split(string model)
    {
        var at = model.LastIndexOf('@');
        return at > 0 && Levels.Contains(model[(at + 1)..])
            ? (model[..at], model[(at + 1)..].ToLowerInvariant()) : (model, null);
    }
    public static string Combine(string model, string? effort)
        => string.IsNullOrWhiteSpace(effort) ? Split(model).Model : Split(model).Model + "@" + effort;
}
