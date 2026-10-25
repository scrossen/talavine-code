using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SwarmCore.RemoteControl.Agent;

namespace SwarmCore.RemoteControl.Platform;

/// <summary>
/// Registers desktop control for a host: <see cref="IDesktopAutomation"/> (the platform adapter, created on first use
/// and disposed with the container), one <see cref="RemoteControlSession"/>, <see cref="DesktopControlTools"/> and
/// <see cref="DesktopProcedureRunner"/>. Every registration is TryAdd, so a host or test can supply its own adapter.
/// The host keeps the gates (packaging policy, user setting) and the procedure store (its existing
/// <c>IAgentProcedureStore</c>, passed to the runner per call).
/// </summary>
public static class DesktopControlServiceCollectionExtensions
{
    /// <summary>Most rows a control listing's text shows to an agent (Data keeps every row).</summary>
    public const int AgentListingMaxRows = 60;
    /// <summary>Longest control name or value shown to an agent before it is cut.</summary>
    public const int AgentListingNameMaxLength = 90;

    /// <summary>
    /// Session settings for agent hosts: screenshots embedded for the model (downscaled by <see cref="ScreenshotScaler"/>
    /// to <see cref="ScreenshotScaler.DefaultMaxSide"/>; the full capture stays on disk) and compact control listings.
    /// </summary>
    public static RemoteControlSessionOptions AgentSessionOptions(string captureDirectory) => new()
    {
        CaptureDirectory = captureDirectory,
        EmbedScreenshotBase64 = true,
        ScreenshotScaler = ScreenshotScaler.Scale,
        EmbeddedImageMaxSide = ScreenshotScaler.DefaultMaxSide,
        ListingMaxRows = AgentListingMaxRows,
        ListingNameMaxLength = AgentListingNameMaxLength,
    };

    public static IServiceCollection AddDesktopControl(this IServiceCollection services,
        Func<IServiceProvider, string> captureDirectory,
        Func<IServiceProvider, string?>? unavailableReason = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(captureDirectory);
        services.TryAddSingleton<IDesktopAutomation>(sp =>
            DesktopAutomationFactory.CreateLazy(unavailableReason is null ? null : () => unavailableReason(sp)));
        services.TryAddSingleton(sp => AgentSessionOptions(captureDirectory(sp)));
        services.TryAddSingleton(sp => new RemoteControlSession(sp.GetRequiredService<IDesktopAutomation>(),
            sp.GetRequiredService<RemoteControlSessionOptions>()));
        services.TryAddSingleton(sp => new DesktopControlTools(sp.GetRequiredService<RemoteControlSession>(),
            sp.GetService<IDesktopSecretSlots>()));
        services.TryAddSingleton(sp => new DesktopProcedureRunner(sp.GetRequiredService<DesktopControlTools>()));
        return services;
    }
}
