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
    private readonly List<ChromeTabConnection> tabConnections = new();
    private IPlaywright? playwright;
    private ChromeDevToolsConnection? connection;
    private int disposed;

    public static void ValidateEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || !uri.IsLoopback
            || uri.Scheme is not ("http" or "https" or "ws" or "wss"))
            throw new ArgumentException("Chrome CDP endpoint must be a loopback HTTP or WebSocket address.");
    }

    // Called under gate. Browser metadata is independent of every page's renderer health.
    private async Task EnsureAsync(CancellationToken ct)
    {
        if (!options.Value.Enabled) throw new InvalidOperationException("Computer access is disabled in Settings.");
        if (connection?.Connected == true) return;
        foreach (var tab in tabConnections) tab.Dispose();
        tabConnections.Clear();
        tabs.Clear();
        connection?.Dispose();
        connection = new();
        var o = options.Value;
        string endpoint;
        if (!string.IsNullOrWhiteSpace(o.ChromeCdpEndpoint)) endpoint = o.ChromeCdpEndpoint;
        else if (o.UsePersonalChrome)
            endpoint = await Task.Run(() => PersonalChromeConnection.GetEndpoint(o.PersonalChromeUserDataDirectory), ct).ConfigureAwait(false);
        else
        {
            if (string.IsNullOrWhiteSpace(o.BrowserProfileDirectory))
                throw new InvalidOperationException("The host must configure a Chrome profile directory.");
            endpoint = await Task.Run(() => SharedChromeProcess.GetEndpoint(o, ct), ct).ConfigureAwait(false);
        }
        try { await connection.ConnectAsync(endpoint, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            connection.Dispose();
            throw new IOException("Could not connect to your Chrome. Keep Chrome open, enable remote debugging at " +
                PersonalChromeConnection.SetupUrl + ", then retry and allow the connection prompt in Chrome.", ex);
        }
    }

    private async Task<IReadOnlyList<BrowserTabInfo>> ReadTabsAsync(CancellationToken ct)
    {
        var result = await connection!.SendAsync("Target.getTargets", null, null, ct).ConfigureAwait(false);
        return result.GetProperty("targetInfos").EnumerateArray()
            .Where(t => t.GetProperty("type").GetString() == "page")
            .Select(t => new BrowserTabInfo(t.GetProperty("targetId").GetString()!,
                t.GetProperty("title").GetString() ?? "", PlaywrightComputerSurface.SafeUrl(t.GetProperty("url").GetString() ?? "")))
            .ToArray();
    }

    private async Task<(IPage Page, PlaywrightComputerSurface Surface)> AttachAsync(string tabId, CancellationToken ct)
    {
        if (tabs.TryGetValue(tabId, out var existing) && !existing.Page.IsClosed) return existing;
        if (!(await ReadTabsAsync(ct).ConfigureAwait(false)).Any(t => t.Id == tabId))
            throw new ArgumentException("Tab is closed or unknown; call browser.tabs first.");
        playwright ??= await Playwright.CreateAsync().ConfigureAwait(false);
        var proxy = new ChromeTabConnection(connection!, tabId);
        try
        {
            var browser = await playwright.Chromium.ConnectOverCDPAsync(proxy.Endpoint, new() { Timeout = 15_000 })
                .WaitAsync(ct).ConfigureAwait(false);
            var page = browser.Contexts.SelectMany(c => c.Pages).Single();
            page.SetDefaultTimeout(15_000);
            var entry = (page, new PlaywrightComputerSurface(page, token => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }));
            tabs[tabId] = entry;
            tabConnections.Add(proxy);
            return entry;
        }
        catch { proxy.Dispose(); throw; }
    }

    public async Task<IReadOnlyList<BrowserTabInfo>> ListAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { await EnsureAsync(ct).ConfigureAwait(false); return await ReadTabsAsync(ct).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task<BrowserTabInfo> OpenAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("Use an absolute HTTP or HTTPS URL.");
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureAsync(ct).ConfigureAwait(false);
            var created = await connection!.SendAsync("Target.createTarget", new { url = "about:blank" }, null, ct).ConfigureAwait(false);
            var id = created.GetProperty("targetId").GetString()!;
            var tab = await AttachAsync(id, ct).ConfigureAwait(false);
            await PlaywrightComputerSurface.RunBoundedAsync(tab.Page,
                tab.Page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30_000 }),
                TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            return new(id, await PlaywrightComputerSurface.RunBoundedAsync(tab.Page, tab.Page.TitleAsync(), TimeSpan.FromSeconds(15), ct).ConfigureAwait(false),
                PlaywrightComputerSurface.SafeUrl(tab.Page.Url));
        }
        finally { gate.Release(); }
    }

    public async Task<BrowserObservation> ObserveAsync(string tabId, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureAsync(ct).ConfigureAwait(false);
            var tab = await AttachAsync(tabId, ct).ConfigureAwait(false);
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
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureAsync(ct).ConfigureAwait(false);
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
        foreach (var tab in tabConnections) tab.Dispose();
        connection?.Dispose();
        playwright?.Dispose();
    }

    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}
