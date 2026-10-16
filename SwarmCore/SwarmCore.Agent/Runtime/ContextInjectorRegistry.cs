using System.Text;
using Microsoft.Extensions.Logging;

namespace SwarmCore.Agent.Runtime;

/// <summary>
/// Default registry that looks injectors up by name (case-insensitive) and
/// concatenates their contributions with a blank line between each.
/// </summary>
public sealed class ContextInjectorRegistry : IContextInjectorRegistry
{
    private static readonly TimeSpan DefaultInjectorTimeout = TimeSpan.FromSeconds(35);

    private readonly Dictionary<string, IContextInjector> _byName;
    private readonly ILogger<ContextInjectorRegistry> _logger;
    private readonly TimeSpan _injectorTimeout;

    public ContextInjectorRegistry(
        IEnumerable<IContextInjector> injectors,
        ILogger<ContextInjectorRegistry> logger,
        TimeSpan? injectorTimeout = null)
    {
        _byName = injectors.ToDictionary(i => i.Name, StringComparer.OrdinalIgnoreCase);
        _logger = logger;
        _injectorTimeout = injectorTimeout ?? DefaultInjectorTimeout;
    }

    public async Task<string?> BuildCombinedAsync(
        IReadOnlyList<string> injectorNames,
        ContextInjectionRequest request,
        CancellationToken ct = default)
    {
        if (injectorNames.Count == 0) return null;

        var sb = new StringBuilder();
        foreach (var name in injectorNames)
        {
            if (!_byName.TryGetValue(name, out var injector))
            {
                _logger.LogWarning("Profile references unknown context injector '{Name}' — skipping.", name);
                continue;
            }
            try
            {
                var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var buildTask = Task.Run(
                    () => injector.BuildAsync(request, timeoutCts.Token),
                    timeoutCts.Token);

                string? block;
                try
                {
                    block = await buildTask.WaitAsync(_injectorTimeout, ct);
                    timeoutCts.Dispose();
                }
                catch (TimeoutException)
                {
                    timeoutCts.Cancel();
                    _logger.LogWarning(
                        "Context injector '{Name}' exceeded {TimeoutSeconds}s; continuing without its contribution.",
                        name,
                        _injectorTimeout.TotalSeconds);
                    _ = ObserveTimedOutInjectorAsync(name, buildTask, timeoutCts);
                    continue;
                }
                catch
                {
                    timeoutCts.Dispose();
                    throw;
                }

                if (!string.IsNullOrWhiteSpace(block))
                {
                    if (sb.Length > 0) sb.Append("\n\n");
                    sb.Append(block);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(
                    "Context injector '{Name}' was canceled; continuing without its contribution.",
                    name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Context injector '{Name}' threw; continuing without its contribution.", name);
            }
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    private async Task ObserveTimedOutInjectorAsync(string name, Task<string?> buildTask, CancellationTokenSource timeoutCts)
    {
        try
        {
            await buildTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected after the foreground timeout requested cancellation.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Timed-out context injector '{Name}' eventually failed.", name);
        }
        finally
        {
            timeoutCts.Dispose();
        }
    }
}
