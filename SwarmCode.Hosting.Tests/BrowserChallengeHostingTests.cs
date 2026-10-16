using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using SwarmCore.Abstractions.Computer;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Coding.Computer;

namespace SwarmCode.Hosting.Tests;

public sealed class BrowserChallengeHostingTests : TempDirTest
{
    private sealed class ChromeChallengeFactAttribute : FactAttribute
    {
        public ChromeChallengeFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("SWARMCODE_CHROME_SESSION_PROOF") != "1")
                Skip = "Set SWARMCODE_CHROME_SESSION_PROOF=1 to use installed Chrome with a disposable fixture profile.";
        }
    }
    private sealed class VisionChallengeFactAttribute : FactAttribute
    {
        public VisionChallengeFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("SWARMCODE_CHALLENGE_VISION_PROOF") != "1")
                Skip = "Set SWARMCODE_CHALLENGE_VISION_PROOF=1 and SWARMCODE_CHALLENGE_VISION_MODEL to test an installed local vision model.";
        }
    }
    private sealed class FixturePlanner : IBrowserChallengePlanner
    {
        public Task<BrowserChallengeDecision> NextAsync(BrowserObservation o, BrowserChallengeRegion r, int actions, CancellationToken ct)
            => Task.FromResult(o.VisibleText.Contains("Verification complete")
                ? new BrowserChallengeDecision("clear") : new("act", new("click") { X = 400, Y = 300 }));
    }
    private sealed class DiagnosticPlanner(IBrowserChallengePlanner inner, Action<Exception> failed, List<BrowserChallengeDecision> decisions) : IBrowserChallengePlanner
    {
        public async Task<BrowserChallengeDecision> NextAsync(BrowserObservation o, BrowserChallengeRegion r, int actions, CancellationToken ct)
        {
            try { var decision = await inner.NextAsync(o, r, actions, ct); decisions.Add(decision); return decision; }
            catch (Exception ex) { failed(ex); throw; }
        }
    }

    [ChromeChallengeFact, Trait("Category", "InstalledChrome")]
    public Task Native_bridge_solves_a_fixture_in_installed_Chrome_and_verifies_the_page() => RunAsync(vision: false);

    [VisionChallengeFact, Trait("Category", "LiveLlm")]
    public Task Real_local_vision_solves_an_arithmetic_fixture_through_the_native_bridge() => RunAsync(vision: true);

    private async Task RunAsync(bool vision)
    {
        using var portProbe = new TcpListener(IPAddress.Loopback, 0); portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
        var origin = $"http://127.0.0.1:{port}";
        using var listener = new HttpListener(); listener.Prefixes.Add(origin + "/"); listener.Start();
        var server = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext request;
                try { request = await listener.GetContextAsync(); }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { break; }
                var bytes = Encoding.UTF8.GetBytes("""
                    <!DOCTYPE html><html><head><title>Local challenge fixture</title>
                    <style>body{margin:0;font:26px Arial;background:#eee}section{position:absolute;left:80px;top:80px;width:640px;height:420px;background:white;text-align:center}button{position:absolute;top:185px;width:120px;height:70px;font:32px Arial}#a{left:80px}#b{left:260px}#c{left:440px}</style></head>
                    <body><section id="challenge"><h1>Human verification</h1><p>Select the answer to 2 + 3</p>
                    <button id="a" onclick="this.style.background='pink'">4</button>
                    <button id="b" onclick="document.getElementById('challenge').innerHTML='<h1 id=completed>Verification complete</h1><p>Original task ready</p>'">5</button>
                    <button id="c" onclick="this.style.background='pink'">6</button></section></body></html>
                    """);
                request.Response.ContentType = "text/html"; request.Response.ContentLength64 = bytes.Length;
                await request.Response.OutputStream.WriteAsync(bytes); request.Response.Close();
            }
        });
        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchPersistentContextAsync(Path.Combine(Tmp, "chrome"), new()
            {
                Channel = "chrome", Headless = true, ViewportSize = new() { Width = 800, Height = 600 },
                Args = ["--remote-debugging-port=0", "--remote-debugging-address=127.0.0.1"],
            });
            var page = await browser.NewPageAsync(); await page.GotoAsync(origin);
            var settings = new Dictionary<string, string?>
            {
                ["Coding:Computer:PersonalChromeUserDataDirectory"] = Path.Combine(Tmp, "chrome"),
                ["Coding:Computer:ChallengeTimeoutSeconds"] = "180",
                ["Coding:Computer:ChallengeMaxActions"] = "6",
                ["Coding:Agents:Enabled"] = "false", ["Coding:FallbackDiscovery:Enabled"] = "false",
                ["PersonalMemory:Enabled"] = "false",
            };
            Exception? visionFailure = null;
            List<BrowserChallengeDecision> decisions = [];
            await using var host = await BridgedWorkspace.StartAsync(Paths, Path.Combine(Tmp, "repo"), settings,
                configure: services =>
                {
                    if (!vision) services.AddSingleton<IBrowserChallengePlanner, FixturePlanner>();
                    else services.AddSingleton<IBrowserChallengePlanner>(sp => new DiagnosticPlanner(
                        new RoutedBrowserChallengePlanner(sp.GetRequiredService<IServiceScopeFactory>()), ex => visionFailure = ex, decisions));
                });
            if (vision)
            {
                var model = Environment.GetEnvironmentVariable("SWARMCODE_CHALLENGE_VISION_MODEL");
                Assert.False(string.IsNullOrWhiteSpace(model), "Select an already installed vision model; this test never downloads one.");
                // Only this temporary test host's preference changes. The person's settings and accounts are untouched.
                await using var scope = host.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ILlmRouter>().SetFunctionPreferenceAsync(FunctionTypes.CaptchaSolving,
                    "Ollama", model!, requiresVision: true, userPinned: true);
            }
            var bridge = host.Turn("challenge-test");
            Assert.Contains(await bridge.ListAsync(default), t => t.Name == "browser_solve_challenge");
            Assert.DoesNotContain(await host.Turn("plan-test", planMode: true).ListAsync(default), t => t.Name == "browser_solve_challenge");
            var computer = host.Services.GetRequiredService<ICodingComputer>();
            var tab = Assert.Single(await computer.BrowserTabsAsync(), t => t.Url.TrimEnd('/') == origin);
            var before = await computer.BrowserObserveAsync(tab.Id);
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            var result = await bridge.CallAsync("browser_solve_challenge",
                BridgedWorkspace.Args(new BrowserChallengeInput(tab.Id, new(80, 80, 640, 420))), deadline.Token);
            var after = await computer.BrowserObserveAsync(tab.Id);
            var evidence = Environment.GetEnvironmentVariable("SWARMCODE_CHALLENGE_EVIDENCE_DIR");
            if (!string.IsNullOrWhiteSpace(evidence))
            {
                Directory.CreateDirectory(evidence);
                var prefix = vision ? "vision" : "chrome";
                await File.WriteAllTextAsync(Path.Combine(evidence, prefix + "-result.json"), result.Text);
                await File.WriteAllTextAsync(Path.Combine(evidence, prefix + "-decisions.json"), JsonSerializer.Serialize(decisions));
                await File.WriteAllBytesAsync(Path.Combine(evidence, prefix + "-before.png"), Convert.FromBase64String(before.Screenshot.PngBase64!));
                await File.WriteAllBytesAsync(Path.Combine(evidence, prefix + "-after.png"), Convert.FromBase64String(after.Screenshot.PngBase64!));
            }
            Assert.False(result.IsError, result.Text);
            Assert.True(visionFailure is null, visionFailure?.ToString());
            using var document = JsonDocument.Parse(result.Text);
            Assert.Equal("appears_clear", document.RootElement.GetProperty("Status").GetString());
            // Independent page assertion, not the model's verdict, proves the intended fixture transition.
            Assert.Equal("Verification complete", await page.Locator("#completed").TextContentAsync());
            Assert.Contains("Original task ready", after.VisibleText);
            Assert.NotEmpty(result.Images);
        }
        finally { listener.Stop(); await server; }
    }
}
