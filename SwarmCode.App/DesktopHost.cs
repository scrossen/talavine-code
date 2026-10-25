using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using SwarmCode.App.Services;
using SwarmCode.Hosting;
using SwarmCore.Secrets;

namespace SwarmCode.App;

public static class DesktopHost
{
    /// <summary>
    /// Persisted Keychain service name of SwarmCode's vault key-encryption key.
    /// Renaming it orphans every secret written so far.
    /// </summary>
    internal const string MacKeychainServiceName = "com.swarmfamily.swarmcode.secret-protector";

    private static IServiceProvider? _services;
    private static string? _logPath;
    private static readonly ConcurrentQueue<string> _pendingLog = new();

    public static string? LogPath => _logPath;

    public static IServiceProvider? GetServiceProvider() => _services;

#if MACOS
    public static ServiceProvider CreateNativeServices()
    {
        var builder = new NativeServicesBuilder();
#else
    public static MauiApp CreateMauiApp()
    {
#if WINDOWS
        // Standalone installers can carry Microsoft's fixed WebView2 runtime.
        // Select it for this process only; explicit operator overrides win.
        var bundledBrowser = Path.Combine(AppContext.BaseDirectory, "WebView2Runtime");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER"))
            && File.Exists(Path.Combine(bundledBrowser, "msedgewebview2.exe")))
            Environment.SetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER", bundledBrowser);
#endif
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
#endif

        // Shared data directory with the console (SWARMCODE_HOME overrides it).
        var paths = new SwarmCodeDataPaths();
        _logPath = Path.Combine(paths.LogsDirectory, "swarmcode-app.log");

        // Diagnostics: SWARMCODE_CDP_PORT opens WebView2's remote-debugging port. The runtime reads this environment
        // variable when it creates the browser process; the per-view EnvironmentOptions hook alone did not apply (verified live).
        if (Environment.GetEnvironmentVariable("SWARMCODE_CDP_PORT") is { Length: > 0 } cdpPort && int.TryParse(cdpPort, out var port) && port > 0)
        {
            var existing = Environment.GetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS");
            if (string.IsNullOrWhiteSpace(existing) || !existing.Contains("--remote-debugging-port", StringComparison.OrdinalIgnoreCase))
                Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",
                    (string.IsNullOrWhiteSpace(existing) ? "" : existing + " ") + $"--remote-debugging-port={port} --remote-allow-origins=*");
        }

        builder.Logging.ClearProviders();
        builder.Logging.AddFilter("SwarmCore", LogLevel.Information);
        builder.Logging.AddFilter("SwarmCode", LogLevel.Debug);
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Logging.AddProvider(new FileLoggerProvider(_logPath));
#if DEBUG
        builder.Logging.AddDebug();
#endif

#if MACOS
        builder.Services.AddBlazorWebView();
#else
        builder.Services.AddMauiBlazorWebView();
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif

        builder.Services.AddSingleton<App>();
#endif
        // Talk instead of type: the composer's dictate button starts the OS dictation (Win+H, macOS Dictation).
        builder.Services.AddSingleton<SwarmCore.Chat.IDictationLauncher, SwarmCore.Chat.SystemDictationLauncher>();
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton<AppPreferencesStore>();
        builder.Services.AddSingleton<StarInvitationStore>();
        builder.Services.AddSingleton<WorkspaceSessionCatalog>();
        builder.Services.AddSingleton<UserSettingsFile>();
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<SwarmCore.LlmRouting.LocalRuntime.OllamaSetupClient>();
        builder.Services.AddSingleton<SwarmCore.LlmRouting.LocalRuntime.OpenModelDiscovery>();
        builder.Services.AddSingleton<AppState>();
        builder.Services.AddSingleton<SwarmCodeUpdates>();
        builder.Services.AddSingleton<UserBugIntake>(_ => new(
            new SwarmCore.Coding.SelfImprovement.JsonFileSelfImprovementBacklog(paths.SelfImprovementDirectory),
            () => SwarmCode.Hosting.Improvement.SwarmCodeSelfImprovement.BindOptions(SwarmCodeHostComposer.BuildConfiguration(paths))));
        builder.Services.AddSingleton<RepositoryOpener>();
        builder.Services.AddSingleton<SwarmCode.Hosting.Projects.CodingAppLibraryStore>();
        builder.Services.AddSingleton<SwarmCode.Hosting.Projects.FileAppProcedureStore>();
        builder.Services.AddSingleton(sp =>
        {
            var store = sp.GetRequiredService<SwarmCode.Hosting.Projects.FileAppProcedureStore>();
            return new SwarmCore.Coding.Projects.CodingAppRuntime(store, paths.RootDirectory, store.AcquireRunLease);
        });
        builder.Services.AddSingleton<MyAppsService>();
        // Editor integration: "Open in VS Code" and the loopback endpoint the SwarmCode VS Code extension posts to.
        builder.Services.AddSingleton<IdeIntegration>();

        // OS keystore that wraps the vault's data keys: DPAPI on Windows (same scope as the
        // console, so both read one secrets.db), Keychain on macOS.
        // Mac vault storage requires encryption and fails closed without usable Keychain access.
#if MACOS
        builder.Services.AddSingleton<IOsKeyStore>(sp => new SwarmCore.Secrets.Apple.MacKeychainKeyStore(
            sp.GetRequiredService<ILogger<SwarmCore.Secrets.Apple.MacKeychainKeyStore>>(), MacKeychainServiceName));
        builder.Services.AddSingleton<SwarmCore.Coding.Computer.IMacDesktopDriver, Platforms.macOS.Services.MacDesktopDriver>();
#else
        builder.Services.AddSingleton<IOsKeyStore>(sp => KeyStoreFactory.Create(sp.GetRequiredService<ILoggerFactory>(), SwarmCodeHostComposer.KeyStoreApplicationScope));
#endif

#if WINDOWS
        builder.Services.AddSingleton<IFolderPickerService, Platforms.Windows.Services.WindowsFolderPickerService>();
#elif MACOS
        builder.Services.AddSingleton<IFolderPickerService, Platforms.macOS.Services.MacFolderPickerService>();
#else
        builder.Services.AddSingleton<IFolderPickerService, NoOpFolderPickerService>();
#endif

#if MACOS
        var services = builder.Services.BuildServiceProvider();
#else
        var app = builder.Build();
        var services = app.Services;
#endif
        _services = services;
        FlushPendingLog();
        var ide = services.GetRequiredService<IdeIntegration>(); // AppState is resolved lazily, not here
        _ = Task.Run(ide.Start);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { ide.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); } catch { } };
        // Exceptions nothing caught are SwarmCode's own defects: they go to the self-improvement backlog (never constructing
        // AppState here; a crash during startup has nothing to report into).
        TaskScheduler.UnobservedTaskException += (_, e) => ReportUnhandled(e.Exception, "An unobserved task failed.");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportUnhandled(e.ExceptionObject as Exception, "An unhandled exception reached the app domain.");
#if MACOS
        return services;
#else
        return app;
#endif
    }

#if MACOS
    private sealed class NativeServicesBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();
        public ILoggingBuilder Logging { get; }
        public NativeServicesBuilder()
        {
            ILoggingBuilder? logging = null;
            Services.AddLogging(builder => logging = builder);
            Logging = logging!;
        }
    }
#endif

    private static void ReportUnhandled(Exception? exception, string context)
    {
        try
        {
            if (exception is not null) LogToFile($"{context} {exception}");
            SwarmCode.Hosting.Improvement.SelfImprovementCapture.Unhandled(AppState.Instance?.SelfImprovementReporter, exception, context);
        }
        catch { /* never throw from a crash handler */ }
    }

    /// <summary>Append a line to the app debug log. Safe to call before the host is built.</summary>
    public static void LogToFile(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        if (_logPath is null) { _pendingLog.Enqueue(line); return; }
        TryAppend(line);
    }

    private static void FlushPendingLog()
    {
        while (_pendingLog.TryDequeue(out var line)) TryAppend(line);
    }

    private static void TryAppend(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath!)!);
            File.AppendAllText(_logPath!, line + Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
