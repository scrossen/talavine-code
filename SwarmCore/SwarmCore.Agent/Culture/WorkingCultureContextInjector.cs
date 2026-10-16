using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SwarmCore.Agent.Runtime;

namespace SwarmCore.Agent.Culture;

/// <summary>
/// Appends the shared working culture to every turn's system prompt. The
/// stored profile wins, including an explicitly blank name. Before the first
/// save, the host's principal resolver supplies the default identity. Failures degrade to the default charter
/// rather than dropping the culture, because a turn without it would read
/// differently from every other turn.
/// </summary>
public sealed class WorkingCultureContextInjector : IContextInjector
{
    private readonly IAgentCultureStore _store;
    private readonly IAgentCulturePrincipalResolver? _principals;
    private readonly ILogger<WorkingCultureContextInjector> _logger;

    public WorkingCultureContextInjector(
        IAgentCultureStore store,
        IAgentCulturePrincipalResolver? principals = null,
        ILogger<WorkingCultureContextInjector>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _principals = principals;
        _logger = logger ?? NullLogger<WorkingCultureContextInjector>.Instance;
    }

    public string Name => AgentCultureCharter.InjectorName;

    public async Task<string?> BuildAsync(ContextInjectionRequest request, CancellationToken ct = default)
    {
        var profile = await ResolveAsync(ct);
        return AgentCultureCharter.Build(profile);
    }

    /// <summary>The effective culture: saved choice, or the host principal before the first save.</summary>
    public async Task<AgentCultureProfile> ResolveAsync(CancellationToken ct = default)
    {
        AgentCultureProfile? profile = null;
        try
        {
            profile = await _store.GetAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the agent culture profile; using the default charter.");
        }

        if (profile is not null || _principals is null)
        {
            return AgentCultureProfile.Resolve(profile, null);
        }

        try
        {
            var principal = await _principals.ResolveAsync(ct);
            if (principal is not null && !string.IsNullOrWhiteSpace(principal.Name))
            {
                return AgentCultureProfile.Resolve(null, principal);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve a default culture principal; the charter stays generic.");
        }

        return AgentCultureProfile.Empty;
    }
}
