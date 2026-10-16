using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using SwarmCore.Abstractions.Computer;
using SwarmCore.Missions;

namespace SwarmCore.Coding.Computer;

public sealed record BrowserTabInfo(string Id, string Title, string Url);
public sealed record BrowserObservation(ComputerObservation Screenshot, string VisibleText)
    : SwarmCore.Abstractions.LlmRouting.IToolResultImages
{
    IReadOnlyList<SwarmCore.Abstractions.LlmRouting.ImagePart> SwarmCore.Abstractions.LlmRouting.IToolResultImages.Images
        => ((SwarmCore.Abstractions.LlmRouting.IToolResultImages)Screenshot).Images;
}

/// <summary>Connects to regular signed-in Chrome by default, with explicit CDP and managed-profile alternatives.</summary>
public sealed class ChromeComputerSession(IOptions<CodingComputerOptions> options) : IAsyncDisposable, IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, (IPage Page, PlaywrightComputerSurface Surface)> tabs = new();
    private IPlaywright? playwright;
    private IBrowser? attachedBrowser;
    private IBrowserContext? context;
    private volatile bool disconnected;
    private int disposed;

    public static void ValidateEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || !uri.IsLoopback
            || uri.Scheme is not ("http" or "https" or "ws" or "wss"))
            throw new ArgumentException("Chrome CDP endpoint must be a loopback HTTP or WebSocket address.");
    }

    private async Task EnsureAsync(CancellationToken ct)
    {
        if (!options.Value.Enabled) throw new InvalidOperationException("Computer access is disabled in Settings.");
        if (context is not null && !disconnected) return;
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (context is not null && !disconnected) return;
            context = null;
            tabs.Clear();
            disconnected = false;
            var o = options.Value;
            playwright ??= await Playwright.CreateAsync().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(o.ChromeCdpEndpoint))
            {
                ValidateEndpoint(o.ChromeCdpEndpoint);
                attachedBrowser = await playwright.Chromium.ConnectOverCDPAsync(o.ChromeCdpEndpoint,
                    new() { Timeout = 15_000 }).ConfigureAwait(false);
                context = attachedBrowser.Contexts.FirstOrDefault() ?? throw new InvalidOperationException("Chrome has no browser context.");
            }
            else if (o.UsePersonalChrome)
            {
                var endpoint = await Task.Run(() => PersonalChromeConnection.GetEndpoint(o.PersonalChromeUserDataDirectory), ct).ConfigureAwait(false);
                try
                {
                    attachedBrowser = await playwright.Chromium.ConnectOverCDPAsync(endpoint,
                        new() { Timeout = 60_000 }).WaitAsync(ct).ConfigureAwait(false);
                    context = attachedBrowser.Contexts.FirstOrDefault() ?? throw new InvalidOperationException("Chrome has no browser context.");
                }
                catch (Microsoft.Playwright.PlaywrightException ex)
                {
                    throw new InvalidOperationException("Could not connect to your Chrome. Keep Chrome open, enable remote debugging at " +
                        PersonalChromeConnection.SetupUrl + ", then retry and allow the connection prompt in Chrome.", ex);
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(o.BrowserProfileDirectory))
                    throw new InvalidOperationException("The host must configure a Chrome profile directory.");
                var endpoint = await Task.Run(() => SharedChromeProcess.GetEndpoint(o, ct), ct).ConfigureAwait(false);
                attachedBrowser = await playwright.Chromium.ConnectOverCDPAsync(endpoint, new() { Timeout = 15_000 }).ConfigureAwait(false);
                context = attachedBrowser.Contexts.FirstOrDefault() ?? throw new InvalidOperationException("Chrome has no browser context.");
            }
            context.SetDefaultTimeout(15_000);
            var connectedContext = context;
            var connectedBrowser = attachedBrowser;
            context.Close += (_, _) => { if (ReferenceEquals(context, connectedContext)) disconnected = true; };
            attachedBrowser!.Disconnected += (_, _) => { if (ReferenceEquals(attachedBrowser, connectedBrowser)) disconnected = true; };
        }
        finally { gate.Release(); }
    }

    private string Track(IPage page)
    {
        var existing = tabs.FirstOrDefault(kv => ReferenceEquals(kv.Value.Page, page));
        if (existing.Key is not null) return existing.Key;
        var id = Guid.NewGuid().ToString("N");
        tabs[id] = (page, new PlaywrightComputerSurface(page, ct => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }));
        return id;
    }

    public async Task<IReadOnlyList<BrowserTabInfo>> ListAsync(CancellationToken ct = default)
    {
        await EnsureAsync(ct).ConfigureAwait(false);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var id in tabs.Where(kv => kv.Value.Page.IsClosed).Select(kv => kv.Key).ToArray()) tabs.Remove(id);
            var result = new List<BrowserTabInfo>();
            foreach (var page in context!.Pages)
                result.Add(new(Track(page), await PlaywrightComputerSurface.RunBoundedAsync(page, page.TitleAsync(), TimeSpan.FromSeconds(15), ct).ConfigureAwait(false), PlaywrightComputerSurface.SafeUrl(page.Url)));
            return result;
        }
        finally { gate.Release(); }
    }

    public async Task<BrowserTabInfo> OpenAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("Use an absolute HTTP or HTTPS URL.");
        await EnsureAsync(ct).ConfigureAwait(false);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var page = await context!.NewPageAsync().ConfigureAwait(false);
            var id = Track(page);
            await PlaywrightComputerSurface.RunBoundedAsync(page, page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30_000 }), TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            return new(id, await PlaywrightComputerSurface.RunBoundedAsync(page, page.TitleAsync(), TimeSpan.FromSeconds(15), ct).ConfigureAwait(false), PlaywrightComputerSurface.SafeUrl(page.Url));
        }
        finally { gate.Release(); }
    }

    public async Task<BrowserObservation> ObserveAsync(string tabId, CancellationToken ct = default)
    {
        await EnsureAsync(ct).ConfigureAwait(false);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!tabs.TryGetValue(tabId, out var tab) || tab.Page.IsClosed) throw new ArgumentException("Tab is closed or unknown; call browser.tabs first.");
            var screenshot = await tab.Surface.ObserveAsync(ct).ConfigureAwait(false);
            // Text is website evidence, not authority. Input values are never included.
            var text = await PlaywrightComputerSurface.RunBoundedAsync(tab.Page, tab.Page.EvaluateAsync<string>("""
                () => {
                  function read(node) {
                    if (node.nodeType === Node.TEXT_NODE) return node.textContent || '';
                    if (!(node instanceof Element) || node.matches('[data-private],input,textarea,script,style,noscript')) return '';
                    const style = getComputedStyle(node);
                    if (style.display === 'none' || style.visibility === 'hidden' || style.opacity === '0' || node.hidden || node.inert) return '';
                    const value = Array.from(node.childNodes, read).join('');
                    return /^(block|flex|grid|table|list-item)/.test(style.display) ? value + '\n' : value;
                  }
                  return read(document.body).slice(0, 24001);
                }
                """), TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            if (await tab.Surface.InspectLastObservationAsync(ct).ConfigureAwait(false) is null)
                throw new InvalidOperationException("The page changed while it was being observed; observe again.");
            return new(screenshot, text.Length <= 24_000 ? text : text[..24_000] + "\n[Text truncated]");
        }
        finally { gate.Release(); }
    }

    public async Task<ComputerActionResult> ActAsync(ComputerActionBatch batch, CancellationToken ct = default)
    {
        await EnsureAsync(ct).ConfigureAwait(false);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tab = tabs.Values.FirstOrDefault(t => t.Surface.LastObservation?.SessionId == batch.SessionId);
            if (tab.Surface is null) return new(false, 0, null, "Unknown browser session; observe an existing tab first.");
            return await tab.Surface.ExecuteBatchAsync(batch, ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public void Dispose()
    {
        // Connected Chrome outlives individual clients and workspace reloads; only disconnect here.
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        playwright?.Dispose();
        gate.Dispose();
    }

    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}
