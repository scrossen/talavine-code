namespace SwarmCore.Skills.Abstractions;

/// <summary>
/// Marks an ISkill implementation as NOT auto-discoverable by
/// <c>SkillRegistry.DiscoverSkillsAsync</c>. Use for skill types that are
/// constructed on-demand with runtime parameters (e.g. a manifest + assembly
/// pair) and cannot be activated from the DI container alone.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class SkillDiscoveryIgnoredAttribute : Attribute
{
}
