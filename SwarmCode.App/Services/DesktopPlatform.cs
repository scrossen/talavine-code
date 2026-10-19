#if MACOS
using AppKit;
using Foundation;
#else
using Microsoft.Maui.ApplicationModel;
#endif

namespace SwarmCode.App.Services;

/// <summary>Desktop shell operations, independent of the component renderer.</summary>
public static class DesktopPlatform
{
#if MACOS
    public static Task InvokeAsync(Action action) => InvokeAsync(() => { action(); return Task.CompletedTask; });
    public static Task InvokeAsync(Func<Task> action)
    {
        if (NSThread.IsMain) return action();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NSApplication.SharedApplication.BeginInvokeOnMainThread(async () =>
        {
            try { await action(); completion.SetResult(); }
            catch (Exception ex) { completion.SetException(ex); }
        });
        return completion.Task;
    }
    public static Task<T> InvokeAsync<T>(Func<T> action) => InvokeAsync(() => Task.FromResult(action()));
    public static Task<T> InvokeAsync<T>(Func<Task<T>> action)
    {
        if (NSThread.IsMain) return action();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        NSApplication.SharedApplication.BeginInvokeOnMainThread(async () =>
        {
            try { completion.SetResult(await action()); }
            catch (Exception ex) { completion.SetException(ex); }
        });
        return completion.Task;
    }
    public static Task<bool> OpenUriAsync(Uri uri) => InvokeAsync(() =>
    {
        if (uri.Scheme is not ("http" or "https" or "mailto" or "vscode" or "vscode-insiders")) return false;
        using var url = new NSUrl(uri.AbsoluteUri);
        return NSWorkspace.SharedWorkspace.OpenUrl(url);
    });
    public static Task<bool> OpenFileAsync(string path) => InvokeAsync(() =>
    {
        using var url = NSUrl.FromFilename(Path.GetFullPath(path));
        return NSWorkspace.SharedWorkspace.OpenUrl(url);
    });
    public static Task<string?> PickFileAsync(string title, params string[] extensions) => InvokeAsync(() =>
    {
        using var panel = NSOpenPanel.OpenPanel;
        panel.CanChooseFiles = true;
        panel.CanChooseDirectories = false;
        panel.AllowsMultipleSelection = false;
        panel.Message = title;
        if (extensions.Length > 0)
            panel.AllowedContentTypes = extensions.Select(extension => UniformTypeIdentifiers.UTType.CreateFromExtension(extension.TrimStart('.'))!).ToArray();
        return panel.RunModal() == 1 ? panel.Url?.Path : null;
    });
    public static void BringToFront()
    {
        NSApplication.SharedApplication.ActivateIgnoringOtherApps(true);
        NSApplication.SharedApplication.MainWindow?.MakeKeyAndOrderFront(null);
    }
    public static void Quit() => NSApplication.SharedApplication.Terminate(null);
    public static void SetTheme(string theme) => _ = InvokeAsync(() =>
        NSApplication.SharedApplication.Appearance = theme switch
        {
            "light" => NSAppearance.GetAppearance(NSAppearance.NameAqua),
            "dark" => NSAppearance.GetAppearance(NSAppearance.NameDarkAqua),
            _ => null,
        });
#else
    public static Task InvokeAsync(Action action) => MainThread.InvokeOnMainThreadAsync(action);
    public static Task InvokeAsync(Func<Task> action) => MainThread.InvokeOnMainThreadAsync(action);
    public static Task<T> InvokeAsync<T>(Func<T> action) => MainThread.InvokeOnMainThreadAsync(action);
    public static Task<T> InvokeAsync<T>(Func<Task<T>> action) => MainThread.InvokeOnMainThreadAsync(action);
    public static Task<bool> OpenUriAsync(Uri uri) => MainThread.InvokeOnMainThreadAsync(() => Launcher.Default.TryOpenAsync(uri));
    public static Task<bool> OpenFileAsync(string path) => MainThread.InvokeOnMainThreadAsync(() => Launcher.Default.OpenAsync(new OpenFileRequest(Path.GetFileName(path), new ReadOnlyFile(path))));
    public static async Task<string?> PickFileAsync(string title, params string[] extensions)
    {
        var file = await Microsoft.Maui.Storage.FilePicker.Default.PickAsync(new Microsoft.Maui.Storage.PickOptions
        {
            PickerTitle = title,
            FileTypes = extensions.Length == 0 ? null : new Microsoft.Maui.Storage.FilePickerFileType(
                new Dictionary<Microsoft.Maui.Devices.DevicePlatform, IEnumerable<string>>
                { [Microsoft.Maui.Devices.DevicePlatform.WinUI] = extensions.Select(x => "." + x.TrimStart('.')) }),
        });
        return file?.FullPath;
    }
    public static void Quit() => Application.Current?.Quit();
    public static void SetTheme(string theme)
    {
        if (Application.Current is { } app)
            app.UserAppTheme = theme switch { "light" => AppTheme.Light, "dark" => AppTheme.Dark, _ => AppTheme.Unspecified };
    }
#endif
}
