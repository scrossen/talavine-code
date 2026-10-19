using Microsoft.AspNetCore.Components.WebView;
using Microsoft.AspNetCore.Components.WebView.Maui;

#if WINDOWS
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
#endif

namespace SwarmCode.App;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();
#if WINDOWS
        // MAUI initializes WinUI with the default environment unless the native
        // controller already exists. Delay the Blazor host until the bundled
        // runtime's controller is ready, then apply the host-page mapper.
        blazorWebView.Loaded += InitializeWindowsBrowser;
#else
        blazorWebView.HostPage = "wwwroot/index.html";
#endif
    }

#if WINDOWS
    private bool _browserStarted;

    private async void InitializeWindowsBrowser(object? sender, EventArgs e)
    {
        if (_browserStarted || blazorWebView.Handler?.PlatformView is not WebView2 win) return;
        _browserStarted = true;
        try
        {
            var options = new BlazorWebViewInitializingEventArgs();
            BlazorWebView_BlazorWebViewInitializing(this, options);
            if (!string.IsNullOrWhiteSpace(options.BrowserExecutableFolder))
            {
                var environment = await CoreWebView2Environment.CreateWithOptionsAsync(
                    options.BrowserExecutableFolder, options.UserDataFolder, options.EnvironmentOptions).AsTask();
                DesktopHost.LogToFile("Custom browser environment ready");
                await win.EnsureCoreWebView2Async(environment).AsTask();
                DesktopHost.LogToFile("Custom browser controller ready");
            }
            blazorWebView.HostPage = "wwwroot/index.html";
            blazorWebView.Handler?.UpdateValue(nameof(BlazorWebView.HostPage));
            DesktopHost.LogToFile("Blazor host page assigned");
        }
        catch (Exception exception)
        {
            DesktopHost.LogToFile($"Browser initialization failed: {exception}");
            Content = new Label { Text = "Talavine Code could not start its browser: " + exception.Message,
                Margin = new Thickness(24), LineBreakMode = LineBreakMode.WordWrap };
        }
    }
#endif

    private void BlazorWebView_BlazorWebViewInitializing(object? sender, BlazorWebViewInitializingEventArgs e)
    {
#if WINDOWS
        // WinUI's WinRT WebView2 creation needs the folder passed explicitly.
        // Its runtime detection does not reliably use the process override alone.
        var browserFolder = Environment.GetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER");
        if (!string.IsNullOrWhiteSpace(browserFolder) && File.Exists(Path.Combine(browserFolder, "msedgewebview2.exe")))
        {
            e.BrowserExecutableFolder = browserFolder;
            DesktopHost.LogToFile($"Using WebView2 runtime: {browserFolder}");
        }
        var additionalArgs = "--enable-features=msSpellCheck";
        var cdpPort = Environment.GetEnvironmentVariable("SWARMCODE_CDP_PORT");
        if (!string.IsNullOrWhiteSpace(cdpPort))
        {
            additionalArgs += $" --remote-debugging-port={cdpPort} --remote-allow-origins=*";
            DesktopHost.LogToFile($"CDP debugging enabled on port {cdpPort}");
        }

        e.EnvironmentOptions = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = additionalArgs };
#endif
    }

    private void BlazorWebView_BlazorWebViewInitialized(object? sender, BlazorWebViewInitializedEventArgs e)
    {
#if WINDOWS
        if (e.WebView is WebView2 win)
        {
            win.CoreWebView2Initialized += (_, _) => ApplyWindowsEditingSettings(win);
            ApplyWindowsEditingSettings(win);
        }
#endif
    }

#if WINDOWS
    private static void ApplyWindowsEditingSettings(WebView2 win)
    {
        var settings = win.CoreWebView2?.Settings;
        if (settings is null) return;
        settings.AreDefaultContextMenusEnabled = true;
        settings.AreBrowserAcceleratorKeysEnabled = true;
    }
#endif
}
