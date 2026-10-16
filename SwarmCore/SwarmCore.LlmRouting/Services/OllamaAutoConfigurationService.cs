using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.Configuration;
using SwarmCore.LlmRouting.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SwarmCore.LlmRouting.Services;

/// <summary>
/// Background service that auto-configures Ollama on startup if available.
/// Checks for Ollama availability, available models, and embedding models.
/// Automatically pulls missing embedding models for a seamless first-time experience.
/// </summary>
public class OllamaAutoConfigurationService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<OllamaAutoConfigurationService> _logger;
    private readonly OllamaProviderOptions _options;

    /// <summary>
    /// Event raised when Ollama configuration status changes
    /// </summary>
    public static event EventHandler<OllamaConfigurationStatus>? ConfigurationChanged;

    /// <summary>
    /// Current configuration status
    /// </summary>
    public static OllamaConfigurationStatus CurrentStatus { get; private set; } = new();

    /// <summary>
    /// Update the configuration status from external code (e.g., MAUI apps that can't use IHostedService)
    /// </summary>
    public static void UpdateStatusStatic(OllamaConfigurationStatus status)
    {
        CurrentStatus = status;
        ConfigurationChanged?.Invoke(null, status);
    }

    public OllamaAutoConfigurationService(
        IServiceProvider serviceProvider,
        ILogger<OllamaAutoConfigurationService> logger,
        IOptions<OllamaProviderOptions> options)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _options = options.Value;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run entirely on a background thread to avoid blocking MAUI startup
        // This is necessary because MAUI's SynchronizationContext can cause deadlocks
        _ = Task.Run(async () =>
        {
            try
            {
                // Wait briefly for app to initialize
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken).ConfigureAwait(false);

                _logger.LogInformation("Checking Ollama availability...");

                try
                {
                    await ConfigureOllamaAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error during Ollama auto-configuration");
                }

                // Continue monitoring periodically (every 5 minutes) in case Ollama starts later
                while (!stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken).ConfigureAwait(false);

                    if (!CurrentStatus.IsAvailable)
                    {
                        try
                        {
                            await ConfigureOllamaAsync(stoppingToken).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Ollama still not available");
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Expected on shutdown
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in Ollama auto-configuration service");
            }
        }, stoppingToken);

        // Return immediately to not block startup
        return Task.CompletedTask;
    }

    private async Task ConfigureOllamaAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();

        // Get the Ollama provider
        var providers = scope.ServiceProvider.GetServices<ILlmProvider>();
        var ollamaProvider = providers.OfType<OllamaLlmProvider>().FirstOrDefault();
        var providerDiscovery = scope.ServiceProvider.GetRequiredService<LlmProviderDiscoveryCoordinator>();

        if (ollamaProvider == null)
        {
            _logger.LogDebug("OllamaLlmProvider not registered");
            UpdateStatus(new OllamaConfigurationStatus
            {
                IsAvailable = false,
                Message = "Ollama provider not registered"
            });
            return;
        }

        // Test connectivity
        var connectivity = await providerDiscovery.ProbeConnectivityAsync(
            ollamaProvider,
            TimeSpan.FromSeconds(5),
            ct,
            highPriority: true);
        var isHealthy = connectivity.Completed && connectivity.IsHealthy;
        var message = connectivity.Message;

        if (!isHealthy)
        {
            _logger.LogInformation("Ollama not available: {Message}", message);
            _logger.LogInformation("To use local AI, install Ollama from https://ollama.ai");

            UpdateStatus(new OllamaConfigurationStatus
            {
                IsAvailable = false,
                Message = message,
                SetupInstructions = "Install Ollama from https://ollama.ai and run 'ollama serve'"
            });
            return;
        }

        _logger.LogInformation("Ollama is available: {Message}", message);

        // Get available models
        var models = await providerDiscovery.GetAvailableModelsAsync(
            ollamaProvider,
            TimeSpan.FromSeconds(5),
            ct,
            highPriority: true);

        // Check for embedding model
        var (hasEmbedding, embeddingModel, embeddingMessage) = await ollamaProvider.CheckEmbeddingModelAsync();

        if (!hasEmbedding)
        {
            _logger.LogWarning("No embedding model found for semantic search");

            // Auto-pull embedding model if enabled (default: true)
            if (_options.AutoPullEmbeddingModel)
            {
                var preferredModel = EmbeddingModelDefaults.Model;
                _logger.LogInformation("Auto-pulling embedding model: {Model}... (running in background)", preferredModel);

                UpdateStatus(new OllamaConfigurationStatus
                {
                    IsAvailable = true,
                    Message = $"Downloading embedding model: {preferredModel}...",
                    IsPullingModel = true,
                    PullingModelName = preferredModel,
                    AvailableModels = models.ToList()
                });

                // Fire-and-forget the model pull to avoid blocking app startup
                // Capture the provider reference for the background task
                var provider = ollamaProvider;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var pullSuccess = await provider.EnsureEmbeddingModelAsync(preferredModel);

                        if (pullSuccess)
                        {
                            _logger.LogInformation("Successfully installed embedding model: {Model}", preferredModel);

                            // Refresh model list and update status
                            var refreshedModels = await providerDiscovery.GetAvailableModelsAsync(
                                provider,
                                TimeSpan.FromSeconds(10),
                                CancellationToken.None,
                                forceRefresh: true,
                                highPriority: true);

                            UpdateStatus(new OllamaConfigurationStatus
                            {
                                IsAvailable = true,
                                IsReady = true,
                                Message = $"Ollama ready with embedding model {preferredModel}",
                                AvailableModels = refreshedModels.ToList(),
                                HasEmbeddingModel = true,
                                EmbeddingModel = preferredModel,
                                ChatModel = refreshedModels.FirstOrDefault()
                            });
                        }
                        else
                        {
                            _logger.LogError("Failed to pull embedding model: {Model}", preferredModel);
                            _logger.LogWarning("You can manually install it with: ollama pull {Model}", preferredModel);

                            UpdateStatus(new OllamaConfigurationStatus
                            {
                                IsAvailable = true,
                                IsReady = false,
                                Message = $"Failed to download {preferredModel}",
                                SetupInstructions = $"Run: ollama pull {preferredModel}"
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error pulling embedding model {Model}", preferredModel);
                    }
                });

                // Don't wait - return now with "pulling" status
                // The background task will update status when complete
                return;
            }
            else
            {
                _logger.LogWarning("To enable semantic search, run: ollama pull {Model}", EmbeddingModelDefaults.Model);
                _logger.LogInformation("Or enable auto-pull in OllamaProviderOptions.AutoPullEmbeddingModel");
            }
        }
        else
        {
            _logger.LogInformation("Embedding model available: {Model}", embeddingModel);
        }

        // Check for vision model
        // GetAvailableModelsAsync has already refreshed /api/show metadata.
        // Ask the provider so newly released multimodal families (whose names
        // do not contain "vision") are not hidden from auto-configuration.
        var hasVision = models.Any(ollamaProvider.SupportsVision);

        if (!hasVision)
        {
            _logger.LogInformation("No vision model found. For image analysis, run: ollama pull llama3.2-vision");
        }

        // Register with the provider registry
        var registry = scope.ServiceProvider.GetService<ILlmProviderRegistry>();
        if (registry != null)
        {
            // Provider should already be registered, but ensure it is
            var existingProvider = registry.GetProvider("Ollama");
            if (existingProvider == null)
            {
                registry.RegisterProvider(ollamaProvider);
                _logger.LogInformation("Registered Ollama provider with registry");
            }
        }

        // Update status
        UpdateStatus(new OllamaConfigurationStatus
        {
            IsAvailable = true,
            Message = message,
            AvailableModels = models.ToList(),
            HasEmbeddingModel = hasEmbedding,
            EmbeddingModel = embeddingModel,
            HasVisionModel = hasVision,
            ChatModel = models.FirstOrDefault(),
            IsReady = hasEmbedding, // Ready for semantic search if embedding model available
            SetupInstructions = hasEmbedding ? null : $"Run: ollama pull {EmbeddingModelDefaults.Model}"
        });

        // Log summary
        if (hasEmbedding)
        {
            _logger.LogInformation(
                "Ollama ready for AI features: {ChatModel} (chat), {EmbeddingModel} (embeddings){Vision}",
                models.FirstOrDefault() ?? "auto",
                embeddingModel,
                hasVision ? ", vision enabled" : "");
        }
        else
        {
            _logger.LogWarning(
                "Ollama available but missing embedding model. Semantic search disabled. " +
                "Run: ollama pull {Model}",
                EmbeddingModelDefaults.Model);
        }
    }

    /// <summary>
    private void UpdateStatus(OllamaConfigurationStatus status)
    {
        CurrentStatus = status;
        ConfigurationChanged?.Invoke(this, status);
    }
}

/// <summary>
/// Status of Ollama configuration
/// </summary>
public class OllamaConfigurationStatus
{
    /// <summary>
    /// Whether Ollama is running and accessible
    /// </summary>
    public bool IsAvailable { get; set; }

    /// <summary>
    /// Whether Ollama is ready for all AI features (has embedding model)
    /// </summary>
    public bool IsReady { get; set; }

    /// <summary>
    /// Status message
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Setup instructions if not ready
    /// </summary>
    public string? SetupInstructions { get; set; }

    /// <summary>
    /// List of available model names
    /// </summary>
    public List<string> AvailableModels { get; set; } = new();

    /// <summary>
    /// Whether an embedding model is available
    /// </summary>
    public bool HasEmbeddingModel { get; set; }

    /// <summary>
    /// Name of the detected embedding model
    /// </summary>
    public string? EmbeddingModel { get; set; }

    /// <summary>
    /// Whether a vision model is available
    /// </summary>
    public bool HasVisionModel { get; set; }

    /// <summary>
    /// Name of the detected chat model
    /// </summary>
    public string? ChatModel { get; set; }

    /// <summary>
    /// Whether a model is currently being pulled/downloaded
    /// </summary>
    public bool IsPullingModel { get; set; }

    /// <summary>
    /// Name of the model being pulled (if any)
    /// </summary>
    public string? PullingModelName { get; set; }

    /// <summary>
    /// When the status was last checked
    /// </summary>
    public DateTime LastChecked { get; set; } = DateTime.UtcNow;
}
