namespace SwarmCore.Agent.Catalog;

/// <summary>
/// A tool result that a decorator annotated (hook notes, for example) without changing what the tool returned. The
/// model reads the serialized annotation; every type check (execution evidence, images, failure-as-data) must look at
/// <see cref="Result"/>, the tool's own object, through <see cref="ToolResultAnnotations.Unwrap"/>.
/// </summary>
public interface IAnnotatedToolResult
{
    object? Result { get; }
}

public static class ToolResultAnnotations
{
    /// <summary>The tool's own result under any annotation wrappers (a decorator may wrap another).</summary>
    public static object? Unwrap(object? value)
    {
        for (var depth = 0; depth < 16 && value is IAnnotatedToolResult annotated; depth++) value = annotated.Result;
        return value;
    }
}
