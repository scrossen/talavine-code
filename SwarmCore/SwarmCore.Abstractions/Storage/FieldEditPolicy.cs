namespace SwarmCore.Abstractions.Storage;

/// <summary>Three-way edit policy. Null and false are values, not an instruction to skip a field.</summary>
public static class FieldEditPolicy
{
    public static bool WasEdited<T>(T original, T requested, Func<T, T, bool> equal) =>
        !equal(original, requested);

    public static bool Conflicts<T>(T original, T current, T requested, Func<T, T, bool> equal) =>
        !equal(original, current) && !equal(current, requested);
}
