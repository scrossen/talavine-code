using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Continual;
using SwarmCore.Agent.Runtime;
using SwarmCore.Abstractions.Computer;
using SwarmCore.Abstractions.Packaging;

namespace SwarmCore.Missions;

public sealed record MissionBrowserInput(string Action, string? Url = null, string? Selector = null, string? Value = null, string? PageToken = null);

/// <summary>Automatic, no-caller-digest outcome proof attached to a mutating <c>missions.browser</c>
/// receipt at dispatch time via <see cref="IMissionStore.AttachRecoveryProofAsync"/> (see
/// <see cref="MissionBrowserService"/>). Captured only when the host has explicitly mapped the
/// dispatched route to a logical read-only resource (<see cref="MissionBrowserRoute.ResourceUrl"/>);
/// a lost tool result can then reconcile from the mission's own recorded proof in
/// <c>MissionTurnExecutor.InspectExternalOutcomeAsync</c> without any trusted caller supplying a
/// digest through <see cref="MissionOutcomeInspection"/>/<c>MissionService.BindOutcomeInspectionAsync</c>.</summary>
public sealed record MissionBrowserOutcomeProof(string InspectUrl, string ExpectedBodySha256);
public sealed record MissionBrowserResult(bool Succeeded, string? Url, string? Title, string? Text, string? Error, string? PageToken = null)
    : IToolExecutionEvidence, SwarmCore.Abstractions.LlmRouting.IToolResultImages, SwarmCore.Abstractions.Context.IContextPackEvidence
{
    public bool LocalProcessCompleted => false;
    public ComputerObservation? Observation { get; init; }
    IReadOnlyList<SwarmCore.Abstractions.LlmRouting.ImagePart> SwarmCore.Abstractions.LlmRouting.IToolResultImages.Images
        => Observation is SwarmCore.Abstractions.LlmRouting.IToolResultImages images ? images.Images : [];
    SwarmCore.Abstractions.Context.ContextPack? SwarmCore.Abstractions.Context.IContextPackEvidence.WorkingContextSourcePack => null;
}

/// <summary>Mission/profile-isolated Chromium sessions. Browser sockets go to an unavailable
/// proxy; only the host's per-request authority-checked HTTP bridge can reach a server.</summary>
public sealed partial class MissionBrowserService(MissionBrowserOptions browserOptions, IOptions<MissionNetworkOptions> networkOptions,
    IMissionStore? missionStore = null, OptionalHostCapabilityPolicy? hostCapabilities = null) : IAsyncDisposable, IComputerSession
{
    private readonly ConcurrentDictionary<(Guid Profile, Guid Mission), Session> _sessions = new();
    private readonly OptionalHostCapabilityPolicy _hostCapabilities = hostCapabilities ?? OptionalHostCapabilityPolicy.Unrestricted;
    public async Task<ComputerObservation> ObserveAsync(string? url = null, CancellationToken ct = default)
    {
        _hostCapabilities.Require(OptionalHostCapability.ManagedBrowser);
        var turn = CurrentComputerTurn();
        await turn.ValidateMissionExecution!(ct);
        var session = _sessions.GetOrAdd((turn.ProfileId!.Value, turn.MissionObjectiveId!.Value), _ => new Session());
        await session.Gate.WaitAsync(ct);
        try
        {
            session.Active = turn; session.Cancellation = ct; session.Error = null; session.ReadOnly = true;
            await EnsureSessionAsync(session, turn.ProfileId.Value, turn.MissionObjectiveId.Value, url is not null);
            if (url is not null)
            {
                await AuthorizeAsync(turn, url, "GET", ct, true);
                await PlaywrightComputerSurface.RunBoundedAsync(session.Page!, session.Page!.GotoAsync(url,
                    new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = (float)browserOptions.ActionTimeout.TotalMilliseconds }), browserOptions.ActionTimeout, ct);
            }
            await AuthorizeAsync(turn, session.Page!.Url, "GET", ct, true);
            var result = await Computer(session).ObserveAsync(ct);
            session.ObservedAuthorityRevision = turn.MissionResources!.Revision;
            return result with { SemanticPageToken = await PageTokenAsync(session.Page!, ct) };
        }
        finally { session.Active = null; session.Gate.Release(); }
    }
    public async Task<ComputerActionResult> ExecuteBatchAsync(ComputerActionBatch batch, CancellationToken ct = default)
    {
        _hostCapabilities.Require(OptionalHostCapability.ManagedBrowser);
        var turn = CurrentComputerTurn();
        await turn.ValidateMissionExecution!(ct);
        if (!_sessions.TryGetValue((turn.ProfileId!.Value, turn.MissionObjectiveId!.Value), out var session))
            return new(false, 0, null, "session-unavailable");
        await session.Gate.WaitAsync(ct);
        try
        {
            session.Active = turn; session.Cancellation = ct; session.Error = null;
            session.ReadOnly = batch.Actions.All(action => action.Type is "screenshot" or "wait");
            if (session.Page is null || session.Page.IsClosed) return new(false, 0, null, "surface-closed");
            var result = await Computer(session).ExecuteBatchAsync(batch, ct);
            session.ObservedAuthorityRevision = turn.MissionResources!.Revision;
            return session.Error is null ? result : result with { Succeeded = false, Error = "browser-request-blocked", OutcomeUncertain = result.CompletedActions > 0 };
        }
        finally { session.Active = null; session.Gate.Release(); }
    }
    public async Task<ComputerObservation?> InspectObservationAsync(Guid profile, Guid mission, CancellationToken ct = default)
    {
        _hostCapabilities.Require(OptionalHostCapability.ManagedBrowser);
        if (missionStore is null || !_sessions.TryGetValue((profile, mission), out var session)) return null;
        await session.Gate.WaitAsync(ct);
        try
        {
            if (!await IsCurrentAsync()) return null;
            var observation = session.Computer is { } computer ? await computer.InspectLastObservationAsync(ct) : null;
            // Inspection may await page work. Revocation or ancestor steering during that
            // wait must remove the pixels without manufacturing a new execution lease.
            return observation is not null && await IsCurrentAsync() ? observation : null;

            async Task<bool> IsCurrentAsync()
            {
                var saved = await missionStore.GetAsync(mission, ct);
                if (saved is null || saved.PartitionKey != profile.ToString("D")
                    || saved.Status is ContinualObjectiveStatus.Cancelled or ContinualObjectiveStatus.Paused
                    || saved.Authority.MissionResources?.Revision != session.ObservedAuthorityRevision) return false;
                var ancestor = saved;
                var visited = new HashSet<Guid>();
                while (ancestor.Mission?.Delegation is { } delegation)
                {
                    if (!visited.Add(ancestor.Id) || visited.Count > 64 || ancestor.Mission.ParentId is not { } parentId) return false;
                    var parent = await missionStore.GetAsync(parentId, ct);
                    if (parent is null || parent.PartitionKey != saved.PartitionKey
                        || parent.Status is ContinualObjectiveStatus.Paused or ContinualObjectiveStatus.Cancelled
                        || parent.Authority.MissionResources?.Revision != delegation.ParentAuthorityRevision
                        || parent.Mission?.ConstraintVersion != delegation.ParentConstraintVersion) return false;
                    ancestor = parent;
                }
                return true;
            }
        }
        finally { session.Gate.Release(); }
    }
    private static CurrentTurnContext CurrentComputerTurn()
        => AsyncLocalCurrentTurnContextAccessor.Ambient is { MissionObjectiveId: not null, ProfileId: not null,
            MissionResources: not null, ValidateMissionExecution: not null } turn ? turn
            : throw new MissionActionSkippedException("A current mission and durable execution fence are required.");
    private PlaywrightComputerSurface Computer(Session session) => session.Computer ??= new(session.Page!, async ct =>
    {
        var turn = session.Active ?? throw new MissionActionSkippedException("No active browser authority.");
        await AuthorizeAsync(turn, session.Page!.Url, "GET", ct);
    }, browserOptions.ActionTimeout);
    public async Task<MissionBrowserResult> ExecuteAsync(MissionBrowserInput input, CancellationToken ct)
    {
        _hostCapabilities.Require(OptionalHostCapability.ManagedBrowser);
        if (input.Action == "screenshot")
        {
            var observation = await ObserveAsync(input.Url, ct);
            return new(true, observation.Url, null, "Screenshot is current untrusted browser evidence.", null, observation.SemanticPageToken)
            { Observation = observation };
        }
        var turn = AsyncLocalCurrentTurnContextAccessor.Ambient;
        if (turn is not { MissionObjectiveId: { } id, ProfileId: { } profile, MissionResources: not null, ValidateMissionExecution: not null })
            throw new MissionActionSkippedException("A current mission and durable execution fence are required.");
        await turn.ValidateMissionExecution(ct);
        var session = _sessions.GetOrAdd((profile, id), _ => new Session());
        await session.Gate.WaitAsync(ct);
        try
        {
            session.Active = turn;
            session.Cancellation = ct;
            session.Error = null;
            session.ReadOnly = input.Action is "navigate" or "snapshot";
            await EnsureSessionAsync(session, profile, id, input.Action == "navigate");
            var page = session.Page!;
            if (input.Action is "click" or "fill")
            {
                if (input.PageToken is null || input.PageToken != await PageTokenAsync(page, ct))
                    throw new MissionActionSkippedException("The page changed or no page token was supplied. Take a snapshot and use its pageToken.");
                if (input.Url is not null && input.Url != page.Url)
                    throw new MissionActionSkippedException("The current page does not match the action target.");
            }
            switch (input.Action)
            {
                case "navigate":
                    await AuthorizeAsync(turn, input.Url!, "GET", ct);
                    await PlaywrightComputerSurface.RunBoundedAsync(page, page.GotoAsync(input.Url!, new()
                        { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = (float)browserOptions.ActionTimeout.TotalMilliseconds }), browserOptions.ActionTimeout, ct);
                    break;
                case "snapshot":
                    await AuthorizeAsync(turn, page.Url, "GET", ct);
                    break;
                case "click":
                    await AuthorizeAsync(turn, page.Url, "GET", ct);
                    await PlaywrightComputerSurface.RunBoundedAsync(page, page.Locator(input.Selector!).ClickAsync(new()
                        { Timeout = (float)browserOptions.ActionTimeout.TotalMilliseconds }), browserOptions.ActionTimeout, ct);
                    break;
                case "fill":
                    await AuthorizeAsync(turn, page.Url, "GET", ct);
                    var target = page.Locator(input.Selector!);
                    await PlaywrightComputerSurface.RejectCredentialTargetAsync(page, target, browserOptions.ActionTimeout, ct);
                    ct.ThrowIfCancellationRequested();
                    await PlaywrightComputerSurface.RunBoundedAsync(page, target.FillAsync(input.Value ?? "", new()
                        { Timeout = (float)browserOptions.ActionTimeout.TotalMilliseconds }), browserOptions.ActionTimeout, ct);
                    break;
                default: throw new ArgumentException("Action must be navigate, snapshot, click, or fill.");
            }
            if (input.Action is "click" or "fill" && session.Error is null && session.PendingAutomaticResourceUrl is { } autoResourceUrl)
            {
                session.PendingAutomaticResourceUrl = null;
                if (ToolProgressContext.CurrentToolCallId is { } toolCallId)
                {
                    // Best-effort: a lost result can still fall back to trusted manual binding
                    // (MissionService.BindOutcomeInspectionAsync) when this cannot be captured.
                    try { await RecordAutomaticOutcomeInspectionAsync(turn, session, autoResourceUrl, toolCallId, ct); }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or MissionActionSkippedException) { }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                }
            }
            await turn.ValidateMissionExecution(ct);
            var snapshot = await CaptureSemanticAsync(page, ct);
            return new(session.Error is null, PlaywrightComputerSurface.SafeUrl(snapshot.Url), snapshot.Title,
                snapshot.Text[..Math.Min(12000, snapshot.Text.Length)] + "\nControls: " + snapshot.Controls,
                session.Error is null ? null : "browser-request-blocked", snapshot.Token);
        }
        catch (Exception ex) when (ex is PlaywrightException or HttpRequestException)
        {
            var failedUrl = session.Page is { } failedPage ? PlaywrightComputerSurface.SafeUrl(failedPage.Url) : null;
            // A failed navigation can still be transitioning to Chromium's error document.
            // Settle that surface; only a subsequent explicit navigation creates a new page.
            if (input.Action == "navigate" && session.Page is { IsClosed: false } failedNavigation)
                await failedNavigation.CloseAsync();
            return new(false, failedUrl, null, null, "browser-action-failed");
        }
        finally { session.Active = null; session.Gate.Release(); }
    }

    private async Task EnsureSessionAsync(Session session, Guid profile, Guid id, bool allowRecovery)
    {
        if (allowRecovery && session.Context is not null && session.Page is { IsClosed: true })
        {
            // Closing the sole page can terminate a persistent Chromium context before
            // its disconnect event arrives. Reopen the isolated profile only for a new
            // explicit navigation; never replay the failed action or reuse its surface.
            await session.Context.CloseAsync();
            session.Playwright?.Dispose();
            session.Context = null;
            session.Playwright = null;
            session.Page = null;
            session.Computer = null;
            session.ObservedAuthorityRevision = null;
        }
        if (session.Context is null)
            {
                if (!Path.IsPathFullyQualified(browserOptions.UserDataDirectory))
                    throw new InvalidOperationException("The host must configure an absolute mission browser profile directory.");
                session.Playwright = await Playwright.CreateAsync();
                var directory = Path.Combine(browserOptions.UserDataDirectory, "missions", profile.ToString("N"), id.ToString("N"));
                Directory.CreateDirectory(directory);
                session.Context = await session.Playwright.Chromium.LaunchPersistentContextAsync(directory, new()
                {
                    Channel = browserOptions.BrowserChannel, Headless = browserOptions.Headless, ViewportSize = new() { Width = browserOptions.ViewportWidth, Height = browserOptions.ViewportHeight }, DeviceScaleFactor = browserOptions.DeviceScaleFactor, ServiceWorkers = ServiceWorkerPolicy.Block,
                    AcceptDownloads = false, Proxy = new() { Server = "http://127.0.0.1:9" },
                    Args = ["--proxy-bypass-list=<-loopback>", "--disable-quic", "--force-webrtc-ip-handling-policy=disable_non_proxied_udp", "--disable-background-networking"]
                });
                await session.Context.RouteAsync("**/*", route => BridgeAsync(session, route));
                await session.Context.RouteWebSocketAsync("**/*", socket => socket.CloseAsync());
                session.Page = session.Context.Pages.FirstOrDefault() ?? await session.Context.NewPageAsync();
            }
        if (allowRecovery && (session.Page is null || session.Page.IsClosed))
        {
            session.Page = await session.Context.NewPageAsync();
            session.Computer = null;
            session.ObservedAuthorityRevision = null;
        }
    }

    private async Task<string> PageTokenAsync(IPage page, CancellationToken ct)
        => (await CaptureSemanticAsync(page, ct)).Token;

    private sealed record SemanticSnapshot(string Url, string Title, string Text, string Controls, string Token);
    private async Task<SemanticSnapshot> CaptureSemanticAsync(IPage page, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var before = await PlaywrightComputerSurface.CaptureLayoutFingerprintAsync(page, ct, browserOptions.ActionTimeout);
                var data = await PlaywrightComputerSurface.RunBoundedAsync(page, page.EvaluateAsync<string[]>("""
                    () => [location.href, document.title, document.body?.innerText || '',
                      JSON.stringify([...document.querySelectorAll('input:not([type=password]),button,a,select,textarea')].slice(0,80)
                        .map(e=>({tag:e.tagName,id:e.id,name:e.getAttribute('name'),text:(e.innerText||'').slice(0,120)})))]
                    """), browserOptions.ActionTimeout, ct);
                var after = await PlaywrightComputerSurface.CaptureLayoutFingerprintAsync(page, ct, browserOptions.ActionTimeout);
                if (before != after) throw new PlaywrightException("semantic-layout-changed");
                var token = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(before + "\n" + data[2])));
                return new(data[0], data[1], data[2], data[3], token);
            }
            catch (PlaywrightException ex) when (attempt < 2 && !page.IsClosed &&
                (ex.Message.Contains("Execution context", StringComparison.OrdinalIgnoreCase)
                 || ex.Message.Contains("semantic-layout-changed", StringComparison.Ordinal)))
            {
                // Retry observation only, never the preceding click, fill, or navigation.
                await PlaywrightComputerSurface.RunBoundedAsync(page,
                    page.WaitForLoadStateAsync(LoadState.DOMContentLoaded), browserOptions.ActionTimeout, ct);
            }
        }
    }

    /// <summary>Captures a no-caller-digest outcome proof for the click/fill that just dispatched a
    /// host-mapped mutating request (<see cref="MissionBrowserRoute.ResourceUrl"/>), and durably attaches
    /// it to the still-<c>Started</c> receipt via the lease token — independent of whether the tool's own
    /// return value later reaches the caller. See <see cref="MissionBrowserOutcomeProof"/>.</summary>
    private async Task RecordAutomaticOutcomeInspectionAsync(CurrentTurnContext turn, Session session, string resourceUrl, string toolCallId, CancellationToken ct)
    {
        if (missionStore is null || turn.MissionObjectiveId is not { } missionId
            || !Uri.TryCreate(resourceUrl, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https")) return;
        await AuthorizeAsync(turn, url.AbsoluteUri, "GET", ct, true);
        using var response = await session.Http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return;
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length > 65536) return;
        var mission = await missionStore.GetAsync(missionId, ct);
        if (mission?.LeaseToken is not { } lease) return;
        var proof = JsonSerializer.Serialize(new MissionBrowserOutcomeProof(url.AbsoluteUri, Convert.ToHexString(SHA256.HashData(bytes))));
        await missionStore.AttachRecoveryProofAsync(missionId, lease, toolCallId, proof, ct);
    }

    private async Task AuthorizeAsync(CurrentTurnContext turn, string url, string method, CancellationToken ct, bool readOnly = false)
    {
        var uri = new Uri(url, UriKind.Absolute);
        if (uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0)
            throw new MissionActionSkippedException("Browser request is not an HTTP(S) resource.");
        var rule = networkOptions.Value.BrowserRoutes.SingleOrDefault(r => r.Origin == uri.GetLeftPart(UriPartial.Authority)
            && r.Path == uri.AbsolutePath && r.Method.Equals(method, StringComparison.OrdinalIgnoreCase));
        // Unknown mutations are never silently called read-only. Host route contracts make
        // ordinary application writes usable without allowing an unrelated purchase/delete.
        var effects = rule?.Effects ?? (method is "GET" or "HEAD" ? MissionEffect.Inspect
            : MissionEffect.Send | MissionEffect.Modify | MissionEffect.Remove | MissionEffect.Financial | MissionEffect.Deploy | MissionEffect.DestroyProductionData);
        if (readOnly && effects != MissionEffect.Inspect)
            throw new MissionActionSkippedException("Navigation/snapshot cannot dispatch a mutating request. Use an explicitly targeted interaction.");
        var identity = uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
        if (rule?.ResourceUrl is { } logicalResource)
        {
            if (turn.MissionResources!.Evaluate(new(new(MissionResourceKind.Endpoint, identity), MissionEffect.Inspect, turn.ProfileId!.Value.ToString("D"))).Allowed != true)
                throw new MissionActionSkippedException("Browser API transport is outside assigned website scope.");
            identity = new Uri(logicalResource, UriKind.Absolute).GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
        }
        if (turn.MissionResources!.Evaluate(new(new(MissionResourceKind.Endpoint, identity), effects, turn.ProfileId!.Value.ToString("D"))).Allowed != true)
            throw new MissionActionSkippedException("Browser request is outside assigned resources/effects: " + method + " " + uri.GetLeftPart(UriPartial.Path));
        await turn.ValidateMissionExecution!(ct);
        ct.ThrowIfCancellationRequested();
    }

    private async Task BridgeAsync(Session session, IRoute route)
    {
        try
        {
            var turn = session.Active ?? throw new MissionActionSkippedException("No active mission browser action.");
            var ct = session.Cancellation;
            var url = route.Request.Url;
            var method = route.Request.Method;
            var originalOrigin = new Uri(url).GetLeftPart(UriPartial.Authority);
            var headers = await route.Request.AllHeadersAsync();
            var body = route.Request.PostDataBuffer;
            for (var redirects = 0; redirects <= 20; redirects++)
            {
                await AuthorizeAsync(turn, url, method, ct, session.ReadOnly);
                using var request = new HttpRequestMessage(new HttpMethod(method), url);
                if (body is not null) request.Content = new ByteArrayContent(body);
                foreach (var header in headers.Where(h => h.Key is not ("host" or "content-length" or "connection" or "accept-encoding")))
                    if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value)) request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value);
                using var response = await session.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    var next = new Uri(new Uri(url), location);
                    await AuthorizeAsync(turn, next.AbsoluteUri, method, ct, session.ReadOnly);
                    // Never forward cookies/authentication across origins. Return a concrete
                    // target for the next navigation instead of following it with these headers.
                    if (next.GetLeftPart(UriPartial.Authority) != originalOrigin)
                        throw new MissionActionSkippedException("Navigate separately to authorized redirect target: " + next.GetLeftPart(UriPartial.Path));
                    url = next.AbsoluteUri;
                    if (response.StatusCode == HttpStatusCode.SeeOther || method == "POST" && response.StatusCode is HttpStatusCode.Found or HttpStatusCode.MovedPermanently)
                    { method = "GET"; body = null; }
                    continue;
                }
                // A final (non-redirect) response is served even after the 20th redirect; only a 21st redirect is a loop.
                var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                await turn.ValidateMissionExecution!(ct);
                if (!session.ReadOnly && method is not ("GET" or "HEAD") && response.IsSuccessStatusCode)
                {
                    // Never re-derive this from the mutated path itself: an unclassified GET on the
                    // same route can be just as mutating as the POST that produced it. Only a route the
                    // host explicitly mapped to a distinct logical resource is safe to auto-verify later.
                    var finalUri = new Uri(url);
                    var mutationRule = networkOptions.Value.BrowserRoutes.FirstOrDefault(r => r.Origin == finalUri.GetLeftPart(UriPartial.Authority)
                        && r.Path == finalUri.AbsolutePath && r.Method.Equals(method, StringComparison.OrdinalIgnoreCase) && r.ResourceUrl is not null);
                    if (mutationRule is not null) session.PendingAutomaticResourceUrl = mutationRule.ResourceUrl;
                }
                var responseHeaders = response.Headers.Concat(response.Content.Headers)
                    .Where(h => !new[] { "content-length", "transfer-encoding", "content-encoding", "connection" }.Contains(h.Key.ToLowerInvariant()))
                    .ToDictionary(h => h.Key, h => string.Join(h.Key.Equals("set-cookie", StringComparison.OrdinalIgnoreCase) ? "\n" : ", ", h.Value));
                await route.FulfillAsync(new() { Status = (int)response.StatusCode, Headers = responseHeaders, BodyBytes = bytes });
                return;
            }
            throw new HttpRequestException("Browser redirect loop.");
        }
        catch (Exception ex)
        {
            session.Error = ex.Message;
            try { await route.AbortAsync(); } catch (PlaywrightException) { }
        }
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
        {
            await session.Gate.WaitAsync();
            try { if (session.Context is not null) await session.Context.CloseAsync(); session.Playwright?.Dispose(); session.Http.Dispose(); }
            finally { session.Gate.Release(); }
        }
        _sessions.Clear();
    }
    private sealed class Session
    {
        public PlaywrightComputerSurface? Computer;
        public long? ObservedAuthorityRevision;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public IPlaywright? Playwright;
        public IBrowserContext? Context;
        public IPage? Page;
        public CurrentTurnContext? Active;
        public CancellationToken Cancellation;
        public string? Error;
        public bool ReadOnly;
        /// <summary>Host-configured logical resource (<see cref="MissionBrowserRoute.ResourceUrl"/>)
        /// observed during the current action's mutating request, if any. Consumed once, right after
        /// the action completes, to capture an automatic outcome proof.</summary>
        public string? PendingAutomaticResourceUrl;
        public HttpClient Http { get; } = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.All });
    }
}
