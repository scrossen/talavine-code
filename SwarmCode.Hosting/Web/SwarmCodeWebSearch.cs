using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SwarmCore.Coding;
using SwarmCore.Coding.Web;
using SwarmCore.Secrets;
using SwarmCore.Coding.Secrets;

namespace SwarmCode.Hosting.Web;

/// <summary>
/// SwarmCode's web search key: one vault entry (<see cref="SecretName"/>, kind api-key) read on every search,
/// so saving or removing it in Settings or with <c>/key brave</c> takes effect without reopening the workspace.
/// </summary>
public static class SwarmCodeWebSearch
{
    public const string SecretName = "brave-search-api-key";
    public const string KeyUrl = "https://brave.com/search/api/";
    public static CredentialIntakeBinding IntakeBinding { get; } = new(SecretName, ["Brave", "Brave Search"]);

    public const string MissingKeyGuidance =
        "Web search is not configured: no Brave Search API key is stored. Ask the person to add one in Talavine Code Settings > Web search "
        + "(or with /key brave in the console); keys come from " + KeyUrl + ". Until then, use web.fetch for a URL you already know.";

    public static Task<bool> IsConfiguredAsync(ISecretVault vault, CancellationToken ct = default) => vault.HasAsync(SecretName, ct);

    public static Task SaveKeyAsync(ISecretVault vault, string key, CancellationToken ct = default)
    {
        var value = key.Trim();
        if (value.Length == 0) throw new ArgumentException("Paste a Brave Search API key first.");
        return vault.SetAsync(new SecretWriteRequest
        {
            Name = SecretName,
            Value = value,
            Kind = SecretKinds.ApiKey,
            Site = "api.search.brave.com",
            Notes = "Brave Search API key for web.search",
        }, ct);
    }

    public static Task<bool> RemoveKeyAsync(ISecretVault vault, CancellationToken ct = default) => vault.DeleteAsync(SecretName, ct);

    /// <summary>Registers the vault key source and web search services (no catalog publication; see <see cref="AddSwarmCodeWebTools"/>).</summary>
    public static IServiceCollection AddSwarmCodeWebSearch(this IServiceCollection services, IConfiguration? configuration = null)
    {
        services.AddSwarmCodeWebSearchCredentialIntake();
        services.TryAddSingleton<IWebSearchKeySource>(sp => new VaultWebSearchKeySource(sp.GetRequiredService<ISecretVault>()));
        return services.AddSwarmCoreWebSearch(o =>
        {
            o.MissingKeyGuidance = MissingKeyGuidance;
            configuration?.GetSection(WebToolsOptions.SectionName).Bind(o);
        });
    }

    /// <summary>Also used by the desktop's workspace-independent vault composition.</summary>
    public static IServiceCollection AddSwarmCodeWebSearchCredentialIntake(this IServiceCollection services)
    {
        if (!services.Any(d => ReferenceEquals(d.ImplementationInstance, IntakeBinding))) services.AddSingleton(IntakeBinding);
        return services;
    }

    /// <summary><c>web.search</c> and <c>web.fetch</c> for every workspace's API lanes, and the guidance in the coding instructions.</summary>
    public static IServiceCollection AddSwarmCodeWebTools(this IServiceCollection services, IConfiguration? configuration = null)
    {
        services.AddSwarmCodeWebSearch(configuration);
        services.AddSwarmCoreWebTools();
        services.PostConfigure<CodingOptions>(coding => coding.AdditionalInstructions = string.IsNullOrWhiteSpace(coding.AdditionalInstructions)
            ? WebTools.Guidance
            : coding.AdditionalInstructions.Trim() + Environment.NewLine + WebTools.Guidance);
        return services;
    }
}

/// <summary>Reads the Brave key from the vault at call time.</summary>
public sealed class VaultWebSearchKeySource(ISecretVault vault) : IWebSearchKeySource
{
    public Task<string?> GetKeyAsync(CancellationToken ct) => vault.GetValueAsync(SwarmCodeWebSearch.SecretName, ct);
}
