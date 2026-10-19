using AppKit;
using Foundation;

namespace SwarmCode.App;

internal static class Program
{
    private static Platforms.macOS.AppDelegate? _delegate;

    private static int Main(string[] args)
    {
        // The artifact gate exercises the actual native runtime without opening
        // windows, starting workers, reading a profile or contacting providers.
        if (args is ["--verify-runtime-startup"])
        {
            Console.WriteLine("PASS: native runtime started.");
            return 0;
        }
        NSApplication.Init();
        // Apple bundles put Content in Resources, separate from the managed
        // assemblies in MonoBundle where Playwright normally looks for it.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PLAYWRIGHT_DRIVER_SEARCH_PATH")))
            Environment.SetEnvironmentVariable("PLAYWRIGHT_DRIVER_SEARCH_PATH", NSBundle.MainBundle.ResourcePath);

        // Exercise the signed app's driver discovery without opening a browser,
        // composing a workspace, or reading a user profile.
        if (args is ["--verify-playwright-driver"])
        {
            try
            {
                using var playwright = Microsoft.Playwright.Playwright.CreateAsync().GetAwaiter().GetResult();
                Console.WriteLine("PASS: bundled Playwright driver started.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }
#if DEBUG
        if (args is ["--verify-local-profile"])
            return Task.Run(() => Platforms.macOS.MacProfileVerification.RunAsync()).GetAwaiter().GetResult();
        if (args is ["--verify-credential-receipt", var receipt])
            return Task.Run(() => Platforms.macOS.MacProfileVerification.RunAsync(receipt)).GetAwaiter().GetResult();
#endif
        _delegate = new Platforms.macOS.AppDelegate();
        NSApplication.SharedApplication.Delegate = _delegate;
        NSApplication.Main(args);
        return 0;
    }
}
