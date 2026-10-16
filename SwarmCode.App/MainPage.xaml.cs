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
    }

    private void BlazorWebView_BlazorWebViewInitializing(object? sender, BlazorWebViewInitializingEventArgs e)
    {
#if WINDOWS
        var additionalArgs = "--enable-features=msSpellCheck";
        var cdpPort = Environment.GetEnvironmentVariable("SWARMCODE_CDP_PORT");
        if (!string.IsNullOrWhiteSpace(cdpPort))
        {
            additionalArgs += $" --remote-debugging-port={cdpPort} --remote-allow-origins=*";
            MauiProgram.LogToFile($"CDP debugging enabled on port {cdpPort}");
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
