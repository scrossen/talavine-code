using System.Diagnostics;
using SwarmCore.Abstractions.Packaging;
#if MACOS
using Foundation;
#endif

namespace SwarmCode.App.Services;

/// <summary>Owns desktop update UI and lifecycle; trust and download rules live in SwarmCore.</summary>
public sealed class SwarmCodeUpdates : IDisposable
{
    public const string ManifestUrl = "https://talavine.com/downloads/SwarmCode-latest.json";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(45) };
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _gate = new(1);
    private readonly SignedAppUpdateClient _client;
    private SignedAppReleaseManifest? _available;
    private string? _dismissed;
    private bool _started;
    public event Action? Changed;
    public bool Busy { get; private set; }
    public string? Message { get; private set; }
    public string? AvailableVersion => _available?.Mac.Version;
    public string CurrentVersion { get; } =
#if MACOS
        NSBundle.MainBundle.ObjectForInfoDictionary("CFBundleShortVersionString")?.ToString() ?? "1.0.0";
#else
        Microsoft.Maui.ApplicationModel.AppInfo.Current.VersionString;
#endif

    public SwarmCodeUpdates() => _client = new(_http, "SwarmCode", "stable",
        SwarmFamilyReleaseTrust.PublisherPublicKeyPem, SignedAppReleasePlatform.Mac);

    public void Start()
    {
        if (_started || !OperatingSystem.IsMacOS()) return;
        _started = true;
#if DEBUG
        if (Environment.GetEnvironmentVariable("SWARMCODE_ENABLE_DEBUG_AUTO_UPDATE_CHECKS") != "1") return;
#endif
        _ = Task.Run(async () =>
        {
            try
            {
                await CheckAsync(false);
                using var timer = new PeriodicTimer(TimeSpan.FromDays(1));
                while (await timer.WaitForNextTickAsync(_stop.Token)) await CheckAsync(false);
            }
            catch (OperationCanceledException) { }
        });
    }

    public async Task CheckAsync(bool manual = true)
    {
        if (!OperatingSystem.IsMacOS()) { Message = "Updates for this platform are not published yet."; Changed?.Invoke(); return; }
        if (!await _gate.WaitAsync(0)) return;
        try
        {
            Busy = true;
            if (manual) { Message = "Checking for updates…"; Changed?.Invoke(); }
            var manifest = await _client.ReadAsync(new Uri(ManifestUrl), _stop.Token);
            if (_client.IsNewer(manifest, CurrentVersion) && (manual || _dismissed != manifest.Mac.Version))
            {
                _available = manifest;
                Message = $"Talavine Code {manifest.Mac.Version} is available.";
            }
            else if (manual) Message = "Talavine Code is up to date.";
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            DesktopHost.LogToFile($"Update check failed: {ex.Message}");
            if (manual) Message = "Could not check for updates. " + ex.Message;
        }
        finally { Busy = false; _gate.Release(); Changed?.Invoke(); }
    }

    public void Dismiss() { _dismissed = AvailableVersion; _available = null; Message = null; Changed?.Invoke(); }

    public async Task InstallAsync()
    {
#if MACOS
        if (_available is null || !await _gate.WaitAsync(0)) return;
        try
        {
            Busy = true;
            Message = "Preparing the verified update…"; Changed?.Invoke();
            if (AppState.Instance?.BusyReasons().Count > 0)
                throw new InvalidOperationException("Finish or stop active work before restarting to update.");
            var manifest = _available;
            if (!_client.IsNewer(manifest, CurrentVersion)) throw new InvalidOperationException("This update is no longer newer.");
            var plan = _client.Validate(manifest);
            var bytes = await _client.ReadInstallerAsync(manifest, _stop.Token);
            var dir = Path.Combine(Path.GetTempPath(), "swarmcode-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var script = Path.Combine(dir, "install.sh");
            await File.WriteAllBytesAsync(script, bytes, _stop.Token);
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var start = new ProcessStartInfo("/bin/bash") { UseShellExecute = false };
            foreach (var arg in new[] { script, "--asset-url", plan.Asset.DownloadUrl, "--asset-sha256", plan.Asset.Sha256,
                "--asset-version", plan.Asset.Version, "--target-app", NSBundle.MainBundle.BundlePath,
                "--wait-pid", Environment.ProcessId.ToString() }) start.ArgumentList.Add(arg);
            // A detached updater must survive the application's child-process cleanup.
            using var helper = Process.Start(start) ?? throw new IOException("Could not start the updater.");
            await DesktopPlatform.InvokeAsync(DesktopPlatform.Quit);
        }
        catch (Exception ex) { Message = "Could not start the update. " + ex.Message; }
        finally { Busy = false; _gate.Release(); Changed?.Invoke(); }
#else
        await Task.CompletedTask;
#endif
    }

    public void Dispose() { _stop.Cancel(); _http.Dispose(); }
}
