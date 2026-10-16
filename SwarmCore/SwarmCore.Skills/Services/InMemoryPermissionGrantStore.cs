using System.Collections.Concurrent;

using SwarmCore.Skills.Abstractions;

namespace SwarmCore.Skills.Services;

/// <summary>
/// The default grant store: remembers "Always allow" for the life of the process and no longer.
/// A host that wants grants to survive a restart registers its own <see cref="IPermissionGrantStore"/>
/// backed by its database; whichever the host registers wins over this one.
///
/// <para>
/// Forgetting on restart is the safe direction to fail — the user is asked again rather than
/// silently keeping a grant nobody can see. What is not safe is having no store at all: the
/// consent service takes one as a constructor dependency, so a host without this default cannot
/// resolve <see cref="ISkillExecutor"/>, and every skill call fails.
/// </para>
/// </summary>
public sealed class InMemoryPermissionGrantStore : IPermissionGrantStore
{
    private readonly ConcurrentDictionary<string, byte> _granted = new(StringComparer.Ordinal);

    public Task<IReadOnlySet<string>> GetGrantedPermissionsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlySet<string>>(_granted.Keys.ToHashSet(StringComparer.Ordinal));

    public Task GrantAsync(IEnumerable<string> permissionIds, CancellationToken ct = default)
    {
        foreach (var id in permissionIds)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                _granted[id] = 0;
            }
        }

        return Task.CompletedTask;
    }

    public Task RevokeAsync(string permissionId, CancellationToken ct = default)
    {
        _granted.TryRemove(permissionId, out _);
        return Task.CompletedTask;
    }
}
