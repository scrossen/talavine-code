using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting.Services;

/// <summary>Clock configuration for <see cref="ModelScoutMaintenanceScheduler"/>.</summary>
public sealed record ModelScoutMaintenanceSchedulerOptions
{
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Reusable Model Scout maintenance clock: after an initial delay it polls the
/// host's <see cref="IModelScoutMaintenancePass"/> on a fixed interval so Scout
/// keeps learning about newly available, faster, cheaper, or more capable
/// routes. Hosts own cycle content and durable checkpoint cadence; the
/// scheduler owns only process lifetime, pacing, and failure isolation.
/// </summary>
public sealed class ModelScoutMaintenanceScheduler(
    IServiceProvider serviceProvider,
    ILogger<ModelScoutMaintenanceScheduler> logger,
    ModelScoutMaintenanceSchedulerOptions? options = null) : BackgroundService
{
    private readonly ModelScoutMaintenanceSchedulerOptions _options =
        options ?? new ModelScoutMaintenanceSchedulerOptions();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (IsDisabled())
        {
            return;
        }

        logger.LogInformation(
            "ModelScoutMaintenanceScheduler starting; initial delay {Delay}s, poll interval {Poll}s",
            _options.InitialDelay.TotalSeconds,
            _options.PollInterval.TotalSeconds);

        try
        {
            await Task.Delay(_options.InitialDelay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            // Guards are fixed for normal launches, but rechecking at the
            // mutation boundary lets a dynamically constrained diagnostic
            // process fail closed before it changes routing data.
            if (IsDisabled())
            {
                return;
            }

            try
            {
                using var scope = serviceProvider.CreateScope();
                var pass = scope.ServiceProvider.GetRequiredService<IModelScoutMaintenancePass>();
                if (await pass.IsEnabledAsync(stoppingToken).ConfigureAwait(false))
                {
                    await pass.RunCycleAsync(stoppingToken).ConfigureAwait(false);
                }
                else
                {
                    logger.LogInformation(
                        "Model Scout automatic management is off; skipping the maintenance cycle.");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Model Scout maintenance cycle failed");
            }

            try
            {
                await Task.Delay(_options.PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private bool IsDisabled()
    {
        try
        {
            using var scope = serviceProvider.CreateScope();
            return scope.ServiceProvider
                .GetRequiredService<IModelScoutMaintenancePass>()
                .IsDisabledForProcess();
        }
        catch (Exception ex)
        {
            // A pass that cannot even answer the guard question must not mutate
            // routing data on a timer.
            logger.LogWarning(ex, "Model Scout maintenance guard check failed; scheduler is stopping");
            return true;
        }
    }
}
