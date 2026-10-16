using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using Microsoft.CodeAnalysis;

namespace SwarmCore.Scripting;

/// <summary>
/// Builds Roslyn <see cref="MetadataReference"/>s for assemblies that are already loaded in
/// this process. On desktop an assembly has a file on disk, so the reference is created from
/// its path. On Android/iOS (assemblies embedded in the app bundle) and in single-file
/// publishes, <see cref="Assembly.Location"/> is empty or names a file that doesn't exist —
/// there the metadata is read straight out of the loaded runtime image via
/// <see cref="System.Reflection.Metadata.AssemblyExtensions.TryGetRawMetadata"/>. Roslyn's own
/// scripting resolvers are file-path-only, which is why hosts here must never hand raw
/// <see cref="Assembly"/> objects to <c>ScriptOptions.WithReferences</c> on mobile.
/// </summary>
public static class RuntimeMetadataReferences
{
    private static readonly ConcurrentDictionary<Assembly, PortableExecutableReference?> Cache = new();

    /// <summary>Create a reference for <paramref name="assembly"/>, or throw when neither a
    /// disk image nor raw runtime metadata is available (dynamic assemblies, trimmed metadata).</summary>
    public static PortableExecutableReference Create(Assembly assembly) =>
        TryCreate(assembly) ?? throw new NotSupportedException(
            $"Cannot create a metadata reference for '{assembly.FullName}': it has no file on disk and the runtime did not expose its raw metadata.");

    /// <summary>Like <see cref="Create"/> but returns null instead of throwing.</summary>
    public static PortableExecutableReference? TryCreate(Assembly assembly)
    {
        if (assembly.IsDynamic) return null;
        return Cache.GetOrAdd(assembly, static asm =>
        {
            var location = asm.Location;
            if (!string.IsNullOrEmpty(location) && File.Exists(location))
                return MetadataReference.CreateFromFile(location);
            return CreateFromRawMetadata(asm);
        });
    }

    private static unsafe PortableExecutableReference? CreateFromRawMetadata(Assembly assembly)
    {
        // The blob points into the runtime's loaded image, which stays alive for the process
        // lifetime, so the reference reads it lazily without copying.
        if (!assembly.TryGetRawMetadata(out byte* blob, out int length))
            return null;
        var module = ModuleMetadata.CreateFromMetadata((IntPtr)blob, length);
        return AssemblyMetadata.Create(module).GetReference(display: assembly.FullName);
    }
}

/// <summary>
/// Resolves assembly identities that surface during binding but aren't in a host's explicit
/// reference list — the facade assemblies (System.Runtime and friends) that BCL assembly refs
/// point through. Roslyn scripting did this with its file-path-based runtime resolver; this one
/// loads the assembly through the runtime instead, so it also works where assemblies have no
/// disk location. <c>#r</c>/file references stay unresolvable on purpose: agent scripts must
/// not pull in arbitrary assemblies.
/// </summary>
public sealed class LoadedAssemblyMissingResolver : MetadataReferenceResolver
{
    public static readonly LoadedAssemblyMissingResolver Instance = new();

    private LoadedAssemblyMissingResolver() { }

    public override bool ResolveMissingAssemblies => true;

    public override PortableExecutableReference? ResolveMissingAssembly(MetadataReference definition, AssemblyIdentity referenceIdentity)
    {
        try
        {
            return RuntimeMetadataReferences.TryCreate(Assembly.Load(referenceIdentity.GetDisplayName()));
        }
        catch
        {
            return null; // not part of this app — the script can't use those types anyway
        }
    }

    public override ImmutableArray<PortableExecutableReference> ResolveReference(
        string reference, string? baseFilePath, MetadataReferenceProperties properties) =>
        ImmutableArray<PortableExecutableReference>.Empty;

    public override bool Equals(object? other) => ReferenceEquals(this, other);
    public override int GetHashCode() => typeof(LoadedAssemblyMissingResolver).GetHashCode();
}
