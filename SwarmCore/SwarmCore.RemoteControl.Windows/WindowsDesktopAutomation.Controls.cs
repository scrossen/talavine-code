using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Interop.UIAutomationClient;
using static SwarmCore.RemoteControl.Windows.NativeMethods;

namespace SwarmCore.RemoteControl.Windows;

public sealed partial class WindowsDesktopAutomation
{
    /// <summary>
    /// Chromium (Chrome, Edge, Electron apps; UIA framework id "Chrome") enables accessibility lazily the first time a
    /// UIA client touches it, exposing empty or partial web documents for a moment; give it this long to fill in.
    /// </summary>
    private static readonly TimeSpan LazyAccessibilityWait = TimeSpan.FromMilliseconds(1500);
    private const string LazyAccessibilityFrameworkId = "Chrome";
    private const int LazyAccessibilityPollMilliseconds = 50;
    private const int UnknownDepth = -1;

    private const int ToggleOff = 0;
    private const int ToggleOn = 1;
    private const int ExpandStateCollapsed = 0;
    private const int ExpandStateExpanded = 1;
    private const int ExpandStatePartial = 2;
    private const int ExpandStateLeaf = 3;
    private const int FirstControlTypeId = UIA_ControlTypeIds.UIA_ButtonControlTypeId;

    // HRESULTs that mean the element (or its process) is gone, the provider timed out, or access was denied.
    private const int UiaElementNotAvailable = unchecked((int)0x80040201);
    private const int UiaElementNotEnabled = unchecked((int)0x80040200);
    private const int UiaNotSupported = unchecked((int)0x80040204);
    private const int UiaTimeout = unchecked((int)0x80131505);
    private const int RpcDisconnected = unchecked((int)0x80010108);
    private const int RpcServerUnavailable = unchecked((int)0x800706BA);
    private const int ObjectNotConnected = unchecked((int)0x800401FD);
    private const int AccessDenied = unchecked((int)0x80070005);

    /// <summary>Lowercase names indexed by UIA control type id minus 50000 (Button) through AppBar (50040).</summary>
    private static readonly string[] ControlTypeNames =
    [
        "button", "calendar", "checkbox", "combobox", "edit", "hyperlink", "image", "listitem", "list", "menu",
        "menubar", "menuitem", "progressbar", "radiobutton", "scrollbar", "slider", "spinner", "statusbar", "tab", "tabitem",
        "text", "toolbar", "tooltip", "tree", "treeitem", "custom", "group", "thumb", "datagrid", "dataitem",
        "document", "splitbutton", "window", "pane", "header", "headeritem", "table", "titlebar", "separator", "semanticzoom",
        "appbar",
    ];

    /// <summary>Every property ControlInfo reads; cached in one cross-process round trip per walk.</summary>
    private static readonly int[] CachedProperties =
    [
        UIA_PropertyIds.UIA_RuntimeIdPropertyId,
        UIA_PropertyIds.UIA_NamePropertyId,
        UIA_PropertyIds.UIA_ControlTypePropertyId,
        UIA_PropertyIds.UIA_AutomationIdPropertyId,
        UIA_PropertyIds.UIA_ClassNamePropertyId,
        UIA_PropertyIds.UIA_BoundingRectanglePropertyId,
        UIA_PropertyIds.UIA_IsEnabledPropertyId,
        UIA_PropertyIds.UIA_IsOffscreenPropertyId,
        UIA_PropertyIds.UIA_HasKeyboardFocusPropertyId,
        UIA_PropertyIds.UIA_HelpTextPropertyId,
        UIA_PropertyIds.UIA_IsPasswordPropertyId,
        UIA_PropertyIds.UIA_IsKeyboardFocusablePropertyId,
        UIA_PropertyIds.UIA_IsInvokePatternAvailablePropertyId,
        UIA_PropertyIds.UIA_IsTogglePatternAvailablePropertyId,
        UIA_PropertyIds.UIA_IsSelectionItemPatternAvailablePropertyId,
        UIA_PropertyIds.UIA_IsExpandCollapsePatternAvailablePropertyId,
        UIA_PropertyIds.UIA_IsValuePatternAvailablePropertyId,
        UIA_PropertyIds.UIA_IsScrollItemPatternAvailablePropertyId,
        UIA_PropertyIds.UIA_IsRangeValuePatternAvailablePropertyId,
        UIA_PropertyIds.UIA_IsLegacyIAccessiblePatternAvailablePropertyId,
        UIA_PropertyIds.UIA_ValueValuePropertyId,
        UIA_PropertyIds.UIA_ValueIsReadOnlyPropertyId,
        UIA_PropertyIds.UIA_ToggleToggleStatePropertyId,
        UIA_PropertyIds.UIA_SelectionItemIsSelectedPropertyId,
        UIA_PropertyIds.UIA_RangeValueValuePropertyId,
        UIA_PropertyIds.UIA_ExpandCollapseExpandCollapseStatePropertyId,
        UIA_PropertyIds.UIA_LegacyIAccessibleValuePropertyId,
        UIA_PropertyIds.UIA_FrameworkIdPropertyId,
    ];

    private readonly object sync = new();
    private volatile bool disposed;
    private UiaWorker? worker;
    // The fields below are touched only on the UIA worker thread.
    private IUIAutomation? automation;
    private readonly Dictionary<nint, Dictionary<string, RememberedElement>> remembered = [];

    private struct WalkFacts
    {
        public int Walked;
        public string? Truncation;
        public bool SawChromium;
        public bool SawEmptyChromiumDocument;
    }

    private sealed record RememberedElement(IUIAutomationElement Element, int Depth, string? ParentKey);

    /// <summary>What a control can do, read from the same property fetch as its <see cref="ControlInfo"/>.</summary>
    private sealed record Capabilities(bool Invoke, bool Toggle, bool SelectionItem, bool ExpandCollapse, int? ExpandState,
        bool Value, bool ValueReadOnly, bool ScrollItem, bool Legacy, bool Focusable);

    public void Dispose()
    {
        UiaWorker? toStop;
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            toStop = worker;
            worker = null;
            if (inputMonitorAcquired)
            {
                inputMonitorAcquired = false;
                UserInputMonitor.Release();
            }
        }
        toStop?.Dispose(() =>
        {
            remembered.Clear();
            if (automation is not null) Marshal.FinalReleaseComObject(automation);
            automation = null;
        });
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    /// <summary>Runs UIA work on the dedicated MTA worker with the automation object created there.</summary>
    private T Uia<T>(Func<IUIAutomation, T> work)
    {
        UiaWorker current;
        lock (sync)
        {
            ThrowIfDisposed();
            current = worker ??= new UiaWorker("SwarmCore.RemoteControl UI Automation");
        }
        return current.Invoke(() =>
        {
            using var dpi = new DpiScope();
            automation ??= CreateAutomation();
            return work(automation);
        });
    }

    /// <summary>CUIAutomation8 (Windows 8+, with IUIAutomation2 timeouts), else the original CUIAutomation.</summary>
    private static IUIAutomation CreateAutomation()
    {
        try { return new CUIAutomation8(); }
        catch (Exception ex) when (IsUiaError(ex))
        {
            try { return new CUIAutomation(); }
            catch (Exception inner) when (IsUiaError(inner)) { throw UiaFailure(inner, 0, "starting UI Automation"); }
        }
    }

    // ---- Tree walk -------------------------------------------------------------------------------------------

    private volatile bool lastWalkWasTruncated;
    private volatile string? lastWalkTruncationReason;

    /// <summary>
    /// True when the most recent <see cref="GetControls"/> stopped before visiting the whole tree because
    /// <see cref="ControlQuery.Timeout"/> expired or <see cref="ControlQuery.MaxCount"/> was reached (MaxDepth is a
    /// deliberate scope, not a truncation). The returned list is what was found before the cut.
    /// </summary>
    public bool LastWalkWasTruncated => lastWalkWasTruncated;

    /// <summary>A user-facing sentence saying why the most recent walk was truncated; null when it was complete.</summary>
    public string? LastWalkTruncationReason => lastWalkTruncationReason;

    public IReadOnlyList<ControlInfo> GetControls(long windowHandle, ControlQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var hwnd = (nint)windowHandle;
        RequireWindow(hwnd);
        return Uia(uia =>
        {
            lastWalkWasTruncated = false;
            lastWalkTruncationReason = null;
            foreach (var stale in remembered.Keys.Where(h => !IsWindow(h)).ToList())
            {
                Release(remembered[stale].Values, null);
                remembered.Remove(stale);
            }
            var clock = Stopwatch.StartNew();
            var firstTouch = !remembered.ContainsKey(hwnd);
            var previousWalked = -1;
            while (true)
            {
                var result = WalkOnce(uia, hwnd, query, clock, out var facts);
                // Re-walk while a Chromium document is still empty, or (on our first look at the window) until two
                // consecutive samples of the tree size match.
                var settling = facts.SawEmptyChromiumDocument || firstTouch && facts.SawChromium && facts.Walked != previousWalked;
                if (!settling || clock.Elapsed >= LazyAccessibilityWait || Expired(query, clock))
                {
                    lastWalkWasTruncated = facts.Truncation is not null;
                    lastWalkTruncationReason = facts.Truncation;
                    return (IReadOnlyList<ControlInfo>)result;
                }
                previousWalked = facts.Walked;
                Thread.Sleep(LazyAccessibilityPollMilliseconds); // sampling interval while Chromium builds its tree
            }
        });
    }

    private static bool Expired(ControlQuery query, Stopwatch clock)
        => query.Timeout != Timeout.InfiniteTimeSpan && clock.Elapsed >= query.Timeout;

    private List<ControlInfo> WalkOnce(IUIAutomation uia, nint hwnd, ControlQuery query, Stopwatch clock, out WalkFacts facts)
    {
        // Always the raw view: the include/exclude decision is ours (Matches), so named or valued elements that a
        // provider marks IsControlElement=false are not silently dropped.
        var view = uia.RawViewCondition;
        var results = new List<ControlInfo>();
        var map = new Dictionary<string, RememberedElement>();
        facts = default;
        var done = false;
        if (query.MaxDepth == int.MaxValue)
        {
            // Whole tree in one cross-process call (bounded by the query timeout), then walk the cached copy locally.
            var request = CreateCacheRequest(uia, TreeScope.TreeScope_Subtree, view);
            var root = FetchBulk(uia, hwnd, request, query, clock, out var failure);
            if (root is not null)
            {
                try
                {
                    Traverse(root, query, clock, results, map, el => el.GetCachedChildren() is { } children ? Enumerate(children) : [], ref facts);
                    done = true;
                }
                catch (Exception ex) when (IsUiaError(ex)) { failure = ex; }
            }
            if (!done)
            {
                if (failure is not null && !IsWindow(hwnd)) throw UiaFailure(failure, hwnd, "reading its controls");
                results.Clear();
                map.Clear();
                facts = default;
            }
        }
        if (!done)
        {
            // Depth-limited walks, and the fallback when the bulk fetch fails or exceeds the time budget: a live tree
            // walker that fetches each element's properties in one call and checks the deadline per element.
            try
            {
                var request = CreateCacheRequest(uia, TreeScope.TreeScope_Element, view);
                var walker = uia.CreateTreeWalker(view);
                var root = uia.ElementFromHandleBuildCache(hwnd, request);
                Traverse(root, query, clock, results, map, el => LiveChildren(walker, el, request), ref facts);
            }
            catch (Exception ex) when (IsUiaError(ex)) { throw UiaFailure(ex, hwnd, "reading its controls"); }
        }
        if (remembered.TryGetValue(hwnd, out var previous)) Release(previous.Values, map);
        remembered[hwnd] = map;
        return results;
    }

    /// <summary>
    /// The single-call subtree fetch. With a finite timeout it runs on a pool thread (MTA, like the worker, so the
    /// automation object is used directly) and is abandoned when it exceeds the remaining budget; the caller then
    /// walks incrementally. Null means "fall back"; <paramref name="failure"/> holds the error when it failed.
    /// </summary>
    private static IUIAutomationElement? FetchBulk(IUIAutomation uia, nint hwnd, IUIAutomationCacheRequest request,
        ControlQuery query, Stopwatch clock, out Exception? failure)
    {
        failure = null;
        if (query.Timeout == Timeout.InfiniteTimeSpan)
        {
            try { return uia.ElementFromHandleBuildCache(hwnd, request); }
            catch (Exception ex) when (IsUiaError(ex)) { failure = ex; return null; }
        }
        var remaining = query.Timeout - clock.Elapsed;
        if (remaining <= TimeSpan.Zero) return null;
        var fetch = Task.Run(() => uia.ElementFromHandleBuildCache(hwnd, request));
        bool completed;
        try { completed = fetch.Wait(remaining); }
        catch (AggregateException ex) when (ex.InnerException is { } inner && IsUiaError(inner)) { failure = inner; return null; }
        if (completed) return fetch.Result;
        // Abandoned: let it finish in the background and observe its outcome so nothing goes unobserved.
        fetch.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
        return null;
    }

    /// <summary>Releases the COM references of a replaced walk that the new walk does not reuse.</summary>
    private static void Release(IEnumerable<RememberedElement> old, Dictionary<string, RememberedElement>? keep)
    {
        var kept = new HashSet<object>(ReferenceEqualityComparer.Instance);
        if (keep is not null) foreach (var r in keep.Values) kept.Add(r.Element);
        var released = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var r in old)
        {
            if (kept.Contains(r.Element) || !released.Add(r.Element)) continue;
            try { Marshal.ReleaseComObject(r.Element); }
            catch (ArgumentException) { } // not a COM wrapper
        }
    }

    private static IEnumerable<IUIAutomationElement> LiveChildren(IUIAutomationTreeWalker walker, IUIAutomationElement parent, IUIAutomationCacheRequest request)
    {
        var children = new List<IUIAutomationElement>();
        for (var child = walker.GetFirstChildElementBuildCache(parent, request); child is not null; child = walker.GetNextSiblingElementBuildCache(child, request))
            children.Add(child);
        return children;
    }

    private static List<IUIAutomationElement> Enumerate(IUIAutomationElementArray array)
    {
        var list = new List<IUIAutomationElement>(array.Length);
        for (var i = 0; i < array.Length; i++) list.Add(array.GetElement(i));
        return list;
    }

    /// <summary>
    /// Depth-first, document order, with an explicit stack so arbitrarily deep trees cannot overflow. The root is
    /// always visited; after that the deadline is checked per element.
    /// </summary>
    private static void Traverse(IUIAutomationElement root, ControlQuery query, Stopwatch clock, List<ControlInfo> results,
        Dictionary<string, RememberedElement> map, Func<IUIAutomationElement, IEnumerable<IUIAutomationElement>> children, ref WalkFacts facts)
    {
        var stack = new Stack<(IUIAutomationElement Element, int Depth, string? Parent)>();
        stack.Push((root, 0, null));
        while (stack.Count > 0)
        {
            if (facts.Walked > 0 && Expired(query, clock))
            {
                facts.Truncation = $"The walk stopped when its {(query.Timeout.TotalSeconds >= 1 ? $"{query.Timeout.TotalSeconds:0.#} s" : $"{query.Timeout.TotalMilliseconds:0} ms")} time limit expired; {stack.Count} more branch{(stack.Count == 1 ? "" : "es")} were not read.";
                return;
            }
            var (element, depth, parent) = stack.Pop();
            ControlInfo info;
            try { info = Map(element.GetCachedPropertyValue, depth, parent, out _); }
            catch (Exception ex) when (IsGone(ex)) { continue; } // vanished mid-walk
            if (info.RuntimeKey.Length > 0) map[info.RuntimeKey] = new RememberedElement(element, depth, parent);
            facts.Walked++;
            var isChromium = string.Equals(element.GetCachedPropertyValue(UIA_PropertyIds.UIA_FrameworkIdPropertyId) as string,
                LazyAccessibilityFrameworkId, StringComparison.OrdinalIgnoreCase);
            facts.SawChromium |= isChromium;
            if (Matches(info, query))
            {
                results.Add(info);
                if (results.Count >= query.MaxCount)
                {
                    if (stack.Count > 0 || depth < query.MaxDepth)
                        facts.Truncation = $"The walk stopped at the requested maximum of {query.MaxCount} controls; there may be more.";
                    return;
                }
            }
            if (depth >= query.MaxDepth) continue;
            List<IUIAutomationElement> kids;
            try { kids = children(element).ToList(); }
            catch (Exception ex) when (IsGone(ex)) { continue; }
            if (isChromium && kids.Count == 0 && info.ControlType == "document" && !info.Bounds.IsEmpty) facts.SawEmptyChromiumDocument = true;
            for (var i = kids.Count - 1; i >= 0; i--) stack.Push((kids[i], depth + 1, info.RuntimeKey));
        }
    }

    private static bool Matches(ControlInfo info, ControlQuery query)
    {
        if (!query.IncludeOffscreen && info.IsOffscreen) return false;
        if (!query.IncludeUnnamed && info.Name.Length == 0 && string.IsNullOrEmpty(info.Value)
            && !info.SupportedActions.Any(a => a is not (ControlAction.Focus or ControlAction.ScrollIntoView)))
            return false;
        if (query.ControlType is { Length: > 0 } type && !string.Equals(info.ControlType, type, StringComparison.OrdinalIgnoreCase)) return false;
        // A set of types: an element outside it is skipped (its children are still traversed by the caller).
        if (query.ControlTypes is { Count: > 0 } types && !types.Contains(info.ControlType, StringComparer.OrdinalIgnoreCase)) return false;
        if (query.NameContains is { Length: > 0 } text
            && !info.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
            && !(info.AutomationId?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false))
            return false;
        return true;
    }

    private static IUIAutomationCacheRequest CreateCacheRequest(IUIAutomation uia, TreeScope scope, IUIAutomationCondition filter)
    {
        var request = uia.CreateCacheRequest();
        foreach (var property in CachedProperties) request.AddProperty(property);
        request.TreeScope = scope;
        request.TreeFilter = filter;
        request.AutomationElementMode = AutomationElementMode.AutomationElementMode_Full;
        return request;
    }

    private static IUIAutomationCacheRequest ElementRequest(IUIAutomation uia)
        => CreateCacheRequest(uia, TreeScope.TreeScope_Element, uia.RawViewCondition);

    // ---- Mapping ---------------------------------------------------------------------------------------------

    private static ControlInfo Map(Func<int, object?> get, int depth, string? parentKey, out Capabilities capabilities)
    {
        var runtimeId = get(UIA_PropertyIds.UIA_RuntimeIdPropertyId) as int[];
        var name = get(UIA_PropertyIds.UIA_NamePropertyId) as string ?? "";
        var type = ControlTypeName(get(UIA_PropertyIds.UIA_ControlTypePropertyId) is int typeId ? typeId : 0);
        bool Flag(int id) => get(id) is true;

        var valueAvailable = Flag(UIA_PropertyIds.UIA_IsValuePatternAvailablePropertyId);
        var readOnly = valueAvailable && Flag(UIA_PropertyIds.UIA_ValueIsReadOnlyPropertyId);
        var toggleAvailable = Flag(UIA_PropertyIds.UIA_IsTogglePatternAvailablePropertyId);
        var selectionAvailable = Flag(UIA_PropertyIds.UIA_IsSelectionItemPatternAvailablePropertyId);
        var expandAvailable = Flag(UIA_PropertyIds.UIA_IsExpandCollapsePatternAvailablePropertyId);
        var rangeAvailable = Flag(UIA_PropertyIds.UIA_IsRangeValuePatternAvailablePropertyId);
        var legacyAvailable = Flag(UIA_PropertyIds.UIA_IsLegacyIAccessiblePatternAvailablePropertyId);
        int? expandState = expandAvailable && get(UIA_PropertyIds.UIA_ExpandCollapseExpandCollapseStatePropertyId) is int state ? state : null;

        string? value = valueAvailable ? get(UIA_PropertyIds.UIA_ValueValuePropertyId) as string : null;
        bool? toggled = null;
        if (toggleAvailable && get(UIA_PropertyIds.UIA_ToggleToggleStatePropertyId) is int toggleState)
        {
            toggled = toggleState switch { ToggleOn => true, ToggleOff => false, _ => null };
            value ??= toggleState switch { ToggleOn => "on", ToggleOff => "off", _ => "indeterminate" };
        }
        bool? selected = null;
        if (selectionAvailable && get(UIA_PropertyIds.UIA_SelectionItemIsSelectedPropertyId) is bool isSelected)
        {
            selected = isSelected;
            if (isSelected) value ??= "selected";
        }
        if (rangeAvailable && get(UIA_PropertyIds.UIA_RangeValueValuePropertyId) is double range)
            value ??= range.ToString("G", CultureInfo.InvariantCulture);
        // Last resort, and never for text elements, whose name already is their text.
        if (value is null && legacyAvailable && type != "text"
            && get(UIA_PropertyIds.UIA_LegacyIAccessibleValuePropertyId) is string { Length: > 0 } legacy && legacy != name)
            value = legacy;

        capabilities = new Capabilities(
            Flag(UIA_PropertyIds.UIA_IsInvokePatternAvailablePropertyId), toggleAvailable, selectionAvailable, expandAvailable, expandState,
            valueAvailable, readOnly, Flag(UIA_PropertyIds.UIA_IsScrollItemPatternAvailablePropertyId), legacyAvailable,
            Flag(UIA_PropertyIds.UIA_IsKeyboardFocusablePropertyId));

        return new ControlInfo(
            runtimeId is null ? "" : string.Join('.', runtimeId),
            type,
            name,
            NonEmpty(get(UIA_PropertyIds.UIA_AutomationIdPropertyId) as string),
            NonEmpty(get(UIA_PropertyIds.UIA_ClassNamePropertyId) as string),
            value,
            Flag(UIA_PropertyIds.UIA_IsEnabledPropertyId),
            Flag(UIA_PropertyIds.UIA_IsOffscreenPropertyId),
            Flag(UIA_PropertyIds.UIA_HasKeyboardFocusPropertyId),
            Bounds(get(UIA_PropertyIds.UIA_BoundingRectanglePropertyId)),
            depth,
            Actions(capabilities))
        {
            HelpText = NonEmpty(get(UIA_PropertyIds.UIA_HelpTextPropertyId) as string),
            ParentRuntimeKey = parentKey,
            IsToggled = toggled,
            IsSelected = selected,
            IsReadOnly = readOnly,
            IsPassword = Flag(UIA_PropertyIds.UIA_IsPasswordPropertyId),
        };
    }

    private static string? NonEmpty(string? text) => string.IsNullOrEmpty(text) ? null : text;

    private static string ControlTypeName(int id)
    {
        var index = id - FirstControlTypeId;
        return index >= 0 && index < ControlTypeNames.Length ? ControlTypeNames[index] : "custom";
    }

    /// <summary>UIA reports bounds as [left, top, width, height] doubles in physical pixels (we are per-monitor aware).</summary>
    private static ScreenRect Bounds(object? value)
    {
        if (value is not double[] { Length: 4 } r || !r.All(double.IsFinite)) return default;
        return new ScreenRect((int)Math.Round(r[0]), (int)Math.Round(r[1]), (int)Math.Round(r[2]), (int)Math.Round(r[3]));
    }

    private static List<ControlAction> Actions(Capabilities c)
    {
        var actions = new List<ControlAction>();
        if (c.Invoke) actions.Add(ControlAction.Invoke);
        if (c.Toggle) actions.Add(ControlAction.Toggle);
        if (c.SelectionItem) actions.Add(ControlAction.Select);
        if (c.ExpandCollapse)
        {
            if (c.ExpandState is null or ExpandStateCollapsed or ExpandStatePartial) actions.Add(ControlAction.Expand);
            if (c.ExpandState is null or ExpandStateExpanded or ExpandStatePartial) actions.Add(ControlAction.Collapse);
        }
        if (c.Value && !c.ValueReadOnly) actions.Add(ControlAction.SetValue);
        if (c.Focusable) actions.Add(ControlAction.Focus);
        if (c.ScrollItem) actions.Add(ControlAction.ScrollIntoView);
        return actions;
    }

    // ---- Single elements -------------------------------------------------------------------------------------

    public ControlInfo? RefreshControl(long windowHandle, string runtimeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeKey);
        var hwnd = (nint)windowHandle;
        RequireWindow(hwnd);
        return Uia(uia =>
        {
            var found = Resolve(uia, hwnd, runtimeKey);
            if (found is null) return null;
            try { return Fresh(uia, found, out _); }
            catch (Exception ex) when (IsGone(ex)) { return null; }
            catch (Exception ex) when (IsUiaError(ex)) { throw UiaFailure(ex, hwnd, "reading the control"); }
        });
    }

    public ControlInfo? GetControlAt(ScreenPoint point)
    {
        return Uia(uia =>
        {
            try
            {
                var element = uia.ElementFromPointBuildCache(new tagPOINT { x = point.X, y = point.Y }, ElementRequest(uia));
                return element is null ? null : Map(element.GetCachedPropertyValue, UnknownDepth, ParentKey(uia, element), out _);
            }
            catch (Exception ex) when (IsGone(ex)) { return null; }
            catch (Exception ex) when (IsUiaError(ex)) { throw UiaFailure(ex, RootWindowAt(point), $"reading the control at {point}"); }
        });
    }

    public ControlInfo? GetFocusedControl()
    {
        return Uia(uia =>
        {
            try
            {
                var element = uia.GetFocusedElementBuildCache(ElementRequest(uia));
                return element is null ? null : Map(element.GetCachedPropertyValue, UnknownDepth, ParentKey(uia, element), out _);
            }
            catch (Exception ex) when (IsGone(ex)) { return null; }
            catch (Exception ex) when (IsUiaError(ex)) { throw UiaFailure(ex, GetForegroundWindow(), "reading the focused control"); }
        });
    }

    private static string? ParentKey(IUIAutomation uia, IUIAutomationElement element)
    {
        try
        {
            var parent = uia.RawViewWalker.GetParentElement(element);
            return parent?.GetRuntimeId() is { } id ? string.Join('.', id) : null;
        }
        catch (Exception ex) when (IsUiaError(ex)) { return null; } // the parent is optional context
    }

    /// <summary>
    /// The remembered element when it is still alive and still the same element; else FindFirst on RuntimeId; else
    /// (many providers do not answer RuntimeId conditions) a walk of the window's raw tree comparing RuntimeIds.
    /// Null only when none of them finds it.
    /// </summary>
    private RememberedElement? Resolve(IUIAutomation uia, nint hwnd, string runtimeKey)
    {
        if (remembered.TryGetValue(hwnd, out var map) && map.TryGetValue(runtimeKey, out var known))
        {
            try
            {
                if (known.Element.GetCurrentPropertyValue(UIA_PropertyIds.UIA_RuntimeIdPropertyId) is int[] id && string.Join('.', id) == runtimeKey)
                    return known;
            }
            catch (Exception ex) when (IsUiaError(ex)) { } // gone or unreachable: search afresh below
            map.Remove(runtimeKey);
        }
        var ints = ParseRuntimeKey(runtimeKey);
        if (ints is null) return null;
        try
        {
            var root = uia.ElementFromHandle(hwnd);
            IUIAutomationElement? found = null;
            try { found = root.FindFirst(TreeScope.TreeScope_Subtree, uia.CreatePropertyCondition(UIA_PropertyIds.UIA_RuntimeIdPropertyId, ints)); }
            catch (Exception ex) when (IsUiaError(ex) && !IsGone(ex)) { } // some providers reject the condition
            found ??= FindByWalking(uia, root, ints);
            if (found is null) return null;
            var element = new RememberedElement(found, UnknownDepth, null);
            if (map is not null) map[runtimeKey] = element;
            return element;
        }
        catch (Exception ex) when (IsGone(ex) && IsWindow(hwnd)) { return null; }
        catch (Exception ex) when (IsUiaError(ex)) { throw UiaFailure(ex, hwnd, "finding the control"); }
    }

    private static IUIAutomationElement? FindByWalking(IUIAutomation uia, IUIAutomationElement root, int[] runtimeId)
    {
        var request = uia.CreateCacheRequest();
        request.AddProperty(UIA_PropertyIds.UIA_RuntimeIdPropertyId);
        request.TreeScope = TreeScope.TreeScope_Subtree;
        request.TreeFilter = uia.RawViewCondition;
        request.AutomationElementMode = AutomationElementMode.AutomationElementMode_Full;
        IUIAutomationElement tree;
        try { tree = root.BuildUpdatedCache(request); }
        catch (Exception ex) when (IsUiaError(ex) && !IsGone(ex)) { return FindByLiveWalk(uia, root, runtimeId); }
        var stack = new Stack<IUIAutomationElement>();
        stack.Push(tree);
        while (stack.Count > 0)
        {
            var element = stack.Pop();
            if (element.GetCachedPropertyValue(UIA_PropertyIds.UIA_RuntimeIdPropertyId) is int[] id && id.AsSpan().SequenceEqual(runtimeId)) return element;
            if (element.GetCachedChildren() is { } children)
                for (var i = children.Length - 1; i >= 0; i--) stack.Push(children.GetElement(i));
        }
        return null;
    }

    private static IUIAutomationElement? FindByLiveWalk(IUIAutomation uia, IUIAutomationElement root, int[] runtimeId)
    {
        var walker = uia.RawViewWalker;
        var stack = new Stack<IUIAutomationElement>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var element = stack.Pop();
            try
            {
                if (element.GetRuntimeId() is { } id && id.AsSpan().SequenceEqual(runtimeId)) return element;
                for (var child = walker.GetFirstChildElement(element); child is not null; child = walker.GetNextSiblingElement(child)) stack.Push(child);
            }
            catch (Exception ex) when (IsGone(ex)) { } // a branch vanished while we looked
        }
        return null;
    }

    private static int[]? ParseRuntimeKey(string key)
    {
        var parts = key.Split('.');
        var ints = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out ints[i])) return null;
        return ints;
    }

    /// <summary>A fresh read (one round trip) of the element, keeping its walk position.</summary>
    private static ControlInfo Fresh(IUIAutomation uia, RememberedElement found, out Capabilities capabilities)
    {
        var updated = found.Element.BuildUpdatedCache(ElementRequest(uia));
        return Map(updated.GetCachedPropertyValue, found.Depth, found.ParentKey, out capabilities);
    }

    // ---- Actions ---------------------------------------------------------------------------------------------

    public ControlActionResult PerformAction(long windowHandle, string runtimeKey, ControlAction action, string? value = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeKey);
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action), action, null);
        if (action == ControlAction.SetValue) ArgumentNullException.ThrowIfNull(value);
        var hwnd = (nint)windowHandle;
        RequireWindow(hwnd);
        return Uia(uia =>
        {
            var found = Resolve(uia, hwnd, runtimeKey) ?? throw ControlGone();
            ControlInfo before;
            Capabilities can;
            try { before = Fresh(uia, found, out can); }
            catch (Exception ex) when (IsGone(ex)) { throw ControlGone(ex); }
            catch (Exception ex) when (IsUiaError(ex)) { throw UiaFailure(ex, hwnd, "reading the control"); }

            var element = found.Element;
            var label = Describe(before);
            var step = $"{action} on {label}";
            var supported = before.SupportedActions.Count == 0
                ? "it supports no accessibility actions, so use a mouse click or keys instead"
                : "it supports " + string.Join(", ", before.SupportedActions);
            (string Method, string Message)? done;
            try
            {
                done = action switch
                {
                    ControlAction.Invoke => can.Invoke ? Do("invoke-pattern", "Invoked", () => Pattern<IUIAutomationInvokePattern>(element, UIA_PatternIds.UIA_InvokePatternId).Invoke())
                        : can.Legacy ? Do("legacy-accessible", "Performed the default action of", () => Pattern<IUIAutomationLegacyIAccessiblePattern>(element, UIA_PatternIds.UIA_LegacyIAccessiblePatternId).DoDefaultAction())
                        : null,
                    ControlAction.Toggle => can.Toggle ? Do("toggle-pattern", "Toggled", () => Pattern<IUIAutomationTogglePattern>(element, UIA_PatternIds.UIA_TogglePatternId).Toggle()) : null,
                    ControlAction.Select => can.SelectionItem ? Do("selection-pattern", "Selected", () => Pattern<IUIAutomationSelectionItemPattern>(element, UIA_PatternIds.UIA_SelectionItemPatternId).Select())
                        : can.ExpandCollapse ? Do("expand-collapse-pattern", "Expanded", () => Pattern<IUIAutomationExpandCollapsePattern>(element, UIA_PatternIds.UIA_ExpandCollapsePatternId).Expand())
                        : can.Invoke ? Do("invoke-pattern", "Invoked", () => Pattern<IUIAutomationInvokePattern>(element, UIA_PatternIds.UIA_InvokePatternId).Invoke())
                        : null,
                    ControlAction.Expand => can.ExpandCollapse ? Do("expand-collapse-pattern", "Expanded", () => Pattern<IUIAutomationExpandCollapsePattern>(element, UIA_PatternIds.UIA_ExpandCollapsePatternId).Expand()) : null,
                    ControlAction.Collapse => can.ExpandCollapse ? Do("expand-collapse-pattern", "Collapsed", () => Pattern<IUIAutomationExpandCollapsePattern>(element, UIA_PatternIds.UIA_ExpandCollapsePatternId).Collapse()) : null,
                    ControlAction.SetValue => SetValue(element, can, label, value!),
                    ControlAction.Focus => Do("focus", "Focused", element.SetFocus),
                    ControlAction.ScrollIntoView => can.ScrollItem ? Do("scroll-item-pattern", "Scrolled into view", () => Pattern<IUIAutomationScrollItemPattern>(element, UIA_PatternIds.UIA_ScrollItemPatternId).ScrollIntoView()) : null,
                    _ => null,
                };
            }
            catch (Exception ex) when (IsUiaError(ex) && ex.HResult == UiaElementNotEnabled)
            {
                throw new DesktopAutomationException($"{label} is disabled, so {action} is not possible right now; {supported}.", ex, FailureCategories.Unsupported);
            }
            catch (Exception ex) when (IsUiaError(ex)) { throw UiaFailure(ex, hwnd, step); }

            if (done is null)
            {
                return new ControlActionResult(false, "unsupported", $"{label} does not support {action}; {supported}.", before);
            }
            ControlInfo? after;
            try { after = Fresh(uia, found, out _); }
            catch (Exception ex) when (IsUiaError(ex)) { after = null; } // it may legitimately disappear (a Close button, a dismissed menu)
            return new ControlActionResult(true, done.Value.Method, $"{done.Value.Message} {label}.", after);
        });

        static (string, string)? Do(string method, string message, Action act) { act(); return (method, message); }
    }

    private static (string Method, string Message)? SetValue(IUIAutomationElement element, Capabilities can, string label, string value)
    {
        if (can.Value)
        {
            if (can.ValueReadOnly)
                throw new DesktopAutomationException($"{label} is read-only; its value cannot be changed.", FailureCategories.Unsupported);
            Pattern<IUIAutomationValuePattern>(element, UIA_PatternIds.UIA_ValuePatternId).SetValue(value);
            return ("value-pattern", "Set the value of");
        }
        if (can.Legacy)
        {
            Pattern<IUIAutomationLegacyIAccessiblePattern>(element, UIA_PatternIds.UIA_LegacyIAccessiblePatternId).SetValue(value);
            return ("legacy-accessible", "Set the value of");
        }
        return null;
    }

    private static T Pattern<T>(IUIAutomationElement element, int patternId) where T : class
        => element.GetCurrentPattern(patternId) as T
           ?? throw new COMException("The control stopped offering this pattern.", UiaNotSupported);

    private static string Describe(ControlInfo control)
        => control.Name.Length > 0 ? $"the {control.ControlType} '{control.Name}'" : $"the unnamed {control.ControlType} ({control.RuntimeKey})";

    // ---- Failures --------------------------------------------------------------------------------------------

    /// <summary>
    /// Exceptions a UIA call can surface: COM interop maps some HRESULTs to managed types (E_ACCESSDENIED to
    /// UnauthorizedAccessException, UIA_E_TIMEOUT to TimeoutException, UIA_E_INVALIDOPERATION to
    /// InvalidOperationException, E_INVALIDARG to ArgumentException) and leaves the rest as COMException. All are
    /// classified by <see cref="Exception.HResult"/>.
    /// </summary>
    private static bool IsUiaError(Exception ex)
        => ex is COMException or UnauthorizedAccessException or TimeoutException or ArgumentException
           || ex is InvalidOperationException and not ObjectDisposedException;

    private static bool IsGone(Exception ex)
        => IsUiaError(ex) && ex.HResult is UiaElementNotAvailable or RpcDisconnected or RpcServerUnavailable or ObjectNotConnected;

    private static DesktopAutomationException ControlGone(Exception? inner = null)
    {
        const string message = "That control is no longer there; list the window's controls again.";
        return inner is null ? new(message, FailureCategories.ControlGone) : new(message, inner, FailureCategories.ControlGone);
    }

    private static DesktopAutomationException UiaFailure(Exception ex, nint hwnd, string step)
    {
        if (hwnd != 0 && !IsWindow(hwnd))
            return new($"Window {(long)hwnd} closed while {step}; list the windows again.", ex, FailureCategories.WindowGone);
        if (IsGone(ex)) return ControlGone(ex);
        if (ex.HResult == UiaTimeout || DesktopState.IsHung(hwnd)) return DesktopState.Busy(hwnd, step, ex);
        if (ex.HResult == AccessDenied)
        {
            if (DesktopState.IsAuthenticationSurfaceActive()) return DesktopState.AuthenticationRequired(step);
            if (ProcessIntegrity.IsWindowHigherThanCurrent(hwnd)) return DesktopState.Elevated(hwnd, step, ex);
        }
        if (ex.HResult == UiaElementNotEnabled) return new($"The control is disabled, so {step} is not possible right now.", ex, FailureCategories.Unsupported);
        if (ex.HResult == UiaNotSupported) return new($"The control does not support {step}.", ex, FailureCategories.Unsupported);
        return new($"UI Automation failed during {step}: {ex.Message} (0x{ex.HResult:X8}).", ex, FailureCategories.OsRefused);
    }
}
