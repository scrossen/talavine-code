using SwarmCore.Abstractions.Computer;

namespace SwarmCore.Coding.Computer;

/// <summary>Host-owned computer operations. Native workers proxy this contract rather than owning browser processes.</summary>
public interface ICodingComputer
{
    Task<IReadOnlyList<BrowserTabInfo>> BrowserTabsAsync(CancellationToken ct = default);
    Task<BrowserTabInfo> BrowserOpenAsync(string url, CancellationToken ct = default);
    Task<BrowserObservation> BrowserObserveAsync(string tabId, CancellationToken ct = default);
    Task<ComputerActionResult> BrowserActAsync(ComputerActionBatch batch, CancellationToken ct = default);
    Task<IReadOnlyList<DesktopWindowInfo>> WindowsAsync(CancellationToken ct = default);
    Task<ComputerObservation> DesktopObserveAsync(string windowId, CancellationToken ct = default);
    Task<ComputerActionResult> DesktopActAsync(ComputerActionBatch batch, CancellationToken ct = default);
}

public sealed class LocalCodingComputer(ChromeComputerSession browser, WindowsComputerSession desktop) : ICodingComputer
{
    public Task<IReadOnlyList<BrowserTabInfo>> BrowserTabsAsync(CancellationToken ct = default) => browser.ListAsync(ct);
    public Task<BrowserTabInfo> BrowserOpenAsync(string url, CancellationToken ct = default) => browser.OpenAsync(url, ct);
    public Task<BrowserObservation> BrowserObserveAsync(string tabId, CancellationToken ct = default) => browser.ObserveAsync(tabId, ct);
    public Task<ComputerActionResult> BrowserActAsync(ComputerActionBatch batch, CancellationToken ct = default) => browser.ActAsync(batch, ct);
    public Task<IReadOnlyList<DesktopWindowInfo>> WindowsAsync(CancellationToken ct = default) => desktop.ListAsync(ct);
    public Task<ComputerObservation> DesktopObserveAsync(string windowId, CancellationToken ct = default) => desktop.ObserveAsync(windowId, ct);
    public Task<ComputerActionResult> DesktopActAsync(ComputerActionBatch batch, CancellationToken ct = default) => desktop.ExecuteBatchAsync(batch, ct);
}

/// <summary>
/// A computer operation failed for a reason the session states in its own words (no page, window or credential
/// text). The host broker passes this message to the agent; any other failure is reported generically.
/// </summary>
public sealed class ComputerOperationException(string message) : InvalidOperationException(message);
