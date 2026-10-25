namespace SwarmCode.App.Services;

internal static class DesktopPlatform
{
    public static void Quit() { }
    public static Task<string?> PickFileAsync(string title, params string[] extensions) => Task.FromResult<string?>(null);
    public static Task<bool> OpenUriAsync(Uri uri) => Task.FromResult(false);
    public static Task<bool> OpenFileAsync(string path) => Task.FromResult(false);
    public static Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
}
