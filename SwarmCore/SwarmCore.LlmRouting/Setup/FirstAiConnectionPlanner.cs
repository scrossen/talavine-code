using SwarmCore.LlmRouting.Configuration;
using SwarmCore.LlmRouting.Services;

namespace SwarmCore.LlmRouting.Setup;

/// <summary>
/// The ways a new user can give an app its first working AI, named for what the
/// user does rather than for the provider plumbing behind it.
/// </summary>
public enum FirstAiConnectionPath
{
    /// <summary>Sign in with a ChatGPT or Claude plan the user already pays for.</summary>
    Subscription,

    /// <summary>Download a free model that runs on this computer.</summary>
    LocalModel,

    /// <summary>Paste a key from a provider account that bills per use.</summary>
    ApiKey,
}

/// <summary>Where one path stands on this computer.</summary>
public enum FirstAiPathAvailability
{
    /// <summary>A usable chat connection on this path already exists.</summary>
    Connected,

    /// <summary>The one path setup leads with.</summary>
    Recommended,

    /// <summary>Offered, but not the lead.</summary>
    Available,

    /// <summary>Works here, but slowly; offered only after the API-key path.</summary>
    Slow,

    /// <summary>Cannot work on this computer right now; shown only to explain why.</summary>
    Unavailable,
}

/// <summary>
/// The accelerator a local runtime can use. Only NVIDIA CUDA and Apple Silicon
/// count toward acceptable local speed, matching the app's existing hardware
/// classification; other GPUs are reported but not relied on.
/// </summary>
public enum FirstAiLocalAccelerator
{
    None,
    NvidiaCuda,
    AppleSilicon,
    OtherGpu,
}

public enum FirstAiLocalRuntimeState
{
    Unknown,
    NotInstalled,
    InstalledNotRunning,
    Running,
}

/// <summary>Outcome of asking Model Scout for a tested local chat model.</summary>
public enum FirstAiLocalModelLookup
{
    /// <summary>A website-qualified model fits this computer.</summary>
    Found,

    /// <summary>The tested-model list was read, but nothing on it fits this computer.</summary>
    NoneFits,

    /// <summary>The tested-model list could not be read (offline, or stale).</summary>
    CatalogUnavailable,
}

/// <param name="ProviderName">A <see cref="SubscriptionCliProviders"/> name.</param>
/// <param name="Stage">The last setup probe, or null when it has not been probed.</param>
/// <param name="PausedByUser">The user turned this provider off; readiness never overrides that.</param>
/// <param name="CanInstall">The host can set this provider's connection up by itself
/// (for example with a runtime bundled beside the app).</param>
public sealed record FirstAiSubscriptionFact(
    string ProviderName,
    SubscriptionCliStage? Stage,
    bool PausedByUser = false,
    bool CanInstall = true);

/// <param name="TotalRamGib">Physical memory. Classification uses 7.5 and 15.5 GiB so an
/// "8 GB" or "16 GB" machine that reports slightly less still counts.</param>
/// <param name="EffectiveModelMemoryGb">Memory a local model may use, from the host's budget.</param>
/// <param name="GpuMemoryGb">Dedicated GPU memory; zero for unified memory or none.</param>
public sealed record FirstAiLocalHardware(
    double TotalRamGib,
    int EffectiveModelMemoryGb,
    FirstAiLocalAccelerator Accelerator,
    int GpuMemoryGb = 0);

/// <param name="ModelName">Exact runtime tag Model Scout qualified.</param>
/// <param name="RequiredMemoryGb">Memory the model needs to run.</param>
/// <param name="DownloadBytes">Artifact size, or zero when unknown.</param>
/// <param name="Installed">The exact tag is already on this computer.</param>
public sealed record FirstAiLocalModelCandidate(
    string ModelName,
    int RequiredMemoryGb,
    long DownloadBytes,
    bool Installed);

/// <param name="Hardware">Detected hardware, or null when detection failed.</param>
/// <param name="CanInstallRuntimeAutomatically">The host can install the runtime without the user
/// downloading it (for example through an approved package manager).</param>
/// <param name="ManagedElsewhere">The user turned local AI off or pointed it at another computer.</param>
public sealed record FirstAiLocalFacts(
    FirstAiLocalHardware? Hardware,
    FirstAiLocalRuntimeState Runtime,
    bool CanInstallRuntimeAutomatically,
    bool ManagedElsewhere,
    FirstAiLocalModelLookup ModelLookup,
    FirstAiLocalModelCandidate? Model);

/// <param name="Subscriptions">Lead subscription providers the host supports on this
/// platform, in the order to offer them. Empty when none are supported.</param>
/// <param name="ConnectedPaths">Paths that already have a usable chat connection.</param>
public sealed record FirstAiConnectionFacts(
    IReadOnlyList<FirstAiSubscriptionFact> Subscriptions,
    FirstAiLocalFacts Local,
    IReadOnlyCollection<FirstAiConnectionPath> ConnectedPaths);

/// <param name="Reason">A stable <see cref="FirstAiConnectionReasons"/> code for copy and tests.</param>
/// <param name="SuggestedProvider">For a subscription, the provider closest to ready.</param>
/// <param name="LocalModel">For the local path, the model it would use.</param>
public sealed record FirstAiConnectionOption(
    FirstAiConnectionPath Path,
    FirstAiPathAvailability Availability,
    string Reason,
    string? SuggestedProvider = null,
    FirstAiLocalModelCandidate? LocalModel = null);

/// <summary>Every path, in the order setup should present them.</summary>
public sealed record FirstAiConnectionPlan(IReadOnlyList<FirstAiConnectionOption> Options)
{
    /// <summary>The path setup leads with; null once any path is connected.</summary>
    public FirstAiConnectionOption? Lead =>
        Options.FirstOrDefault(option => option.Availability == FirstAiPathAvailability.Recommended);

    public bool HasConnection =>
        Options.Any(option => option.Availability == FirstAiPathAvailability.Connected);

    public FirstAiConnectionOption Get(FirstAiConnectionPath path) =>
        Options.First(option => option.Path == path);
}

/// <summary>Stable reason codes. Hosts map them to plain-language copy.</summary>
public static class FirstAiConnectionReasons
{
    public const string Connected = "connected";

    // Subscription
    public const string SignedIn = "signed-in";
    public const string Paused = "paused";
    public const string SignInNeeded = "sign-in-needed";
    public const string AppFound = "app-found";
    public const string ReinstallNeeded = "reinstall-needed";
    public const string SetupNeeded = "setup-needed";
    public const string InstallAppNeeded = "install-app-needed";
    public const string UnsupportedPlatform = "unsupported-platform";

    // Local model
    public const string Ready = "ready";
    public const string Download = "download";
    public const string RuntimeDownload = "runtime-download";
    public const string SlowHardware = "slow";
    public const string HardwareUnknown = "hardware-unknown";
    public const string NotEnoughMemory = "not-enough-memory";
    public const string NoModelFits = "no-model-fits";
    public const string CatalogUnavailable = "catalog-unavailable";
    public const string ManagedElsewhere = "managed-elsewhere";

    // API key
    public const string Advanced = "advanced";
}

/// <summary>
/// Orders the first-AI setup paths for a non-technical user. Deterministic
/// application code: it never consults a model, so it works before any AI
/// exists. The order is the easiest eligible path first:
/// <list type="number">
/// <item>the ChatGPT or Claude subscription the user already has;</item>
/// <item>a free model on this computer, only when the hardware runs a tested
/// model acceptably;</item>
/// <item>an API key, as the advanced option;</item>
/// <item>a local model that would run slowly here.</item>
/// </list>
/// Paths that are already connected come first, and nothing is recommended
/// once one is, so setup never pushes a second connection. A path that cannot
/// work here is listed last so the host can explain why.
/// </summary>
public static class FirstAiConnectionPlanner
{
    /// <summary>Below this, a local model is not offered at all.</summary>
    public const double MinimumLocalRamGib = 7.5;

    /// <summary>At or above this, a CPU-only computer runs a fitting model acceptably.</summary>
    public const double ComfortableLocalRamGib = 15.5;

    /// <summary>
    /// The subscriptions setup leads with. Gemini CLI stays available in the
    /// full sign-in dialog, but its provider pack does not allow automatic
    /// routing, so it cannot answer a first question on its own.
    /// </summary>
    public static IReadOnlyList<string> LeadSubscriptionProviders { get; } =
    [
        SubscriptionCliProviders.CodexCli,
        SubscriptionCliProviders.ClaudeCode,
    ];

    public static FirstAiConnectionPlan Plan(FirstAiConnectionFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var subscription = PlanSubscription(facts);
        var local = PlanLocal(facts);
        var apiKey = facts.ConnectedPaths.Contains(FirstAiConnectionPath.ApiKey)
            ? new FirstAiConnectionOption(
                FirstAiConnectionPath.ApiKey,
                FirstAiPathAvailability.Connected,
                FirstAiConnectionReasons.Connected)
            : new FirstAiConnectionOption(
                FirstAiConnectionPath.ApiKey,
                FirstAiPathAvailability.Available,
                FirstAiConnectionReasons.Advanced);

        var ordered = new[] { subscription, local, apiKey }
            .OrderBy(Rank)
            .ToList();

        if (!ordered.Any(option => option.Availability == FirstAiPathAvailability.Connected))
        {
            var lead = ordered.FindIndex(option => option.Availability == FirstAiPathAvailability.Available);
            if (lead >= 0)
            {
                ordered[lead] = ordered[lead] with { Availability = FirstAiPathAvailability.Recommended };
            }
        }

        return new FirstAiConnectionPlan(ordered);
    }

    private static int Rank(FirstAiConnectionOption option) => option switch
    {
        { Availability: FirstAiPathAvailability.Connected } => (int)option.Path,
        { Availability: FirstAiPathAvailability.Unavailable } => 20 + (int)option.Path,
        { Path: FirstAiConnectionPath.Subscription } => 10,
        { Path: FirstAiConnectionPath.LocalModel, Availability: FirstAiPathAvailability.Slow } => 13,
        { Path: FirstAiConnectionPath.LocalModel } => 11,
        _ => 12,
    };

    private static FirstAiConnectionOption PlanSubscription(FirstAiConnectionFacts facts)
    {
        var lead = facts.Subscriptions
            .Where(fact => LeadSubscriptionProviders.Contains(fact.ProviderName, StringComparer.OrdinalIgnoreCase))
            .Select((fact, index) => (Fact: fact, Index: index))
            .ToList();

        if (facts.ConnectedPaths.Contains(FirstAiConnectionPath.Subscription))
        {
            // Name a provider only when a probe proved it signed in; the
            // connection itself may be one this setup does not lead with.
            var connected = lead
                .Where(item => item.Fact is { Stage: SubscriptionCliStage.Ready, PausedByUser: false })
                .Select(item => item.Fact.ProviderName)
                .FirstOrDefault();
            return new FirstAiConnectionOption(
                FirstAiConnectionPath.Subscription,
                FirstAiPathAvailability.Connected,
                FirstAiConnectionReasons.Connected,
                connected);
        }

        if (lead.Count == 0)
        {
            return new FirstAiConnectionOption(
                FirstAiConnectionPath.Subscription,
                FirstAiPathAvailability.Unavailable,
                FirstAiConnectionReasons.UnsupportedPlatform);
        }

        var closest = lead
            .OrderBy(item => ClosenessToReady(item.Fact))
            .ThenBy(item => item.Index)
            .First()
            .Fact;

        return new FirstAiConnectionOption(
            FirstAiConnectionPath.Subscription,
            FirstAiPathAvailability.Available,
            SubscriptionReason(closest),
            closest.ProviderName);
    }

    /// <summary>Lower is closer to answering a first question.</summary>
    private static int ClosenessToReady(FirstAiSubscriptionFact fact) => fact switch
    {
        { Stage: SubscriptionCliStage.Ready, PausedByUser: false } => 0,
        { Stage: SubscriptionCliStage.NotAuthenticated, PausedByUser: false } => 1,
        { Stage: SubscriptionCliStage.DesktopAppDetected, PausedByUser: false } => 2,
        { Stage: SubscriptionCliStage.WrongArchitecture, PausedByUser: false } => 3,
        { PausedByUser: true } => 6,
        { CanInstall: true } => 4,
        _ => 5,
    };

    private static string SubscriptionReason(FirstAiSubscriptionFact fact) => fact switch
    {
        { PausedByUser: true } => FirstAiConnectionReasons.Paused,
        { Stage: SubscriptionCliStage.Ready } => FirstAiConnectionReasons.SignedIn,
        { Stage: SubscriptionCliStage.NotAuthenticated } => FirstAiConnectionReasons.SignInNeeded,
        { Stage: SubscriptionCliStage.DesktopAppDetected } => FirstAiConnectionReasons.AppFound,
        { Stage: SubscriptionCliStage.WrongArchitecture } => FirstAiConnectionReasons.ReinstallNeeded,
        { CanInstall: true } => FirstAiConnectionReasons.SetupNeeded,
        _ => FirstAiConnectionReasons.InstallAppNeeded,
    };

    private static FirstAiConnectionOption PlanLocal(FirstAiConnectionFacts facts)
    {
        var local = facts.Local;
        var model = local.ModelLookup == FirstAiLocalModelLookup.Found ? local.Model : null;

        if (facts.ConnectedPaths.Contains(FirstAiConnectionPath.LocalModel))
        {
            return Local(FirstAiPathAvailability.Connected, FirstAiConnectionReasons.Connected, model);
        }

        if (local.ManagedElsewhere)
        {
            return Local(FirstAiPathAvailability.Unavailable, FirstAiConnectionReasons.ManagedElsewhere);
        }

        if (local.Hardware is { TotalRamGib: < MinimumLocalRamGib })
        {
            return Local(FirstAiPathAvailability.Unavailable, FirstAiConnectionReasons.NotEnoughMemory);
        }

        if (local.ModelLookup == FirstAiLocalModelLookup.CatalogUnavailable)
        {
            return Local(FirstAiPathAvailability.Unavailable, FirstAiConnectionReasons.CatalogUnavailable);
        }

        // Treat the lookup as an untrusted seam: a candidate that does not fit
        // the detected budget is not offered, whatever produced it.
        if (model is null
            || model.RequiredMemoryGb <= 0
            || local.Hardware is { } budget && model.RequiredMemoryGb > budget.EffectiveModelMemoryGb)
        {
            return Local(FirstAiPathAvailability.Unavailable, FirstAiConnectionReasons.NoModelFits);
        }

        if (local.Hardware is not { } hardware)
        {
            return Local(FirstAiPathAvailability.Slow, FirstAiConnectionReasons.HardwareUnknown, model);
        }

        if (!RunsAcceptably(hardware, model))
        {
            return Local(FirstAiPathAvailability.Slow, FirstAiConnectionReasons.SlowHardware, model);
        }

        if (model.Installed && local.Runtime is FirstAiLocalRuntimeState.Running
                or FirstAiLocalRuntimeState.InstalledNotRunning)
        {
            return Local(FirstAiPathAvailability.Available, FirstAiConnectionReasons.Ready, model);
        }

        if (local.Runtime == FirstAiLocalRuntimeState.NotInstalled && !local.CanInstallRuntimeAutomatically)
        {
            return Local(FirstAiPathAvailability.Available, FirstAiConnectionReasons.RuntimeDownload, model);
        }

        return Local(FirstAiPathAvailability.Available, FirstAiConnectionReasons.Download, model);
    }

    /// <summary>
    /// Acceptable speed for a first answer: Apple Silicon's unified memory, an
    /// NVIDIA GPU that holds the whole model, or enough system memory for a
    /// CPU-only run. The thresholds match the app's existing "Good" local-AI
    /// classification (16 GB, or a CUDA/Metal GPU).
    /// </summary>
    public static bool RunsAcceptably(FirstAiLocalHardware hardware, FirstAiLocalModelCandidate model)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        ArgumentNullException.ThrowIfNull(model);

        return hardware.Accelerator == FirstAiLocalAccelerator.AppleSilicon
            || hardware.Accelerator == FirstAiLocalAccelerator.NvidiaCuda
                && hardware.GpuMemoryGb >= model.RequiredMemoryGb
            || hardware.TotalRamGib >= ComfortableLocalRamGib;
    }

    private static FirstAiConnectionOption Local(
        FirstAiPathAvailability availability,
        string reason,
        FirstAiLocalModelCandidate? model = null)
        => new(FirstAiConnectionPath.LocalModel, availability, reason, LocalModel: model);
}
