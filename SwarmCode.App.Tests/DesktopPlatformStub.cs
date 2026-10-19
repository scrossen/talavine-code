namespace SwarmCode.App.Services;

internal static class DesktopPlatform
{
    public static Task<string?> PickFileAsync(string title, params string[] extensions) => Task.FromResult<string?>(null);
}
