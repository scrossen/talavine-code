namespace SwarmCore.Services.Abstractions;

/// <summary>
/// Host-owned path resolver for the observable filesystem surface exposed to
/// authored C# services. The host decides how relative and explicit absolute
/// paths map to the active objective's workspace/authority.
/// </summary>
public interface IServiceFileWorkspace
{
    string Root { get; }
    string Resolve(string path);
}
