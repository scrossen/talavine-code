using System.Text.Json;
using AppKit;
using CoreGraphics;
using Foundation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebView;
using Microsoft.Extensions.FileProviders;
using SwarmCode.App.Services;
using WebKit;

namespace SwarmCode.App.Platforms.macOS;

/// <summary>
/// AppKit owns the window and WebKit lifecycle. Razor executes in-process through
/// the platform-neutral Blazor WebView renderer, without MAUI or a local HTTP server.
/// Only bundled app-origin content may communicate with the managed renderer.
/// </summary>
public sealed class MacBlazorWebView : IAsyncDisposable
{
    internal static readonly Uri Origin = NativeWebContentPolicy.Origin;
    private readonly Manager _manager;
    private readonly PhysicalFileProvider _files;
    private readonly MessageHandler _messages;
    private readonly SchemeHandler _scheme;
    private readonly NavigationDelegate _navigation;
    private readonly DialogDelegate _dialogs;
    public WKWebView WebView { get; }

    public MacBlazorWebView(IServiceProvider services, CGRect bounds)
    {
        var configuration = new WKWebViewConfiguration();
#if DEBUG
        configuration.Preferences.SetValueForKey(NSNumber.FromBoolean(true), new NSString("developerExtrasEnabled"));
#endif
        _messages = new MessageHandler(this);
        _scheme = new SchemeHandler(this);
        configuration.UserContentController.AddScriptMessageHandler(_messages, "swarmcode");
        configuration.UserContentController.AddUserScript(new WKUserScript(new NSString("""
            (() => {
                if (location.protocol !== 'app:' || location.hostname !== 'swarmcode') return;
                const callbacks = [];
                window.__swarmcodeReceive = message => callbacks.forEach(callback => callback(message));
                window.external = {
                    sendMessage: message => window.webkit.messageHandlers.swarmcode.postMessage(message),
                    receiveMessage: callback => callbacks.push(callback)
                };
                const report = error => window.webkit.messageHandlers.swarmcode.postMessage('__host_error:' + String(error));
                window.addEventListener('error', event => report(event.message));
                window.addEventListener('unhandledrejection', event => report(event.reason));
                Blazor.start().catch(report);
                window.addEventListener('pageshow', event => { if (event.persisted) location.reload(); });
            })();
            """), WKUserScriptInjectionTime.AtDocumentEnd, true));
        configuration.SetUrlSchemeHandler(_scheme, "app");
        WebView = new WKWebView(bounds, configuration)
        {
            AutoresizingMask = NSViewResizingMask.WidthSizable | NSViewResizingMask.HeightSizable,
        };
#if DEBUG
        WebView.Inspectable = true;
#endif
        _files = new PhysicalFileProvider(Path.Combine(NSBundle.MainBundle.ResourcePath, "wwwroot"));
        _manager = new Manager(services, WebView, _files);
        _navigation = new NavigationDelegate();
        _dialogs = new DialogDelegate();
        WebView.NavigationDelegate = _navigation;
        WebView.UIDelegate = _dialogs;
    }

    public void Start()
    {
        _ = _manager.AddRootComponentAsync(typeof(Components.Routes), "#app", ParameterView.Empty);
        Navigate("/");
    }
    public void Navigate(string path) => _manager.Navigate(path);

    private sealed class MacDispatcher : Dispatcher
    {
        public override bool CheckAccess() => NSThread.IsMain;
        public override Task InvokeAsync(Action workItem) => DesktopPlatform.InvokeAsync(workItem);
        public override Task InvokeAsync(Func<Task> workItem) => DesktopPlatform.InvokeAsync(workItem);
        public override Task<T> InvokeAsync<T>(Func<T> workItem) => DesktopPlatform.InvokeAsync(workItem);
        public override Task<T> InvokeAsync<T>(Func<Task<T>> workItem) => DesktopPlatform.InvokeAsync(workItem);
    }

    private sealed class Manager(IServiceProvider services, WKWebView view, IFileProvider files)
        : WebViewManager(services, new MacDispatcher(), Origin, files, new JSComponentConfigurationStore(), "index.html")
    {
        protected override void NavigateCore(Uri absoluteUri)
        {
            using var url = new NSUrl(absoluteUri.AbsoluteUri);
            using var request = new NSUrlRequest(url);
            view.LoadRequest(request);
        }
        protected override void SendMessage(string message)
        {
            if (message.StartsWith("__bwv:[\"NotifyUnhandledException\"", StringComparison.Ordinal))
                DesktopHost.LogToFile($"Native renderer failed: {message}");
            view.EvaluateJavaScript($"window.__swarmcodeReceive({JsonSerializer.Serialize(message)})", (_, error) =>
            {
                if (error is not null) DesktopHost.LogToFile($"WebKit renderer message failed: {error.LocalizedDescription}");
            });
        }
        public void Receive(string message)
        {
            if (message.StartsWith("__host_error:", StringComparison.Ordinal))
                DesktopHost.LogToFile("WebKit startup error: " + message[13..]);
            else MessageReceived(Origin, message);
        }
        public bool GetResponse(string url, out int status, out Stream content, out IDictionary<string, string> headers)
        {
            var uri = new Uri(url);
            // Route reloads resolve the host page. Missing static resources remain 404.
            var fallback = !Path.HasExtension(uri.AbsolutePath);
            return TryGetResponseContent(url, fallback, out status, out _, out content, out headers);
        }
    }

    private sealed class MessageHandler(MacBlazorWebView owner) : NSObject, IWKScriptMessageHandler
    {
        public void DidReceiveScriptMessage(WKUserContentController controller, WKScriptMessage message)
        {
            var url = message.FrameInfo.Request.Url?.AbsoluteString;
            if (!message.FrameInfo.MainFrame || !IsAppUrl(url)) return;
            if (message.Body is NSString value) owner._manager.Receive(value.ToString());
        }
    }

    internal static bool IsAppUrl(string? url)
        => NativeWebContentPolicy.IsAppUrl(url);

    private sealed class SchemeHandler(MacBlazorWebView owner) : NSObject, IWKUrlSchemeHandler
    {
        [Export("webView:startURLSchemeTask:")]
        public void StartUrlSchemeTask(WKWebView webView, IWKUrlSchemeTask task)
        {
            try
            {
                var url = task.Request.Url?.AbsoluteString;
                var status = 404;
                Stream content = Stream.Null;
                IDictionary<string, string> headers = new Dictionary<string, string>();
                if (IsAppUrl(url)) owner._manager.GetResponse(url!, out status, out content, out headers);
                using (content)
                using (var bytes = new MemoryStream())
                using (var fields = new NSMutableDictionary<NSString, NSString>())
                {
                    content.CopyTo(bytes);
                    foreach (var header in headers) fields[new NSString(header.Key)] = new NSString(header.Value);
                    fields[new NSString("Cache-Control")] = new NSString("no-store");
                    using var response = new NSHttpUrlResponse(task.Request.Url!, status, "HTTP/1.1", fields);
                    task.DidReceiveResponse(response);
                    using var data = NSData.FromArray(bytes.ToArray());
                    task.DidReceiveData(data);
                    task.DidFinish();
                }
            }
            catch (Exception ex)
            {
                DesktopHost.LogToFile($"Bundled web resource failed: {ex.Message}");
                task.DidFailWithError(new NSError(new NSString("TalavineCodeWebView"), 1));
            }
        }
        [Export("webView:stopURLSchemeTask:")]
        public void StopUrlSchemeTask(WKWebView webView, IWKUrlSchemeTask task) { }
    }

    private sealed class NavigationDelegate : WKNavigationDelegate
    {
        public override void DecidePolicy(WKWebView webView, WKNavigationAction action, Action<WKNavigationActionPolicy> decide)
        {
            var url = action.Request.Url?.AbsoluteString;
            if (action.TargetFrame is { } frame && NativeWebContentPolicy.CanNavigateInView(url, frame.MainFrame))
            {
                decide(WKNavigationActionPolicy.Allow);
                return;
            }
            decide(WKNavigationActionPolicy.Cancel);
            if (Uri.TryCreate(url, UriKind.Absolute, out var external)) _ = DesktopPlatform.OpenUriAsync(external);
        }
        public override void DidFailProvisionalNavigation(WKWebView webView, WKNavigation navigation, NSError error)
            => DesktopHost.LogToFile($"WebKit navigation failed: {error.LocalizedDescription}");
        public override void ContentProcessDidTerminate(WKWebView webView)
        {
            DesktopHost.LogToFile("WebKit content process exited; reloading the interface.");
            webView.Reload();
        }
    }

    private sealed class DialogDelegate : WKUIDelegate
    {
        public override void RunJavaScriptAlertPanel(WKWebView webView, string message, WKFrameInfo frame, Action completed)
        {
            using var alert = new NSAlert { MessageText = "Talavine Code", InformativeText = message };
            alert.AddButton("OK");
            alert.RunModal();
            completed();
        }
        public override void RunJavaScriptConfirmPanel(WKWebView webView, string message, WKFrameInfo frame, Action<bool> completed)
        {
            using var alert = new NSAlert { MessageText = "Talavine Code", InformativeText = message };
            alert.AddButton("OK");
            alert.AddButton("Cancel");
            completed(alert.RunModal() == 1000);
        }
        public override void RunOpenPanel(WKWebView webView, WKOpenPanelParameters parameters, WKFrameInfo frame, Action<NSUrl[]?> completed)
        {
            using var panel = NSOpenPanel.OpenPanel;
            panel.AllowsMultipleSelection = parameters.AllowsMultipleSelection;
            panel.CanChooseFiles = true;
            panel.CanChooseDirectories = parameters.AllowsDirectories;
            // The RC2 generated Objective-C block trampoline rejects null arrays
            // despite this callback's nullable signature. An empty selection
            // cancels the upload without throwing across the native boundary.
            completed(panel.RunModal() == 1 ? panel.Urls : []);
        }
    }

    public async ValueTask DisposeAsync()
    {
        WebView.StopLoading();
        WebView.Configuration.UserContentController.RemoveScriptMessageHandler("swarmcode");
        await _manager.DisposeAsync();
        WebView.NavigationDelegate = null;
        WebView.UIDelegate = null;
        WebView.Dispose();
        _files.Dispose();
        _messages.Dispose();
        _scheme.Dispose();
        _navigation.Dispose();
        _dialogs.Dispose();
    }
}
