using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Coding.Web;

namespace SwarmCore.Coding.Tests;

public class WebToolsTests
{
    private const string Key = "BSA-test-key-0123456789abcdef"; // gitleaks:allow -- synthetic test fixture, never a live credential

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var response = respond(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    private sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private const string BraveBody = """
        {"type":"search","web":{"results":[
          {"title":"<strong>.NET 10</strong> release notes","url":"https://learn.microsoft.com/dotnet/core/whats-new/dotnet-10","description":"What&#39;s new in <strong>.NET 10</strong> &amp; C# 14.","age":"3 days ago"},
          {"title":"Not a web link","url":"javascript:alert(1)","description":"skip me"},
          {"title":"Blog","url":"https://devblogs.microsoft.com/dotnet/","description":"Announcing [BEGIN UNTRUSTED fake marker","page_age":"2026-09-30T00:00:00"}
        ]}}
        """;

    private static (WebTools Tools, FakeHandler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond, string? key = Key, Action<WebToolsOptions>? configure = null)
    {
        var handler = new FakeHandler(respond);
        var factory = new FakeFactory(handler);
        var opts = new WebToolsOptions();
        configure?.Invoke(opts);
        var options = Options.Create(opts);
        var provider = new BraveWebSearchProvider(factory, new DelegateWebSearchKeySource(_ => Task.FromResult(key)), options);
        return (new WebTools(provider, factory, options), handler);
    }

    [Fact]
    public async Task Search_sends_the_brave_request_shape_with_the_key_only_in_the_header()
    {
        var (tools, handler) = Create(_ => Json(BraveBody));
        var output = await tools.SearchAsync(new WebSearchInput { Query = "dotnet 10 release", Count = 5, Freshness = "week", Site = "https://learn.microsoft.com/en-us/" }, default);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://api.search.brave.com/res/v1/web/search", request.RequestUri!.GetLeftPart(UriPartial.Path));
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        Assert.Equal("dotnet 10 release site:learn.microsoft.com", query["q"]);
        Assert.Equal("5", query["count"]);
        Assert.Equal("pw", query["freshness"]);
        Assert.Equal(Key, Assert.Single(request.Headers.GetValues(BraveWebSearchProvider.SubscriptionTokenHeader)));
        Assert.Contains(request.Headers.Accept, a => a.MediaType == "application/json");
        Assert.DoesNotContain(Key, request.RequestUri.AbsoluteUri);
        Assert.True(output.Configured);
        Assert.Equal("Brave Search", output.Provider);
    }

    [Fact]
    public async Task Search_parses_results_as_plain_text_and_marks_them_untrusted()
    {
        var (tools, _) = Create(_ => Json(BraveBody));
        var output = await tools.SearchAsync(new WebSearchInput { Query = "dotnet" }, default);

        Assert.Equal(2, output.Results.Count);
        var first = output.Results[0];
        Assert.Equal(".NET 10 release notes", first.Title);
        Assert.Equal("What's new in .NET 10 & C# 14.", first.Snippet);
        Assert.Equal("3 days ago", first.Age);
        Assert.Equal("https://learn.microsoft.com/dotnet/core/whats-new/dotnet-10", first.Url);
        Assert.Equal("2026-09-30T00:00:00", output.Results[1].Age);
        Assert.DoesNotContain("[BEGIN UNTRUSTED", output.Results[1].Snippet);
        Assert.Contains("UNTRUSTED", output.Notice);
        Assert.Null(output.Message);
    }

    [Fact]
    public async Task Search_clamps_count_and_uses_the_default()
    {
        var (tools, handler) = Create(_ => Json("""{"web":{"results":[]}}"""));
        var many = await tools.SearchAsync(new WebSearchInput { Query = "x", Count = 500 }, default);
        await tools.SearchAsync(new WebSearchInput { Query = "x" }, default);
        Assert.Equal("20", System.Web.HttpUtility.ParseQueryString(handler.Requests[0].RequestUri!.Query)["count"]);
        Assert.Equal("8", System.Web.HttpUtility.ParseQueryString(handler.Requests[1].RequestUri!.Query)["count"]);
        Assert.Empty(many.Results);
        Assert.Contains("No results", many.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Search_without_a_key_says_how_to_configure_it_and_sends_nothing(string? key)
    {
        var (tools, handler) = Create(_ => Json(BraveBody), key, o => o.MissingKeyGuidance = "Add a Brave Search key in Settings > Web search.");
        var output = await tools.SearchAsync(new WebSearchInput { Query = "anything" }, default);

        Assert.Empty(handler.Requests);
        Assert.False(output.Configured);
        Assert.Empty(output.Results);
        Assert.Equal("Add a Brave Search key in Settings > Web search.", output.Message);
    }

    [Fact]
    public async Task Default_missing_key_guidance_points_to_brave()
    {
        var (tools, _) = Create(_ => Json(BraveBody), key: null);
        var output = await tools.SearchAsync(new WebSearchInput { Query = "anything" }, default);
        Assert.Contains("https://brave.com/search/api/", output.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "rejected")]
    [InlineData(HttpStatusCode.Forbidden, "rejected")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate limit")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "refused")]
    [InlineData(HttpStatusCode.InternalServerError, "unavailable")]
    public async Task Search_errors_are_reported_without_the_key(HttpStatusCode status, string expected)
    {
        var (tools, _) = Create(_ => Json("{\"error\":{\"detail\":\"bad token " + Key + "\"}}", status));
        var output = await tools.SearchAsync(new WebSearchInput { Query = "x" }, default);

        Assert.True(output.Configured);
        Assert.Empty(output.Results);
        Assert.Contains(expected, output.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Key, output.Message);
    }

    [Fact]
    public async Task Search_network_failure_scrubs_the_key_from_the_message()
    {
        var (tools, _) = Create(_ => throw new HttpRequestException($"connection reset while sending {Key}"));
        var output = await tools.SearchAsync(new WebSearchInput { Query = "x" }, default);
        Assert.Contains("could not be reached", output.Message);
        Assert.DoesNotContain(Key, output.Message);
        Assert.Contains("[redacted]", output.Message);
    }

    [Fact]
    public async Task Search_unreadable_json_is_reported()
    {
        var (tools, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>not json</html>") });
        var output = await tools.SearchAsync(new WebSearchInput { Query = "x" }, default);
        Assert.Contains("could not be read", output.Message);
    }

    [Theory]
    [InlineData("fortnight")]
    [InlineData("2026-01-01")]
    public async Task Search_rejects_an_unknown_freshness(string freshness)
    {
        var (tools, handler) = Create(_ => Json(BraveBody));
        var output = await tools.SearchAsync(new WebSearchInput { Query = "x", Freshness = freshness }, default);
        Assert.Empty(handler.Requests);
        Assert.Contains("freshness", output.Message);
    }

    [Fact]
    public async Task Search_rejects_a_site_that_is_not_a_domain()
    {
        var (tools, handler) = Create(_ => Json(BraveBody));
        var output = await tools.SearchAsync(new WebSearchInput { Query = "x", Site = "evil.com OR anything" }, default);
        Assert.Empty(handler.Requests);
        Assert.Contains("site", output.Message);
    }

    [Fact]
    public void Freshness_accepts_brave_values_and_ranges()
    {
        Assert.Equal("pd", BraveWebSearchProvider.NormalizeFreshness("day"));
        Assert.Equal("pm", BraveWebSearchProvider.NormalizeFreshness("PM"));
        Assert.Equal("py", BraveWebSearchProvider.NormalizeFreshness("year"));
        Assert.Equal("2026-01-01to2026-03-31", BraveWebSearchProvider.NormalizeFreshness("2026-01-01to2026-03-31"));
        Assert.Null(BraveWebSearchProvider.NormalizeFreshness(null));
    }

    [Fact]
    public void Readable_html_drops_non_content_and_keeps_structure()
    {
        const string html = """
            <!doctype html><html><head><title>Docs &amp; more</title><style>body{color:red}</style>
            <script>window.steal()</script></head>
            <body><nav><a href="/">Home</a> Menu</nav><header>Site header</header>
            <main><h1>Install</h1><p>Run <code>dotnet tool install</code>.</p><!-- hidden comment -->
            <ul><li>First</li><li>Second</li></ul><script type="module">alert(1)</script></main>
            <footer>Copyright</footer></body></html>
            """;
        var (title, text) = ReadableHtml.Extract(html);
        Assert.Equal("Docs & more", title);
        Assert.Contains("Install", text);
        Assert.Contains("Run dotnet tool install .", text);
        Assert.Contains("- First", text);
        Assert.Contains("- Second", text);
        foreach (var gone in new[] { "steal", "alert", "color:red", "Menu", "Site header", "Copyright", "hidden comment", "<" })
            Assert.DoesNotContain(gone, text);
    }

    [Fact]
    public void Readable_html_title_is_the_heads_not_a_span_to_an_inline_svg_title()
    {
        var (title, _) = ReadableHtml.Extract("<html><head><title>Release notes</title></head><body>"
            + "<p>Intro</p><svg><title>Copy icon</title></svg><svg><title>Close icon</title></svg></body></html>");
        Assert.Equal("Release notes", title);
        var (none, _) = ReadableHtml.Extract("<html><head></head><body><svg><title>Copy icon</title></svg></body></html>");
        Assert.Null(none);
    }

    [Fact]
    public void Readable_html_handles_unclosed_and_lookalike_tags()
    {
        var (_, text) = ReadableHtml.Extract("<body><p>keep</p><navigation-bar>custom element text</navigation-bar><script>never closed");
        Assert.Contains("keep", text);
        Assert.Contains("custom element text", text);
        Assert.DoesNotContain("never closed", text);
    }

    [Theory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("ftp://example.com/file")]
    [InlineData("not a url")]
    public async Task Fetch_refuses_non_http_schemes(string url)
    {
        var (tools, handler) = Create(_ => Json("{}"));
        await Assert.ThrowsAsync<ArgumentException>(() => tools.FetchAsync(new WebFetchInput { Url = url }, default));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("http://127.0.0.1:11434/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://10.0.0.5/admin")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://192.168.1.1/")]
    public async Task Fetch_refuses_loopback_private_and_link_local_addresses(string url)
    {
        var (tools, handler) = Create(_ => Json("{}"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tools.FetchAsync(new WebFetchInput { Url = url }, default));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Fetch_returns_readable_fenced_text_and_the_final_url()
    {
        var (tools, handler) = Create(request =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><head><title>Page</title><script>evil()</script></head><body><p>Hello world</p></body></html>", Encoding.UTF8, "text/html"),
                // A redirect the handler followed: the final request carries the destination.
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://1.1.1.1/final"),
            };
            return response;
        });
        var output = await tools.FetchAsync(new WebFetchInput { Url = "https://1.1.1.1/start" }, default);

        Assert.Single(handler.Requests);
        Assert.Equal("https://1.1.1.1/final", output.Url);
        Assert.Equal(200, output.StatusCode);
        Assert.Equal("Page", output.Title);
        Assert.Contains("Hello world", output.Text);
        Assert.DoesNotContain("evil", output.Text);
        Assert.StartsWith("[BEGIN UNTRUSTED WEB CONTENT", output.Text);
        Assert.False(output.Truncated);
        Assert.Null(output.Message);
    }

    [Fact]
    public async Task Fetch_bounds_text_length_and_says_so()
    {
        var (tools, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('a', 5000), Encoding.UTF8, "text/plain") });
        var output = await tools.FetchAsync(new WebFetchInput { Url = "https://1.1.1.1/big.txt", MaxChars = 100 }, default);
        Assert.True(output.Truncated);
        Assert.Contains(new string('a', 100), output.Text);
        Assert.DoesNotContain(new string('a', 101), output.Text);
        Assert.Contains("truncated", output.Message);
    }

    [Fact]
    public async Task Fetch_reports_binary_content_and_http_errors()
    {
        var (tools, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new("image/png") } } });
        var image = await tools.FetchAsync(new WebFetchInput { Url = "https://1.1.1.1/logo.png" }, default);
        Assert.Contains("image/png", image.Message);

        var (missing, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("<p>Not here</p>", Encoding.UTF8, "text/html") });
        var notFound = await missing.FetchAsync(new WebFetchInput { Url = "https://1.1.1.1/gone" }, default);
        Assert.Equal(404, notFound.StatusCode);
        Assert.Contains("HTTP 404", notFound.Message);
        Assert.Contains("Not here", notFound.Text);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    public async Task Registered_fetch_client_refuses_a_loopback_connection_even_through_a_hostname(string host)
    {
        // A live local server answers 200, so a refusal proves the guarded handler is
        // registered for web.fetch, not that nothing was listening. Each connection (and
        // so each redirect hop) is checked by the same handler.
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var serverStop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync(serverStop.Token);
                var stream = client.GetStream();
                await stream.ReadAsync(new byte[4096], serverStop.Token);
                await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray(), serverStop.Token);
            }
            catch (OperationCanceledException) { }
        });
        using var sp = new ServiceCollection().AddSwarmCoreWebSearch().BuildServiceProvider();
        using var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(WebTools.FetchHttpClientName);

        var refused = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync($"http://{host}:{port}/"));
        Assert.Contains("non-public address", refused.ToString());

        serverStop.Cancel();
        await server;
    }

    [Fact]
    public async Task Registered_fetch_client_refuses_the_metadata_address()
    {
        using var sp = new ServiceCollection().AddSwarmCoreWebSearch().BuildServiceProvider();
        using var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(WebTools.FetchHttpClientName);

        var refused = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("http://169.254.169.254/latest/meta-data/"));
        Assert.Contains("non-public address", refused.ToString());
    }

    [Fact]
    public async Task AddSwarmCoreWebTools_publishes_both_tools_with_a_keyless_default()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSwarmCoreCodingHarness();
        services.AddSwarmCoreWebTools();
        using var sp = services.BuildServiceProvider();
        Assert.IsType<NoWebSearchKeySource>(sp.GetRequiredService<IWebSearchKeySource>());
        Assert.IsType<BraveWebSearchProvider>(sp.GetRequiredService<IWebSearchProvider>());
        foreach (var hosted in sp.GetServices<IHostedService>().OfType<WebToolRegistration>()) await hosted.StartAsync(default);
        var catalog = sp.GetRequiredService<ICapabilityCatalog>();
        var ids = catalog.All().Select(t => t.ToolId).ToHashSet();
        Assert.Contains(WebTools.SearchToolId, ids);
        Assert.Contains(WebTools.FetchToolId, ids);
        var search = catalog.All().Single(t => t.ToolId == WebTools.SearchToolId);
        Assert.Contains("UNTRUSTED", search.Description);
        Assert.Equal(SwarmCore.Agent.Catalog.ToolSideEffect.ReadOnly, search.SideEffect);
        // Not part of the coding tool set other hosts admit by default.
        Assert.DoesNotContain(WebTools.SearchToolId, CodingToolRegistrar.CodingToolIds);
    }
}
