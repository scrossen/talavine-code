using AppKit;
using CoreGraphics;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using SwarmCode.App.Design;
using SwarmCode.App.Services;

namespace SwarmCode.App.Platforms.macOS;

[Register("AppDelegate")]
public sealed class AppDelegate : NSApplicationDelegate
{
    private ServiceProvider? _services;
    private NSWindow? _window;
    private MacBlazorWebView? _view;
    private bool _shuttingDown;

    public override void DidFinishLaunching(NSNotification notification)
    {
        try
        {
            _services = DesktopHost.CreateNativeServices();
            NSApplication.SharedApplication.ActivationPolicy = NSApplicationActivationPolicy.Regular;
            _window = new NSWindow(new CGRect(0, 0, TalavineCodeDesign.WindowWidth, TalavineCodeDesign.WindowHeight),
                NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Miniaturizable | NSWindowStyle.Resizable,
                NSBackingStore.Buffered, false)
            {
                Title = "Talavine Code",
                ContentMinSize = new CGSize(TalavineCodeDesign.WindowMinWidth, TalavineCodeDesign.WindowMinHeight),
                ReleasedWhenClosed = false,
                FrameAutosaveName = "TalavineCodeMainWindow",
                CollectionBehavior = NSWindowCollectionBehavior.FullScreenPrimary,
            };
            _view = new MacBlazorWebView(_services, _window.ContentView!.Bounds);
            _window.ContentView = _view.WebView;
            BuildMenu();
            _window.Center();
            _window.MakeKeyAndOrderFront(null);
            DesktopPlatform.BringToFront();
            _view.Start();
            DesktopHost.LogToFile("Native AppKit host started (macOS / WKWebView).");
        }
        catch (Exception ex)
        {
            DesktopHost.LogToFile($"Native startup failed: {ex}");
            using var alert = new NSAlert { MessageText = "Talavine Code could not start", InformativeText = ex.Message, AlertStyle = NSAlertStyle.Critical };
            alert.RunModal();
            NSApplication.SharedApplication.Terminate(null);
        }
    }

    private void BuildMenu()
    {
        var menu = new NSMenu();
        var app = new NSMenu();
        app.AddItem(new NSMenuItem("About Talavine Code", (_, _) => NSApplication.SharedApplication.OrderFrontStandardAboutPanel(null)));
        app.AddItem(new NSMenuItem("Check for Updates…", async (_, _) =>
        {
            if (_services is not null) await _services.GetRequiredService<SwarmCodeUpdates>().CheckAsync();
        }));
        app.AddItem(NSMenuItem.SeparatorItem);
        app.AddItem(new NSMenuItem("Settings…", ",", (_, _) => _view?.Navigate("/settings")));
        app.AddItem(NSMenuItem.SeparatorItem);
        app.AddItem(new NSMenuItem("Hide Talavine Code", "h", (_, _) => NSApplication.SharedApplication.Hide(null)));
        app.AddItem(new NSMenuItem("Quit Talavine Code", "q", (_, _) => DesktopPlatform.Quit()));
        menu.AddItem(new NSMenuItem { Submenu = app });
        var file = new NSMenu();
        file.AddItem(new NSMenuItem("New Project…", (_, _) => _view?.Navigate("/new-project")));
        file.AddItem(new NSMenuItem("New Chat", "n", async (_, _) =>
        {
            try
            {
                if (_services is null) return;
                var session = await _services.GetRequiredService<AppState>().NewSessionAsync();
                _view?.Navigate(AppNavigation.SessionUrl(session.Id));
            }
            catch (Exception ex) { DesktopHost.LogToFile($"New chat failed: {ex}"); }
        }));
        file.AddItem(new NSMenuItem("Open Folder…", "o", async (_, _) =>
        {
            try
            {
                if (_services is not null && await _services.GetRequiredService<RepositoryOpener>().PickAndOpenAsync())
                    _view?.Navigate("/");
            }
            catch (Exception ex) { DesktopHost.LogToFile($"Open folder failed: {ex.Message}"); }
        }));
        file.AddItem(new NSMenuItem("Close Project", async (_, _) =>
        {
            try
            {
                if (_services is null) return;
                await _services.GetRequiredService<AppState>().CloseProjectAsync();
                _view?.Navigate("/");
            }
            catch (Exception ex) { DesktopHost.LogToFile($"Close project failed: {ex}"); }
        }));
        file.AddItem(new NSMenuItem("Close Window", new ObjCRuntime.Selector("performClose:"), "w"));
        menu.AddItem(new NSMenuItem("File") { Submenu = file });
        var edit = new NSMenu();
        foreach (var (title, selector, key) in new[] { ("Undo", "undo:", "z"), ("Redo", "redo:", "Z"), ("Cut", "cut:", "x"), ("Copy", "copy:", "c"), ("Paste", "paste:", "v"), ("Select All", "selectAll:", "a") })
            edit.AddItem(new NSMenuItem(title, new ObjCRuntime.Selector(selector), key));
        menu.AddItem(new NSMenuItem("Edit") { Submenu = edit });
        var window = new NSMenu();
        window.AddItem(new NSMenuItem("Minimize", new ObjCRuntime.Selector("performMiniaturize:"), "m"));
        window.AddItem(new NSMenuItem("Zoom", new ObjCRuntime.Selector("performZoom:"), ""));
        menu.AddItem(new NSMenuItem("Window") { Submenu = window });
        NSApplication.SharedApplication.MainMenu = menu;
        NSApplication.SharedApplication.WindowsMenu = window;
    }

    public override bool ApplicationShouldHandleReopen(NSApplication sender, bool hasVisibleWindows)
    {
        _window?.MakeKeyAndOrderFront(null);
        return true;
    }

    public override NSApplicationTerminateReply ApplicationShouldTerminate(NSApplication sender)
    {
        if (_shuttingDown) return NSApplicationTerminateReply.Now;
        _shuttingDown = true;
        _ = ShutdownAsync(sender);
        return NSApplicationTerminateReply.Later;
    }

    // Closing the main window follows the same graceful shutdown as Quit,
    // including stopping runners and disposing the workspace and WebView.
    public override bool ApplicationShouldTerminateAfterLastWindowClosed(NSApplication sender) => true;

    private async Task ShutdownAsync(NSApplication app)
    {
        try
        {
            if (_services is not null)
            {
                if (_view is not null) await _view.DisposeAsync();
                await _services.DisposeAsync();
            }
        }
        catch (Exception ex) { DesktopHost.LogToFile($"Native shutdown failed: {ex}"); }
        finally { app.ReplyToApplicationShouldTerminate(true); }
    }
}
