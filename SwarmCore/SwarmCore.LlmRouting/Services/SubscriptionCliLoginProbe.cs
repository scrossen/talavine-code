using System.Text.Json;
using SwarmCore.LlmRouting.Configuration;
using SwarmCore.LlmRouting.ProviderPacks;
using SwarmCore.LlmRouting.Providers;

namespace SwarmCore.LlmRouting.Services;

/// <summary>
/// Typed outcome of one subscription-CLI login-status probe. The four states
/// deliberately separate "the probe answered" from "the probe never ran":
/// only a COMPLETED probe is evidence about the login session, while an
/// infrastructure block (co-run guard decline, preemption, killed hang) says
/// nothing about it — conflating the two filed false 14-day "Sign in" human
/// tasks for signed-in users and earned healthy providers spurious cooldowns.
/// </summary>
public enum SubscriptionCliLoginProbeStatus
{
    /// <summary>The login-status command ran to completion and proved an
    /// authenticated subscription session (not merely a stored API key).</summary>
    CompletedAuthenticated,

    /// <summary>The login-status command ran to completion and did NOT prove a
    /// subscription session: signed out, non-zero exit, or API/Console auth.
    /// This is the only state allowed to produce a sign-in verdict.</summary>
    CompletedNotAuthenticated,

    /// <summary>The probe never produced a verdict: the launch was declined by
    /// the cross-process co-run guard/safe-stop sweep, the probe was preempted
    /// by user-waiting work, or it hung and was killed. NOT evidence of a
    /// signed-out session; retry on the next sweep.</summary>
    InfrastructureBlocked,

    /// <summary>The candidate executable genuinely failed to launch (deleted
    /// binary, unlaunchable app-execution alias, spawn failure). Callers skip
    /// the candidate; this is neither a login verdict nor a transient block.</summary>
    ExecutionFailed
}

/// <summary>
/// Result of <see cref="SubscriptionCliLoginProbe.ProbeAsync"/>. <see cref="Failure"/>
/// carries the launch/scheduling exception for <see cref="SubscriptionCliLoginProbeStatus.InfrastructureBlocked"/>
/// and <see cref="SubscriptionCliLoginProbeStatus.ExecutionFailed"/> so callers
/// can keep their exact per-cause logging without re-catching anything.
/// </summary>
public sealed record SubscriptionCliLoginProbeResult(
    SubscriptionCliLoginProbeStatus Status,
    Exception? Failure = null,
    bool StatusOutputRecognized = true);

/// <summary>
/// THE single implementation of subscription-CLI login-status probing, shared
/// by the runtime provider stack (<see cref="SubscriptionCliLlmProviderBase"/>)
/// and the setup/detection stack (<see cref="SubscriptionCliSetupService"/>).
/// It owns the per-provider login-status argument table, the authenticated-
/// session output parser, and the probe-outcome classification; neither stack
/// may keep a private copy (the two copies used to drift — 7 of 21 provider
/// commits also had to patch the setup twin). Follows the
/// <see cref="SubscriptionCliDiscovery"/> precedent: shared provider-identity
/// logic here, each stack keeps its own process engine (the provider's
/// injected <see cref="ISubscriptionCliProcessRunner"/> vs the setup service's
/// install/version-capable runner), both of which already route through the
/// shared execution gate and per-provider containment keys.
/// </summary>
public static class SubscriptionCliLoginProbe
{
    /// <summary>
    /// A bounded, non-interactive command that proves a current login session
    /// for the provider. Returning null deliberately fails closed for CLIs
    /// without such a command (Gemini has no `auth status`-style subcommand;
    /// its session is proven from on-disk auth state instead — see
    /// <see cref="Providers.GeminiCliAuthProbe"/>).
    /// </summary>
    public static IReadOnlyList<string>? GetLoginStatusArguments(string providerName)
    {
        return ProviderPackCatalog.TryGet(providerName, out var providerPack)
               && providerPack!.SubscriptionCli is { LoginStatusArguments.Count: > 0 } cli
            ? cli.LoginStatusArguments
            : null;
    }

    /// <summary>
    /// Validates that a successful login-status command represents
    /// subscription auth, not merely a stored API key. Callers must not log
    /// raw output: it may contain an account email, organization, or
    /// credential metadata.
    /// </summary>
    public static bool IsAuthenticatedSubscriptionSession(
        string providerName,
        string? standardOutput,
        string? standardError)
    {
        if (providerName.Equals(SubscriptionCliProviders.ClaudeCode, StringComparison.OrdinalIgnoreCase))
        {
            if (!LooksLikeJsonObject(standardOutput))
            {
                return false;
            }

            try
            {
                using var json = JsonDocument.Parse(standardOutput ?? string.Empty);
                if (json.RootElement.ValueKind != JsonValueKind.Object
                    || !json.RootElement.TryGetProperty("loggedIn", out var loggedIn)
                    || loggedIn.ValueKind != JsonValueKind.True
                    || !json.RootElement.TryGetProperty("subscriptionType", out var subscriptionType)
                    || subscriptionType.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                var subscription = subscriptionType.GetString()?.Trim();
                return !string.IsNullOrWhiteSpace(subscription)
                       && !subscription.Equals("none", StringComparison.OrdinalIgnoreCase)
                       && !subscription.Equals("api", StringComparison.OrdinalIgnoreCase)
                       && !subscription.Equals("console", StringComparison.OrdinalIgnoreCase);
            }
            catch (JsonException)
            {
                // Current Claude Code emits JSON for `auth status`. Unknown
                // output is not strong enough to authorize an automatic route.
                return false;
            }
        }

        if (providerName.Equals(SubscriptionCliProviders.CodexCli, StringComparison.OrdinalIgnoreCase))
        {
            // `codex login status` can also succeed for a stored API key. The
            // subscription providers are explicitly subscription-backed, so
            // accept only ChatGPT login and leave API-key execution to the
            // metered OpenAI provider.
            var output = $"{standardOutput}\n{standardError}";
            return ProviderPackCatalog.GetRequired(providerName).SubscriptionCli!
                .AuthenticatedStatusMarkers.Any(marker =>
                    output.Contains(marker, StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }

    /// <summary>
    /// Distinguishes an explicit signed-out/API verdict from output produced
    /// because an installed CLI no longer recognizes its historical status
    /// command. Callers may use a provider-specific local OAuth probe only for
    /// the latter case; recognized signed-out output must continue to fail.
    /// </summary>
    public static bool IsRecognizedStatusOutput(
        string providerName,
        string? standardOutput,
        string? standardError)
    {
        if (providerName.Equals(SubscriptionCliProviders.ClaudeCode, StringComparison.OrdinalIgnoreCase))
        {
            if (!LooksLikeJsonObject(standardOutput))
            {
                var output = $"{standardOutput}\n{standardError}";
                return ProviderPackCatalog.GetRequired(providerName).SubscriptionCli!
                    .RecognizedStatusMarkers.Any(marker =>
                        output.Contains(marker, StringComparison.OrdinalIgnoreCase));
            }

            try
            {
                using var json = JsonDocument.Parse(standardOutput ?? string.Empty);
                return json.RootElement.ValueKind == JsonValueKind.Object
                       && json.RootElement.TryGetProperty("loggedIn", out var loggedIn)
                       && loggedIn.ValueKind is JsonValueKind.True or JsonValueKind.False;
            }
            catch (JsonException)
            {
                var output = $"{standardOutput}\n{standardError}";
                return ProviderPackCatalog.GetRequired(providerName).SubscriptionCli!
                    .RecognizedStatusMarkers.Any(marker =>
                        output.Contains(marker, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (providerName.Equals(SubscriptionCliProviders.CodexCli, StringComparison.OrdinalIgnoreCase))
        {
            var output = $"{standardOutput}\n{standardError}";
            return ProviderPackCatalog.GetRequired(providerName).SubscriptionCli!
                .RecognizedStatusMarkers.Any(marker =>
                    output.Contains(marker, StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }

    private static bool LooksLikeJsonObject(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return value.AsSpan().TrimStart().StartsWith("{".AsSpan(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Classify one probe run's evidence into the typed outcome. Used directly
    /// by hosts whose process engine reports infrastructure blocks as a flag
    /// (the setup service's <c>LaunchInfrastructureBlocked</c>) rather than as
    /// typed exceptions; <see cref="ProbeAsync"/> funnels through the same
    /// completed-probe rule so the two stacks cannot diverge.
    /// </summary>
    public static SubscriptionCliLoginProbeStatus Classify(
        string providerName,
        int exitCode,
        string? standardOutput,
        string? standardError,
        bool launchInfrastructureBlocked = false)
    {
        if (launchInfrastructureBlocked)
        {
            return SubscriptionCliLoginProbeStatus.InfrastructureBlocked;
        }

        return exitCode == 0
               && IsAuthenticatedSubscriptionSession(providerName, standardOutput, standardError)
            ? SubscriptionCliLoginProbeStatus.CompletedAuthenticated
            : SubscriptionCliLoginProbeStatus.CompletedNotAuthenticated;
    }

    /// <summary>
    /// Run the provider's login-status command for one executable candidate
    /// through the shared runner/gate machinery and classify the outcome.
    /// Passing <paramref name="providerName"/> on the request is what scopes
    /// the runner's per-provider containment key, so a live child of one CLI
    /// never declines another CLI's probe. Caller cancellation propagates as
    /// <see cref="OperationCanceledException"/>; every other failure is folded
    /// into the typed result.
    /// </summary>
    public static async Task<SubscriptionCliLoginProbeResult> ProbeAsync(
        ISubscriptionCliProcessRunner runner,
        string providerName,
        string executable,
        IReadOnlyList<string> loginStatusArguments,
        string workingDirectory,
        TimeSpan timeout,
        SubscriptionCliProcessPriority priority,
        CancellationToken ct)
    {
        try
        {
            var result = await runner.RunAsync(new SubscriptionCliProcessRequest(
                    executable,
                    loginStatusArguments,
                    StandardInput: null,
                    WorkingDirectory: workingDirectory,
                    Timeout: timeout,
                    ProviderName: providerName,
                    Operation: SubscriptionCliProcessOperation.LoginStatus,
                    Priority: priority),
                ct).ConfigureAwait(false);

            if (result.OutputWasTruncated)
            {
                // Authentication requires a complete status document. A prefix
                // that happens to contain a success marker is not proof, and raw
                // status output may contain account details, so expose only a
                // typed infrastructure failure with a safe fixed message.
                return new SubscriptionCliLoginProbeResult(
                    SubscriptionCliLoginProbeStatus.InfrastructureBlocked,
                    new InvalidDataException(
                        "Subscription CLI login-status output exceeded the safe capture limit."),
                    StatusOutputRecognized: false);
            }

            return new SubscriptionCliLoginProbeResult(
                Classify(providerName, result.ExitCode, result.StandardOutput, result.StandardError),
                StatusOutputRecognized: IsRecognizedStatusOutput(
                    providerName,
                    result.StandardOutput,
                    result.StandardError));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (SubscriptionCliSafeStopException ex)
        {
            // The pre-launch orphan sweep was blocked or another process's live
            // child holds this provider's slot.
            return new SubscriptionCliLoginProbeResult(
                SubscriptionCliLoginProbeStatus.InfrastructureBlocked, ex);
        }
        catch (SubscriptionCliPreemptedException ex)
        {
            // The probe was stopped so user-waiting work could take the
            // execution slot — a scheduling outcome, not a login verdict.
            return new SubscriptionCliLoginProbeResult(
                SubscriptionCliLoginProbeStatus.InfrastructureBlocked, ex);
        }
        catch (TimeoutException ex)
        {
            // The probe process launched but hung and was killed before it
            // could answer; that is not evidence of a signed-out session.
            return new SubscriptionCliLoginProbeResult(
                SubscriptionCliLoginProbeStatus.InfrastructureBlocked, ex);
        }
        catch (Exception ex)
        {
            // A discovered path can be an unlaunchable app-execution alias.
            // Callers continue to later candidates and never include status
            // output in logs or user-visible results.
            return new SubscriptionCliLoginProbeResult(
                SubscriptionCliLoginProbeStatus.ExecutionFailed, ex);
        }
    }
}
