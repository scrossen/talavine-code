using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SwarmCode.Hosting;
using SwarmCore.Coding.Ide;

namespace SwarmCode.App.Services;

/// <summary>
/// The app's editor integration. Outbound: "Open in VS Code" for tool cards, the Changes panel, and file
/// references in the transcript (<see cref="EditorLauncher"/>, fenced to the session's repository). Inbound: the
/// <see cref="IdeBridgeServer"/> loopback endpoint the SwarmCode VS Code extension posts selections to; each one is
/// routed to a session (<see cref="IdeSessionRouter"/>) and parked here until that session's composer takes it.
/// Nothing is ever sent automatically. All file and process work runs off the UI thread.
/// </summary>
public sealed class IdeIntegration : IAsyncDisposable
{
    private readonly IServiceProvider _services;
    private readonly ILogger _logger;
    private readonly EditorLauncher _launcher;
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _pending = new(StringComparer.Ordinal);
    private IdeBridgeServer? _bridge;

    // Resolved on first use so starting the bridge at launch does not construct the app state early.
    private AppState State => _services.GetRequiredService<AppState>();

    public IdeIntegration(IServiceProvider services, ILoggerFactory loggers)
    {
        _services = services;
        _logger = loggers.CreateLogger<IdeIntegration>();
        _launcher = new EditorLauncher(openUri: OpenUriAsync, openDefault: OpenDefaultAsync);
    }

    /// <summary>The session on screen; set by <c>IdeBridgeHost</c> as the person navigates.</summary>
    public string? ActiveSessionId { get; set; }

    /// <summary>A selection was parked for this session: show it and insert the block into its composer.</summary>
    public event Action<string>? SelectionRouted;

    /// <summary>A short message for the person (an editor that could not be opened, a selection with no session).</summary>
    public event Action<string, bool>? Notice;

    /// <summary>"Open in VS Code", or "Open in editor" when the person configured another editor command.</summary>
    public string OpenLabel => string.IsNullOrWhiteSpace(State.Preferences.Current.EditorCommand) ? "Open in VS Code" : "Open in editor";

    /// <summary>Starts the selection endpoint and its discovery record. Safe to call from any thread.</summary>
    public void Start()
    {
        if (_bridge is not null) return;
        try
        {
            var version = typeof(IdeIntegration).Assembly.GetName().Version?.ToString();
            _bridge = new IdeBridgeServer("swarmcode", "Talavine Code", version, DeliverAsync, _logger);
            _bridge.Start();
            _logger.LogInformation("Editor bridge listening; discovery record {Path}", _bridge.DiscoveryPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Editor bridge did not start");
            DesktopHost.LogToFile($"Editor bridge did not start: {ex.Message}");
        }
    }

    /// <summary>Opens a file location; reports failures through <see cref="Notice"/>.</summary>
    public async Task<bool> OpenAsync(EditorTarget target, string? repositoryRoot)
    {
        EditorOpenResult result;
        try { result = await _launcher.OpenAsync(target, repositoryRoot, State.Preferences.Current.EditorCommand).ConfigureAwait(false); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Open in editor failed");
            result = new EditorOpenResult(false, "Could not open the file in your editor.");
        }
        if (!result.Opened) Notice?.Invoke(result.Message, false);
        return result.Opened;
    }

    /// <summary>The reference blocks waiting for this session's composer, joined; null when there are none.</summary>
    public string? TakePending(string sessionId)
    {
        if (!_pending.TryRemove(sessionId, out var queue)) return null;
        var blocks = queue.ToArray();
        return blocks.Length == 0 ? null : string.Join("\n\n", blocks);
    }

    private async Task<IdeDeliveryResult> DeliverAsync(IdeSelection selection, CancellationToken ct)
    {
        var sessions = await State.Sessions.ListAsync(ct: ct).ConfigureAwait(false);
        var candidates = sessions
            .Select(s => new IdeSessionCandidate(s.Id, s.RepositoryRoot, s.LastActivityUtc, s.ParentSessionId is null))
            .ToList();
        var chosen = IdeSessionRouter.Choose(candidates, selection.File, ActiveSessionId);
        if (chosen is null)
        {
            const string message = "Open a session for this repository in Talavine Code, then send the selection again.";
            Notice?.Invoke(message, false);
            return new IdeDeliveryResult(false, message);
        }
        var block = IdeReferenceFormatter.Format(selection, chosen.RepositoryRoot);
        _pending.GetOrAdd(chosen.Id, _ => new ConcurrentQueue<string>()).Enqueue(block);
        SelectionRouted?.Invoke(chosen.Id);
        await BringToFrontAsync().ConfigureAwait(false);
        var title = sessions.FirstOrDefault(s => s.Id == chosen.Id)?.Title;
        return new IdeDeliveryResult(true, $"Added to {(string.IsNullOrWhiteSpace(title) ? "the session" : $"\"{title}\"")} in Talavine Code.", chosen.Id);
    }

    private static Task BringToFrontAsync() => DesktopPlatform.InvokeAsync(() =>
    {
        try
        {
#if WINDOWS
            if (Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView is Microsoft.UI.Xaml.Window window)
            {
                window.Activate();
                var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
                if (NativeMethods.IsIconic(handle)) NativeMethods.ShowWindow(handle, 9 /* SW_RESTORE */);
                NativeMethods.SetForegroundWindow(handle);
            }
#elif MACOS
            DesktopPlatform.BringToFront();
#endif
        }
        catch (Exception ex)
        {
            DesktopHost.LogToFile($"Bring to front failed: {ex.Message}");
        }
    });

    // Windows: the shell resolves the vscode: protocol and file associations (throws when none is registered, so
    // the launcher moves on). macOS uses NSWorkspace on the main thread.
#if WINDOWS
    private static Task<bool> OpenUriAsync(Uri uri) => Task.FromResult(ShellOpen(uri.AbsoluteUri));

    private static Task<bool> OpenDefaultAsync(string path) => Task.FromResult(ShellOpen(path));

    private static bool ShellOpen(string target)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
        return true;
    }
#else
    private static Task<bool> OpenUriAsync(Uri uri)
        => DesktopPlatform.OpenUriAsync(uri);

    private static Task<bool> OpenDefaultAsync(string path)
        => DesktopPlatform.OpenFileAsync(path);
#endif

    public async ValueTask DisposeAsync()
    {
        if (_bridge is not null) await _bridge.DisposeAsync().ConfigureAwait(false);
        _bridge = null;
    }

#if WINDOWS
    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool IsIconic(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }
#endif
}
