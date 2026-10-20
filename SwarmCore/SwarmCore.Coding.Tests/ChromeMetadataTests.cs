using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SwarmCore.Coding.Computer;

namespace SwarmCore.Coding.Tests;

public sealed class ChromeMetadataTests
{
    [Fact]
    public void DefaultProfileCanUseConsentWithoutReadingOtherAppsDataButMalformedMetadataFailsClosed()
    {
        using var root = Talavine.TestSupport.TestTempDirectory.Reserve("chrome-consent-metadata");
        Assert.Equal(PersonalChromeConnection.ConsentEndpoint, PersonalChromeConnection.ReadEndpoint(root.Path, true));
        Assert.False(root.Exists);
        Assert.Throws<IOException>(() => PersonalChromeConnection.ReadEndpoint(root.Path, false));
        File.WriteAllText(Path.Combine(root.EnsureCreated(), "DevToolsActivePort"), "9222\n/devtools/browser/bad?query");
        Assert.Throws<IOException>(() => PersonalChromeConnection.ReadEndpoint(root.Path, true));
    }

    [Fact]
    public async Task ListsSleepingTabsWithoutAttachingRenderersAndKeepsStableIds()
    {
        using var port = new TcpListener(IPAddress.Loopback, 0);
        port.Start();
        var number = ((IPEndPoint)port.LocalEndpoint).Port;
        port.Stop();
        using var server = new HttpListener();
        server.Prefixes.Add($"http://127.0.0.1:{number}/");
        server.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var methods = new List<string>();
        var fixture = Task.Run(async () =>
        {
            var request = await server.GetContextAsync().WaitAsync(deadline.Token);
            using var socket = (await request.AcceptWebSocketAsync(null)).WebSocket;
            var buffer = new byte[4096];
            try
            {
                while (!deadline.IsCancellationRequested)
                {
                    var read = await socket.ReceiveAsync(buffer, deadline.Token);
                    if (read.MessageType == WebSocketMessageType.Close) return;
                    using var message = JsonDocument.Parse(buffer.AsMemory(0, read.Count));
                    var root = message.RootElement;
                    Assert.False(root.TryGetProperty("sessionId", out _));
                    var method = root.GetProperty("method").GetString()!;
                    methods.Add(method);
                    // Any attempt to initialize a renderer deliberately receives no reply.
                    object? result = method switch
                    {
                        "Browser.getVersion" => new { product = "Chrome/154" },
                        "Target.getTargets" => new { targetInfos = new[] {
                            new { targetId = "sleeping", type = "page", title = "Sleeping fixture", url = "https://example.test/page?token=private#secret" },
                            new { targetId = "awake", type = "page", title = "Awake fixture", url = "https://example.test/awake" },
                            new { targetId = "worker", type = "service_worker", title = "Worker", url = "https://example.test/sw.js" } } },
                        _ => null,
                    };
                    if (result is not null)
                        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { id = root.GetProperty("id").GetInt64(), result }),
                            WebSocketMessageType.Text, true, deadline.Token);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
        });
        try
        {
            await using var browser = new ChromeComputerSession(Options.Create(new CodingComputerOptions
                { ChromeCdpEndpoint = $"ws://127.0.0.1:{number}/browser" }));
            var first = await browser.ListAsync(deadline.Token);
            var second = await browser.ListAsync(deadline.Token);
            Assert.Equal(2, first.Count);
            Assert.Equal(first, second);
            Assert.Equal("sleeping", first[0].Id);
            Assert.DoesNotContain("private", first[0].Url);
            Assert.DoesNotContain("secret", first[0].Url);
            Assert.Equal(new[] { "Browser.getVersion", "Target.getTargets", "Target.getTargets" }, methods);
        }
        finally { deadline.Cancel(); server.Stop(); await fixture; }
    }

    [Theory]
    [InlineData("Browser.close")]
    [InlineData("Target.createBrowserContext")]
    [InlineData("Target.disposeBrowserContext")]
    [InlineData("Target.createTarget")]
    public void SelectedTabConnectionCannotCloseTheBrowserOrCreateOtherTargets(string method)
        => Assert.True(ChromeTabConnection.IsBlockedBrowserCommand(method));
}
