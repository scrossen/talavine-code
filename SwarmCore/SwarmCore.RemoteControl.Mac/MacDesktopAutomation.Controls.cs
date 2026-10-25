using System.Diagnostics;
using SwarmCore.RemoteControl.Mac.Mapping;
using SwarmCore.RemoteControl.Mac.Native;

namespace SwarmCore.RemoteControl.Mac;

/// <summary>Counters for the most recent <see cref="MacDesktopAutomation.GetControls"/> walk.</summary>
/// <param name="ElementsVisited">AX elements read (one batched attribute call each).</param>
/// <param name="ElementsReturned">Controls returned to the caller.</param>
/// <param name="AxCalls">AX IPC round trips made by the walk, including the batched reads.</param>
/// <param name="Elapsed">Wall time of the walk.</param>
public sealed record MacWalkStats(int ElementsVisited, int ElementsReturned, long AxCalls, TimeSpan Elapsed)
{
    public double CallsPerElement => ElementsVisited == 0 ? 0 : (double)AxCalls / ElementsVisited;

    public override string ToString() =>
        $"{ElementsVisited} elements read, {ElementsReturned} returned, {AxCalls} AX calls ({CallsPerElement:0.0} per element), {Elapsed.TotalMilliseconds:0} ms";
}

public sealed partial class MacDesktopAutomation
{
    // Attributes read for one element in a single AXUIElementCopyMultipleAttributeValues round trip. The walk adds
    // AXChildren to the same call so traversal costs no extra IPC.
    private static readonly string[] s_detailAttributes =
    [
        "AXRole", "AXSubrole", "AXTitle", "AXDescription", "AXValue", "AXIdentifier", "AXEnabled", "AXFocused",
        "AXPosition", "AXSize", "AXSelected", "AXTitleUIElement", "AXMenuItemMarkChar",
    ];
    private static readonly string[] s_walkAttributes = [.. s_detailAttributes, "AXChildren"];
    private const int AttrRole = 0, AttrSubrole = 1, AttrTitle = 2, AttrDescription = 3, AttrValue = 4, AttrIdentifier = 5,
        AttrEnabled = 6, AttrFocused = 7, AttrPosition = 8, AttrSize = 9, AttrSelected = 10, AttrTitleElement = 11,
        AttrMarkChar = 12, AttrChildren = 13;

    // Process-wide: the enhanced-accessibility switches belong to the target app, not to this adapter instance.
    private static readonly HashSet<int> s_enhancedPids = new();
    private static readonly object s_enhancedGate = new();

    private readonly record struct ControlRead(ControlInfo Info, string? Role, string Fingerprint);

    /// <summary>The attributes that identify an element (role, subrole, computed name, identifier).</summary>
    private readonly record struct ElementIdentity(string? Role, string? Subrole, string Name, string? Identifier, object? RawValue)
    {
        public string Fingerprint => AxFingerprint.Create(Role, Subrole, Name, Identifier);
    }

    /// <summary>The follow-up answers an <see cref="AxCallPlan"/> asked for (false/empty when not asked).</summary>
    private readonly record struct ElementExtras(List<string> Actions, bool ValueSettable, bool FocusedSettable, bool SelectedSettable, bool ExpandedSettable);

    private readonly record struct WalkItem(nint Element, int[] Path, int Depth, string? ParentRole, string? ParentKey);

    /// <summary>Counters for the most recent <see cref="GetControls"/> walk; null before the first walk.</summary>
    public MacWalkStats? LastWalkStats => _lastWalkStats;

    // ---------------------------------------------------------------- walk

    /// <remarks>
    /// Call budget: one batched AX call per element visited (attributes plus children). Elements that fail the type,
    /// offscreen, or name filters, and unnamed structural elements, cost nothing more. Returned elements add only the
    /// follow-up calls their <see cref="AxCallPlan"/> asks for (0 for text/images/groups, 2-4 for interactive controls).
    /// </remarks>
    public IReadOnlyList<ControlInfo> GetControls(long windowHandle, ControlQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        lock (_gate)
        {
            ThrowIfDisposed();
            EnsureAccessibility();
            var watch = Stopwatch.StartNew();
            var callsAtStart = _axCalls;
            var (entry, axWindowBorrowed) = RequireAxWindow(windowHandle);
            using var axWindow = new CfRef(CF.CFRetain(axWindowBorrowed));
            EnableEnhancedAccessibility(entry.Pid);

            var windowFrame = ReadFrame(axWindow.Value) ?? entry.Bounds ?? default;
            var windowId = (long)entry.Id;
            var map = new Dictionary<string, RetainedElement>(StringComparer.Ordinal);
            var results = new List<ControlInfo>();
            var stack = new Stack<WalkItem>();
            stack.Push(new WalkItem(CF.CFRetain(axWindow.Value), [0], 0, null, null));
            string? truncation = null;
            var visited = 0;
            var typeFilter = AxTypeFilter.Create(query.ControlType, query.ControlTypes);

            try
            {
                while (stack.Count > 0)
                {
                    if (results.Count >= query.MaxCount)
                    {
                        truncation = $"stopped after the requested maximum of {query.MaxCount} controls; more elements were not read";
                        break;
                    }
                    if (query.Timeout != Timeout.InfiniteTimeSpan && watch.Elapsed >= query.Timeout)
                    {
                        truncation = $"stopped after the {query.Timeout.TotalSeconds:0.#} s time limit; the rest of the window was not read";
                        break;
                    }

                    var item = stack.Pop();
                    visited++;
                    var keep = false;
                    try
                    {
                        var walked = WalkElement(item, entry.Pid, windowId, windowFrame, query, typeFilter);
                        if (walked.Read is { } r)
                        {
                            results.Add(r.Info);
                            if (map.TryGetValue(r.Info.RuntimeKey, out var dup)) CF.Release(dup.Element);
                            map[r.Info.RuntimeKey] = new RetainedElement(item.Element, r.Role, r.Fingerprint);
                            keep = true;
                        }
                        var children = walked.Children;
                        for (var i = children.Count - 1; i >= 0; i--)
                        {
                            var childPath = new int[item.Path.Length + 1];
                            item.Path.CopyTo(childPath, 0);
                            childPath[^1] = i;
                            stack.Push(new WalkItem(children[i], childPath, item.Depth + 1, walked.Role, walked.Key));
                        }
                    }
                    finally
                    {
                        if (!keep) CF.Release(item.Element);
                    }
                }
            }
            finally
            {
                while (stack.Count > 0) CF.Release(stack.Pop().Element);
                _lastWalkStats = new MacWalkStats(visited, results.Count, _axCalls - callsAtStart, watch.Elapsed);
                _lastWalkTruncationReason = truncation is null ? null : $"{truncation} ({_lastWalkStats})";
            }

            if (_controlMaps.Remove(windowId, out var old)) ReleaseMap(old);
            _controlMaps[windowId] = map;
            return results;
        }
    }

    /// <summary>Result of visiting one element: the control when it is returned, its role/key for its children, and its children (+1 each).</summary>
    private readonly record struct WalkedElement(ControlRead? Read, string? Role, string Key, List<nint> Children);

    private WalkedElement WalkElement(WalkItem item, int pid, long windowId, RectD windowFrame, ControlQuery query, AxTypeFilter typeFilter)
    {
        var key = AxRuntimeKey.Format(pid, windowId, item.Path);
        var slots = CopyAttributes(item.Element, s_walkAttributes, WalkAttributeArray);
        if (slots is null) return new WalkedElement(null, null, key, []);
        try
        {
            var role = Cf.ToStringOrNull(slots[AttrRole]);
            var subrole = Cf.ToStringOrNull(slots[AttrSubrole]);
            var controlType = AxRoleMap.Map(role, subrole, item.ParentRole);

            List<nint> children = [];
            if (item.Depth < query.MaxDepth)
            {
                children = Cf.RetainArrayItems(slots[AttrChildren]);
                if (children.Count == 0 && !Cf.Is(slots[AttrChildren], Cf.ArrayType) && AxCallPlan.UsesVisibleChildrenFallback(role))
                {
                    using var visible = new CfRef(CopyAttribute(item.Element, "AXVisibleChildren"));
                    children = Cf.RetainArrayItems(visible.Value);
                }
            }

            // Cheap filters first: they need nothing beyond the batched read.
            // A type outside ControlType/ControlTypes is traversed for its children but costs no further calls.
            if (!typeFilter.Accepts(controlType)) return new WalkedElement(null, role, key, children);
            var frame = FrameOf(slots);
            var offscreen = IsOffscreen(frame, windowFrame);
            if (!query.IncludeOffscreen && offscreen) return new WalkedElement(null, role, key, children);

            var plan = AxCallPlan.For(role, controlType);
            var identity = ReadIdentity(slots, plan);
            var formatted = AxValueFormatter.Format(controlType, identity.RawValue, Cf.ToBool(slots[AttrSelected]));
            if (query.NameContains is { Length: > 0 } text
                && !identity.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                && !(identity.Identifier?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false))
                return new WalkedElement(null, role, key, children);
            // Unnamed structural containers are traversed, never materialized (no follow-up calls).
            if (!query.IncludeUnnamed && !AxControlFilter.KeepWhenUnnamedExcluded(controlType, identity.Name, formatted.Value, []))
                return new WalkedElement(null, role, key, children);

            var extras = ReadExtras(item.Element, plan);
            var info = BuildControl(slots, identity, controlType, formatted, extras, frame, offscreen, key, item.Depth, item.ParentKey, help: null);
            return new WalkedElement(new ControlRead(info, role, identity.Fingerprint), role, key, children);
        }
        finally
        {
            foreach (var s in slots) CF.Release(s);
        }
    }

    /// <summary>
    /// Asks Chromium/Electron (AXManualAccessibility) and other toolkits (AXEnhancedUserInterface) to build their full
    /// accessibility trees. Errors are ignored; native Cocoa apps simply do not support the attributes. Done once per
    /// app pid per process.
    /// </summary>
    private void EnableEnhancedAccessibility(int pid)
    {
        lock (s_enhancedGate)
        {
            if (!s_enhancedPids.Add(pid)) return;
        }
        var app = GetAppElement(pid);
        if (app == 0) return;
        var manual = SetBool(app, "AXManualAccessibility", true);
        SetBool(app, "AXEnhancedUserInterface", true);
        // Chromium builds the web tree asynchronously after AXManualAccessibility flips on.
        if (manual == AX.kAXErrorSuccess) Thread.Sleep(300);
    }

    /// <summary>The detail attribute names as a CFArray (owned; released in Dispose).</summary>
    private nint DetailAttributeArray =>
        _controlAttributeArray != 0 ? _controlAttributeArray : (_controlAttributeArray = Cf.CreateArray(s_detailAttributes.Select(n => _names[n]).ToArray()));

    /// <summary>The walk attribute names (detail plus AXChildren) as a CFArray (owned; released in Dispose).</summary>
    private nint WalkAttributeArray =>
        _walkAttributeArray != 0 ? _walkAttributeArray : (_walkAttributeArray = Cf.CreateArray(s_walkAttributes.Select(n => _names[n]).ToArray()));

    /// <summary>
    /// Copies <paramref name="names"/> for one element in one round trip; each returned slot is +1 or 0 and the caller
    /// releases them all. Slots that failed hold an AXValue(kAXValueTypeAXError), which the typed readers ignore.
    /// Returns null when the element is gone.
    /// </summary>
    private nint[]? CopyAttributes(nint element, string[] names, nint namesArray)
    {
        var slots = new nint[names.Length];
        _axCalls++;
        var error = AX.AXUIElementCopyMultipleAttributeValues(element, namesArray, 0, out var values);
        if (error == AX.kAXErrorSuccess && Cf.Is(values, Cf.ArrayType))
        {
            using var owned = new CfRef(values);
            var count = (int)Math.Min(CF.CFArrayGetCount(values), slots.Length);
            for (var i = 0; i < count; i++)
            {
                var v = CF.CFArrayGetValueAtIndex(values, i);
                slots[i] = v == 0 ? 0 : CF.CFRetain(v);
            }
            return slots;
        }
        CF.Release(values);
        if (error is AX.kAXErrorAPIDisabled or AX.kAXErrorCannotComplete) throw AxFailure(error, "Reading controls");
        if (error == AX.kAXErrorInvalidUIElement) return null;

        // Fall back to one call per attribute (some apps do not implement the batched call).
        var any = false;
        for (var i = 0; i < slots.Length; i++)
        {
            slots[i] = CopyAttribute(element, names[i], out var e);
            if (e is AX.kAXErrorCannotComplete or AX.kAXErrorAPIDisabled)
            {
                foreach (var s in slots) CF.Release(s);
                throw AxFailure(e, "Reading controls");
            }
            if (e == AX.kAXErrorInvalidUIElement)
            {
                foreach (var s in slots) CF.Release(s);
                return null;
            }
            any |= slots[i] != 0;
        }
        if (!any && ReadRoleIfAlive(element) is null) return null;
        return slots;
    }

    private static RectD? FrameOf(nint[] slots)
    {
        var position = Cf.ToPoint(slots[AttrPosition]);
        var size = Cf.ToSize(slots[AttrSize]);
        return position is { } p && size is { } s ? new RectD(p.X, p.Y, s.Width, s.Height) : null;
    }

    /// <summary>AX has no offscreen flag: an element is offscreen when it has no frame or lies outside its window.</summary>
    private static bool IsOffscreen(RectD? frame, RectD? windowFrame) =>
        frame is not { IsEmpty: false } f || (windowFrame is { IsEmpty: false } wf && !f.Intersects(wf));

    /// <summary>Only the follow-up calls the plan asks for.</summary>
    private ElementExtras ReadExtras(nint element, AxCallPlan plan) => new(
        plan.ActionNames ? ReadActionNames(element) : [],
        plan.ValueSettable && IsSettable(element, "AXValue"),
        plan.FocusedSettable && IsSettable(element, "AXFocused"),
        plan.SelectedSettable && IsSettable(element, "AXSelected"),
        plan.ExpandedSettable && IsSettable(element, "AXExpanded"));

    private static ControlInfo BuildControl(nint[] slots, ElementIdentity identity, string controlType, AxFormattedValue formatted,
        ElementExtras extras, RectD? frame, bool offscreen, string key, int depth, string? parentKey, string? help)
    {
        var (role, subrole) = (identity.Role, identity.Subrole);
        var hasValue = slots[AttrValue] != 0 && !IsAxErrorValue(slots[AttrValue]);
        var supported = AxActionMap.Build(
            extras.Actions,
            AxRoleMap.IsToggleLike(role, subrole, !string.IsNullOrEmpty(Cf.ToStringOrNull(slots[AttrMarkChar]))),
            extras.ValueSettable,
            extras.FocusedSettable,
            extras.SelectedSettable,
            extras.ExpandedSettable);
        return new ControlInfo(key, controlType, identity.Name, string.IsNullOrEmpty(identity.Identifier) ? null : identity.Identifier,
            subrole is null ? role : $"{role}:{subrole}", formatted.Value, Cf.ToBool(slots[AttrEnabled]) ?? true, offscreen,
            Cf.ToBool(slots[AttrFocused]) ?? false, frame?.ToScreenRect() ?? default, depth, supported)
        {
            HelpText = string.IsNullOrEmpty(help) ? null : help,
            ParentRuntimeKey = parentKey,
            IsToggled = formatted.IsToggled,
            IsSelected = formatted.IsSelected,
            IsReadOnly = AxReadOnlyRule.IsReadOnly(controlType, hasValue, extras.ValueSettable),
            IsPassword = subrole == "AXSecureTextField",
        };
    }

    /// <summary>
    /// Full read of one element (Inspect, RefreshControl, after an action): every follow-up call and the help text.
    /// Null when the element is gone.
    /// </summary>
    private ControlRead? ReadControl(nint element, int pid, long windowId, int depth, string? parentRole,
        string? parentKey, RectD? windowFrame, string key)
    {
        var slots = CopyAttributes(element, s_detailAttributes, DetailAttributeArray);
        if (slots is null) return null;
        try
        {
            var role = Cf.ToStringOrNull(slots[AttrRole]);
            var subrole = Cf.ToStringOrNull(slots[AttrSubrole]);
            var controlType = AxRoleMap.Map(role, subrole, parentRole);
            var identity = ReadIdentity(slots, AxCallPlan.For(role, controlType));
            var formatted = AxValueFormatter.Format(controlType, identity.RawValue, Cf.ToBool(slots[AttrSelected]));
            var frame = FrameOf(slots);
            var extras = ReadExtras(element, AxCallPlan.Full);
            var info = BuildControl(slots, identity, controlType, formatted, extras, frame, IsOffscreen(frame, windowFrame), key, depth,
                parentKey, ReadString(element, "AXHelp"));
            return new ControlRead(info, role, identity.Fingerprint);
        }
        finally
        {
            foreach (var s in slots) CF.Release(s);
        }
    }

    /// <summary>
    /// Role, subrole, computed name and identifier from copied attributes (slots stay owned by the caller). The title
    /// element (a separate label) is consulted only when nothing else names the element and the plan allows it, so the
    /// walk and later re-reads compute the same name and fingerprint.
    /// </summary>
    private ElementIdentity ReadIdentity(nint[] slots, AxCallPlan plan)
    {
        var role = Cf.ToStringOrNull(slots[AttrRole]);
        var subrole = Cf.ToStringOrNull(slots[AttrSubrole]);
        var title = Cf.ToStringOrNull(slots[AttrTitle]);
        var description = Cf.ToStringOrNull(slots[AttrDescription]);
        var rawValue = Cf.ToScalar(slots[AttrValue]);
        var identifier = Cf.ToStringOrNull(slots[AttrIdentifier]);

        var name = AxNaming.ChooseName(title, description, rawValue as string, role, null);
        var titleElement = slots[AttrTitleElement];
        if (name.Length == 0 && plan.TitleElement && Cf.Is(titleElement, Cf.AxElementType))
        {
            var titleElementText = ReadString(titleElement, "AXTitle");
            if (string.IsNullOrWhiteSpace(titleElementText)) titleElementText = ReadString(titleElement, "AXValue");
            name = AxNaming.ChooseName(title, description, rawValue as string, role, titleElementText);
        }
        return new ElementIdentity(role, subrole, name, identifier, rawValue);
    }

    /// <summary>The element's identity (one batched call, plus the title element when needed), or null when it is gone.</summary>
    private ElementIdentity? ReadIdentityOf(nint element, string? parentRole = null)
    {
        var slots = CopyAttributes(element, s_detailAttributes, DetailAttributeArray);
        if (slots is null) return null;
        try
        {
            var role = Cf.ToStringOrNull(slots[AttrRole]);
            if (role is null && slots[AttrRole] == 0) return null;
            var controlType = AxRoleMap.Map(role, Cf.ToStringOrNull(slots[AttrSubrole]), parentRole);
            return ReadIdentity(slots, AxCallPlan.For(role, controlType));
        }
        finally
        {
            foreach (var s in slots) CF.Release(s);
        }
    }

    private static bool IsAxErrorValue(nint cf) => Cf.Is(cf, Cf.AxValueType) && AX.AXValueGetType(cf) == AX.kAXValueTypeAXError;

    // ---------------------------------------------------------------- resolve by key

    /// <summary>A resolved element: +1 <see cref="Element"/> the caller releases.</summary>
    private readonly record struct ResolvedElement(nint Element, AxElementKey Key, string RuntimeKey, string? Role);

    private static AxElementKey ParseKey(string runtimeKey) =>
        AxRuntimeKey.TryParse(runtimeKey, out var key)
            ? key
            : throw new DesktopAutomationException($"'{runtimeKey}' is not a macOS control key. List the controls again.", FailureCategories.NotFound);

    private static void CheckWindow(long windowHandle, AxElementKey key, string runtimeKey)
    {
        if (windowHandle != 0 && key.WindowId != 0 && key.WindowId != windowHandle)
            throw new DesktopAutomationException(
                $"Control {runtimeKey} belongs to window {key.WindowId}, not window {windowHandle}.", FailureCategories.NotFound);
    }

    /// <summary>
    /// Finds the live element for a key, or null when it is gone. Index paths shift when siblings are inserted or
    /// removed, so the element found (by retained reference or by re-walking the path) must still carry the identity
    /// fingerprint recorded when the key was issued; a different element at the same path counts as gone.
    /// </summary>
    private ResolvedElement? Resolve(AxElementKey key, string runtimeKey)
    {
        if (!key.IsPath)
        {
            if (_referenceElements.TryGetValue(runtimeKey, out var reference))
            {
                if (ReadIdentityOf(reference.Element) is { } live && AxFingerprint.Matches(reference.Fingerprint, live.Fingerprint))
                    return new ResolvedElement(CF.CFRetain(reference.Element), key, runtimeKey, live.Role);
                CF.Release(reference.Element);
                _referenceElements.Remove(runtimeKey);
            }
            return null;
        }

        Dictionary<string, RetainedElement>? map = null;
        RetainedElement? retained = null;
        if (_controlMaps.TryGetValue(key.WindowId, out map) && map.TryGetValue(runtimeKey, out retained))
        {
            // One batched read both proves the element is alive and yields its fingerprint; no re-walk on this path.
            if (ReadIdentityOf(retained.Element) is { } live)
            {
                // Alive but no longer the element the key was issued for: the key is stale.
                if (!AxFingerprint.Matches(retained.Fingerprint, live.Fingerprint)) return null;
                return new ResolvedElement(CF.CFRetain(retained.Element), key, runtimeKey, live.Role);
            }
        }

        // The retained element died: re-walk the index path from the window element.
        var entry = ReadWindowEntry(key.WindowId);
        if (entry is null) return null;
        var axWindow = FindAxWindow(entry);
        if (axWindow == 0) return null;

        var current = CF.CFRetain(axWindow);
        var path = key.Path!;
        for (var level = 1; level < path.Count; level++)
        {
            var children = CopyChildren(current);
            CF.Release(current);
            current = 0;
            var index = path[level];
            for (var i = 0; i < children.Count; i++)
            {
                if (i == index) current = children[i];
                else CF.Release(children[i]);
            }
            if (current == 0) return null;
        }

        var identity = ReadIdentityOf(current);
        var role = identity?.Role;
        var fingerprint = identity?.Fingerprint;
        if (fingerprint is null || !AxFingerprint.Matches(retained?.Fingerprint, fingerprint))
        {
            CF.Release(current);
            return null;
        }

        map ??= _controlMaps.TryGetValue(key.WindowId, out var m) ? m : (_controlMaps[key.WindowId] = new(StringComparer.Ordinal));
        if (map.TryGetValue(runtimeKey, out var stale)) CF.Release(stale.Element);
        map[runtimeKey] = new RetainedElement(CF.CFRetain(current), role, retained?.Fingerprint ?? fingerprint);
        return new ResolvedElement(current, key, runtimeKey, role);
    }

    /// <summary>Reads a resolved element (does not release it).</summary>
    private ControlInfo? ReadResolved(ResolvedElement resolved, int? depthOverride = null)
    {
        var key = resolved.Key;
        RectD? windowFrame = null;
        if (key.WindowId != 0 && _axWindows.TryGetValue(key.WindowId, out var w)) windowFrame = ReadFrame(w.Element);
        // The parent's role only matters for rows (an AXRow inside an AXOutline is a tree item), so skip the two calls otherwise.
        string? parentRole = null;
        if (resolved.Role == "AXRow")
        {
            using var parent = new CfRef(CopyElementAttribute(resolved.Element, "AXParent"));
            if (!parent.IsNull) parentRole = ReadString(parent.Value, "AXRole");
        }
        var parentKey = key.Path is { } path ? AxRuntimeKey.ParentOf(key.Pid, key.WindowId, path) : null;
        return ReadControl(resolved.Element, key.Pid, key.WindowId, depthOverride ?? key.Depth, parentRole, parentKey, windowFrame,
            resolved.RuntimeKey)?.Info;
    }

    public ControlInfo? RefreshControl(long windowHandle, string runtimeKey)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            EnsureAccessibility();
            var key = ParseKey(runtimeKey);
            CheckWindow(windowHandle, key, runtimeKey);
            if (Resolve(key, runtimeKey) is not { } resolved) return null;
            try
            {
                return ReadResolved(resolved);
            }
            finally
            {
                CF.Release(resolved.Element);
            }
        }
    }

    // ---------------------------------------------------------------- hit test / focus

    public ControlInfo? GetControlAt(ScreenPoint point)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            EnsureAccessibility();
            var error = AX.AXUIElementCopyElementAtPosition(SystemWide, point.X, point.Y, out var element);
            if (error != AX.kAXErrorSuccess)
            {
                CF.Release(element);
                if (error == AX.kAXErrorAPIDisabled) throw AxFailure(error, "Reading the control under the point");
                return null;
            }
            using var owned = new CfRef(element);
            return DescribeLooseElement(owned.Value);
        }
    }

    public ControlInfo? GetFocusedControl()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            EnsureAccessibility();
            using var element = new CfRef(CopyElementAttribute(SystemWide, "AXFocusedUIElement"));
            return element.IsNull ? null : DescribeLooseElement(element.Value);
        }
    }

    /// <summary>
    /// Gives an element found by position or focus a runtime key: an index-path key under its window when the parent
    /// chain reaches the window, otherwise a reference key. Depth is -1 because no walk placed it.
    /// </summary>
    private ControlInfo? DescribeLooseElement(nint element)
    {
        if (AX.AXUIElementGetPid(element, out var pid) != AX.kAXErrorSuccess) return null;
        if (ReadIdentityOf(element) is not { } identity) return null;
        var role = identity.Role;
        var fingerprint = identity.Fingerprint;

        nint window = role == "AXWindow" ? CF.CFRetain(element) : CopyElementAttribute(element, "AXWindow");
        using var windowRef = new CfRef(window);
        uint windowId = 0;
        if (!windowRef.IsNull && AX.AXUIElementGetWindow(windowRef.Value, out var wid) == AX.kAXErrorSuccess) windowId = wid;

        string runtimeKey;
        AxElementKey key;
        var path = windowId != 0 ? ComputeIndexPath(element, windowRef.Value) : null;
        if (path is not null)
        {
            runtimeKey = AxRuntimeKey.Format(pid, windowId, path);
            key = new AxElementKey(pid, windowId, path, null);
            if (!_controlMaps.TryGetValue(windowId, out var map)) _controlMaps[windowId] = map = new(StringComparer.Ordinal);
            if (map.TryGetValue(runtimeKey, out var stale)) CF.Release(stale.Element);
            map[runtimeKey] = new RetainedElement(CF.CFRetain(element), role, fingerprint);
            if (!_axWindows.ContainsKey(windowId)) CacheAxWindow(windowId, pid, CF.CFRetain(windowRef.Value));
        }
        else
        {
            var serial = ++_referenceSerial;
            runtimeKey = AxRuntimeKey.FormatReference(pid, windowId, serial);
            key = new AxElementKey(pid, windowId, null, serial);
            _referenceElements[runtimeKey] = new RetainedElement(CF.CFRetain(element), role, fingerprint);
        }

        return ReadResolved(new ResolvedElement(element, key, runtimeKey, role), depthOverride: -1);
    }

    /// <summary>Index path from <paramref name="window"/> down to <paramref name="element"/> via AXParent; null when the chain does not reach the window.</summary>
    private int[]? ComputeIndexPath(nint element, nint window)
    {
        if (CF.CFEqual(element, window)) return [0];
        var reversed = new List<int>();
        var current = CF.CFRetain(element);
        try
        {
            // Guards against parent cycles in misbehaving apps; real trees are far shallower.
            for (var guard = 0; guard < 65536; guard++)
            {
                var parent = CopyElementAttribute(current, "AXParent");
                if (parent == 0) return null;
                var children = CopyChildren(parent);
                var index = -1;
                for (var i = 0; i < children.Count; i++)
                {
                    if (index < 0 && CF.CFEqual(children[i], current)) index = i;
                    CF.Release(children[i]);
                }
                CF.Release(current);
                current = parent;
                if (index < 0) return null;
                reversed.Add(index);
                if (CF.CFEqual(current, window))
                {
                    reversed.Add(0);
                    reversed.Reverse();
                    return [.. reversed];
                }
            }
            return null;
        }
        finally
        {
            CF.Release(current);
        }
    }

    // ---------------------------------------------------------------- actions

    public ControlActionResult PerformAction(long windowHandle, string runtimeKey, ControlAction action, string? value = null)
    {
        if (action == ControlAction.SetValue && value is null)
            throw new ArgumentNullException(nameof(value), "SetValue needs a value.");
        lock (_gate)
        {
            ThrowIfDisposed();
            EnsureAccessibility();
            var key = ParseKey(runtimeKey);
            CheckWindow(windowHandle, key, runtimeKey);
            if (key.WindowId != 0) RequireWindowEntry(key.WindowId);
            if (Resolve(key, runtimeKey) is not { } resolved)
                throw new DesktopAutomationException($"Control {runtimeKey} no longer exists. List the controls again.", FailureCategories.ControlGone);

            try
            {
                var element = resolved.Element;
                var (succeeded, method, message) = Execute(element, action, value);
                ControlInfo? after = null;
                if (succeeded) Thread.Sleep(50);
                if (ReadRoleIfAlive(element) is not null) after = ReadResolved(resolved);
                return new ControlActionResult(succeeded, method, message, after);
            }
            finally
            {
                CF.Release(resolved.Element);
            }
        }
    }

    private (bool Succeeded, string Method, string? Message) Execute(nint element, ControlAction action, string? value)
    {
        var actions = ReadActionNames(element);
        switch (action)
        {
            case ControlAction.Invoke:
            case ControlAction.Toggle:
            {
                string[] candidates = action == ControlAction.Invoke
                    ? [AxActionMap.Press, AxActionMap.Confirm, AxActionMap.Pick]
                    : [AxActionMap.Press];
                foreach (var name in candidates.Where(actions.Contains))
                {
                    var error = Perform(element, name);
                    if (error == AX.kAXErrorSuccess) return (true, "ax-press", name == AxActionMap.Press ? null : $"performed {name}");
                    if (!IsUnsupported(error)) throw ActionFailure(error, name);
                }
                return Unsupported(element, action, actions);
            }
            case ControlAction.Select:
            {
                if (IsSettable(element, "AXSelected"))
                {
                    var error = SetBool(element, "AXSelected", true);
                    if (error == AX.kAXErrorSuccess) return (true, "ax-set-selected", null);
                    if (!IsUnsupported(error)) throw ActionFailure(error, "AXSelected");
                }
                if (actions.Contains(AxActionMap.Press))
                {
                    var error = Perform(element, AxActionMap.Press);
                    if (error == AX.kAXErrorSuccess) return (true, "ax-press", null);
                    if (!IsUnsupported(error)) throw ActionFailure(error, AxActionMap.Press);
                }
                return Unsupported(element, action, actions);
            }
            case ControlAction.Expand:
            case ControlAction.Collapse:
            {
                var expand = action == ControlAction.Expand;
                if (IsSettable(element, "AXExpanded"))
                {
                    var error = SetBool(element, "AXExpanded", expand);
                    if (error == AX.kAXErrorSuccess) return (true, "ax-set-expanded", null);
                    if (!IsUnsupported(error)) throw ActionFailure(error, "AXExpanded");
                }
                if (expand && actions.Contains(AxActionMap.ShowMenu))
                {
                    var error = Perform(element, AxActionMap.ShowMenu);
                    if (error == AX.kAXErrorSuccess) return (true, "ax-show-menu", null);
                    if (!IsUnsupported(error)) throw ActionFailure(error, AxActionMap.ShowMenu);
                }
                return Unsupported(element, action, actions);
            }
            case ControlAction.SetValue:
            {
                if (!IsSettable(element, "AXValue")) return Unsupported(element, action, actions);
                using var text = new CfRef(Cf.CreateString(value!));
                var error = AX.AXUIElementSetAttributeValue(element, _names["AXValue"], text.Value);
                if (error == AX.kAXErrorSuccess) return (true, "ax-set-value", null);
                if (IsUnsupported(error)) return Unsupported(element, action, actions);
                throw ActionFailure(error, "AXValue");
            }
            case ControlAction.Focus:
            {
                if (!IsSettable(element, "AXFocused")) return Unsupported(element, action, actions);
                var error = SetBool(element, "AXFocused", true);
                if (error == AX.kAXErrorSuccess) return (true, "ax-focus", null);
                if (IsUnsupported(error)) return Unsupported(element, action, actions);
                throw ActionFailure(error, "AXFocused");
            }
            case ControlAction.ScrollIntoView:
            {
                if (!actions.Contains(AxActionMap.ScrollToVisible)) return Unsupported(element, action, actions);
                var error = Perform(element, AxActionMap.ScrollToVisible);
                if (error == AX.kAXErrorSuccess) return (true, "ax-scroll-to-visible", null);
                if (IsUnsupported(error)) return Unsupported(element, action, actions);
                throw ActionFailure(error, AxActionMap.ScrollToVisible);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }
    }

    private static DesktopAutomationException ActionFailure(int error, string what) =>
        error == AX.kAXErrorCannotComplete
            ? new DesktopAutomationException(
                $"{what} did not complete: the application did not respond in time (it may be busy or showing a modal dialog). The action may still have taken effect; check before retrying.",
                FailureCategories.AppBusy)
            : AxFailure(error, what);

    private (bool, string, string?) Unsupported(nint element, ControlAction action, List<string> actions)
    {
        var supported = AxActionMap.Build(actions, false, IsSettable(element, "AXValue"), IsSettable(element, "AXFocused"),
            IsSettable(element, "AXSelected"), IsSettable(element, "AXExpanded"));
        var list = supported.Count == 0 ? "no accessibility actions" : string.Join(", ", supported);
        var raw = actions.Count == 0 ? "none" : string.Join(", ", actions);
        return (false, "unsupported", $"The control does not support {action} through Accessibility. It supports: {list} (AX actions: {raw}).");
    }
}
