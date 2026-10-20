using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using SwarmCore.Coding.Computer;

namespace SwarmCore.Coding.Tests;

public sealed class ChromeSessionReuseTests
{
    private sealed class InstalledChromeFactAttribute : FactAttribute
    {
        public InstalledChromeFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("SWARMCODE_CHROME_SESSION_PROOF") != "1")
                Skip = "Set SWARMCODE_CHROME_SESSION_PROOF=1 to run against installed Chrome.";
        }
    }

    [InstalledChromeFact]
    [Trait("Category", "InstalledChrome")]
    public async Task AttachesToExistingChromeAndReusesItsSessionAcrossClients()
    {
        // Explicit native-browser proof: no real account, no cookie extraction, no private profile mutation.
        var root = Path.Combine(Path.GetTempPath(), "swarmcode-chrome-proof-" + Guid.NewGuid().ToString("N"));
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var origin = $"http://127.0.0.1:{port}";
        using var listener = new HttpListener();
        listener.Prefixes.Add(origin + "/");
        listener.Start();
        var server = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext request;
                try { request = await listener.GetContextAsync(); }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { break; }
                var login = request.Request.Url!.AbsolutePath == "/login";
                if (login) request.Response.Headers.Add("Set-Cookie", "fixture-session=synthetic; HttpOnly; SameSite=Lax; Path=/");
                var signedIn = login || request.Request.Cookies["fixture-session"]?.Value == "synthetic";
                var bytes = Encoding.UTF8.GetBytes($"<html><head><title>{(signedIn ? "Fixture signed in" : "Fixture signed out")}</title></head><body>{(signedIn ? "Existing session active" : "Sign in required")}</body></html>");
                request.Response.ContentType = "text/html";
                request.Response.ContentLength64 = bytes.Length;
                await request.Response.OutputStream.WriteAsync(bytes);
                request.Response.Close();
            }
        });
        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var existing = await playwright.Chromium.LaunchPersistentContextAsync(root, new()
            {
                Channel = "chrome", Headless = true,
                Args = ["--remote-debugging-port=0", "--remote-debugging-address=127.0.0.1"],
            });
            var originalTab = await existing.NewPageAsync();
            await originalTab.GotoAsync(origin + "/login");
            // A real, unrelated renderer that never answers Page/Runtime initialization.
            // Eager ConnectOverCDP used to block every tab on this renderer.
            var sleepingTab = await existing.NewPageAsync();
            await sleepingTab.GotoAsync("data:text/html,<title>Unresponsive unrelated fixture</title>");
            await sleepingTab.EvaluateAsync("setTimeout(() => { for (;;) {} }, 100)");
            await Task.Delay(200);
            var options = Options.Create(new CodingComputerOptions { PersonalChromeUserDataDirectory = root });
            string openedUrl;
            await using (var first = new ChromeComputerSession(options))
            {
                Assert.Contains(await first.ListAsync(), t => t.Url == origin + "/login");
                var opened = await first.OpenAsync(origin + "/protected");
                Assert.Equal("Fixture signed in", opened.Title);
                var observation = await first.ObserveAsync(opened.Id);
                Assert.Contains("Existing session active", observation.VisibleText);
                var screenshot = observation.Screenshot;
                var batch = new SwarmCore.Abstractions.Computer.ComputerActionBatch(screenshot.SessionId,
                    screenshot.SurfaceId, screenshot.ObservationId, [new("screenshot")]);
                Assert.True((await first.ActAsync(batch)).Succeeded);
                Assert.False((await first.ActAsync(batch)).Succeeded);
                openedUrl = opened.Url;
            }
            Assert.False(originalTab.IsClosed);
            await using var second = new ChromeComputerSession(options);
            Assert.Contains(await second.ListAsync(), t => t.Url == openedUrl);
            Assert.Equal("Fixture signed in", (await second.OpenAsync(origin + "/protected-again")).Title);
        }
        finally
        {
            listener.Stop();
            await server;
            // This path was generated solely for this test; the user's Chrome directories are never touched.
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
