using System.Globalization;
using System.Text;

namespace SwarmCore.RemoteControl.Mac.Mapping;

/// <summary>A rectangle in macOS global points (doubles, origin top-left of the main display).</summary>
internal readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool IsEmpty => !(Width > 0) || !(Height > 0) || double.IsInfinity(X) || double.IsInfinity(Y);

    public bool Intersects(RectD other) =>
        !IsEmpty && !other.IsEmpty && X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;

    public RectD Union(RectD other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        var left = Math.Min(X, other.X);
        var top = Math.Min(Y, other.Y);
        return new RectD(left, top, Math.Max(Right, other.Right) - left, Math.Max(Bottom, other.Bottom) - top);
    }

    /// <summary>Rounds edges outward-consistently (round each edge, then derive size) so adjacent rects stay adjacent.</summary>
    public ScreenRect ToScreenRect()
    {
        if (double.IsInfinity(X) || double.IsInfinity(Y) || double.IsNaN(X) || double.IsNaN(Y)) return default;
        var left = (int)Math.Round(X, MidpointRounding.AwayFromZero);
        var top = (int)Math.Round(Y, MidpointRounding.AwayFromZero);
        var right = (int)Math.Round(Right, MidpointRounding.AwayFromZero);
        var bottom = (int)Math.Round(Bottom, MidpointRounding.AwayFromZero);
        return ScreenRect.FromEdges(left, top, right, bottom);
    }

    public static RectD FromScreenRect(ScreenRect r) => new(r.Left, r.Top, r.Width, r.Height);
}

/// <summary>Formatted AX value plus derived toggle/selection state.</summary>
internal readonly record struct AxFormattedValue(string? Value, bool? IsToggled, bool? IsSelected);

/// <summary>Turns a raw AX value (string, double, bool, or null) into <see cref="ControlInfo.Value"/>. Pure.</summary>
internal static class AxValueFormatter
{
    public static string FormatNumber(double d) =>
        d == Math.Floor(d) && Math.Abs(d) < 1e15
            ? ((long)d).ToString(CultureInfo.InvariantCulture)
            : d.ToString("G15", CultureInfo.InvariantCulture);

    /// <param name="controlType">Mapped control type (see <see cref="AxRoleMap"/>).</param>
    /// <param name="raw">kAXValueAttribute as string/double/bool, or null.</param>
    /// <param name="axSelected">kAXSelectedAttribute, when the element exposes it.</param>
    public static AxFormattedValue Format(string controlType, object? raw, bool? axSelected)
    {
        double? number = raw switch
        {
            double d => d,
            bool b => b ? 1 : 0,
            int i => i,
            long l => l,
            _ => null,
        };

        switch (controlType)
        {
            case "checkbox":
            case "radiobutton":
            {
                bool? toggled = number switch { 0 => false, 1 => true, _ => null };
                var text = number switch { 0 => "off", 1 => "on", 2 => "indeterminate", _ => raw as string };
                bool? selected = controlType == "radiobutton" ? toggled : axSelected;
                return new AxFormattedValue(text, toggled, selected);
            }
            case "tabitem":
            {
                var selected = axSelected ?? (number is null ? null : number == 1);
                return new AxFormattedValue(selected == true ? "selected" : null, null, selected);
            }
            case "listitem":
            case "treeitem":
            case "dataitem":
            case "menuitem":
            {
                if (axSelected == true) return new AxFormattedValue("selected", null, true);
                var text = raw switch
                {
                    string s when s.Length > 0 => s,
                    double d => FormatNumber(d),
                    _ => null,
                };
                return new AxFormattedValue(text, null, axSelected);
            }
            case "slider":
            case "spinner":
            case "progressbar":
            case "scrollbar":
                return new AxFormattedValue(number is { } n ? FormatNumber(n) : raw as string, null, axSelected);
            case "edit":
            case "combobox":
            case "document":
                // An empty text box is meaningfully empty, not valueless.
                return new AxFormattedValue(raw switch
                {
                    string s => s,
                    double d => FormatNumber(d),
                    _ => null,
                }, null, axSelected);
        }

        var value = raw switch
        {
            string s when s.Length > 0 => s,
            double d => FormatNumber(d),
            bool b => b ? "on" : "off",
            _ => null,
        };
        if (value is null && axSelected == true) value = "selected";
        return new AxFormattedValue(value, null, axSelected);
    }
}

/// <summary>Chooses <see cref="ControlInfo.Name"/> from AX attributes. Pure.</summary>
internal static class AxNaming
{
    /// <summary>Roles whose visible text lives in kAXValueAttribute.</summary>
    public static bool UsesValueAsName(string? role) => role is "AXStaticText" or "AXLink" or "AXHeading";

    public static string ChooseName(string? title, string? description, string? stringValue, string? role, string? titleElementText)
    {
        if (!string.IsNullOrWhiteSpace(title)) return title;
        if (!string.IsNullOrWhiteSpace(description)) return description;
        if (UsesValueAsName(role) && !string.IsNullOrWhiteSpace(stringValue)) return stringValue;
        if (!string.IsNullOrWhiteSpace(titleElementText)) return titleElementText;
        return string.Empty;
    }
}

/// <summary>Supported <see cref="ControlAction"/>s from AX action names and attribute settability. Pure.</summary>
internal static class AxActionMap
{
    public const string Press = "AXPress";
    public const string Confirm = "AXConfirm";
    public const string Pick = "AXPick";
    public const string ShowMenu = "AXShowMenu";
    public const string ScrollToVisible = "AXScrollToVisible";
    public const string Raise = "AXRaise";

    public static IReadOnlyList<ControlAction> Build(
        IReadOnlyCollection<string> actionNames,
        bool toggleLike,
        bool valueSettable,
        bool focusedSettable,
        bool selectedSettable,
        bool expandedSettable)
    {
        var set = new SortedSet<ControlAction>();
        var press = actionNames.Contains(Press);
        if (press || actionNames.Contains(Confirm) || actionNames.Contains(Pick)) set.Add(ControlAction.Invoke);
        if (press && toggleLike) set.Add(ControlAction.Toggle);
        if (selectedSettable) set.Add(ControlAction.Select);
        // AXShowMenu opens a menu but cannot close one, so it only implies Expand.
        if (expandedSettable || actionNames.Contains(ShowMenu)) set.Add(ControlAction.Expand);
        if (expandedSettable) set.Add(ControlAction.Collapse);
        if (valueSettable) set.Add(ControlAction.SetValue);
        if (focusedSettable) set.Add(ControlAction.Focus);
        if (actionNames.Contains(ScrollToVisible)) set.Add(ControlAction.ScrollIntoView);
        return [.. set];
    }
}

/// <summary>
/// Decoded runtime key. AX elements have no runtime id, so path keys are
/// <c>ax:{pid}:{windowId}:{indexPath}</c> where the index path starts at the window element ("0") and appends the
/// child index at each level (<c>ax:4242:1183:0.3.2.1</c>). Elements found by position/focus that cannot be placed
/// under a window get a reference key <c>axref:{pid}:{windowId}:{serial}</c> valid for this automation object.
/// </summary>
internal sealed record AxElementKey(int Pid, long WindowId, IReadOnlyList<int>? Path, long? Serial)
{
    public bool IsPath => Path is not null;
    public int Depth => Path is null ? -1 : Path.Count - 1;
}

/// <summary>Formats and parses <see cref="AxElementKey"/>. Pure.</summary>
internal static class AxRuntimeKey
{
    public static string Format(int pid, long windowId, IReadOnlyList<int> path)
    {
        if (path.Count == 0) throw new ArgumentException("An index path needs at least the window element.", nameof(path));
        var sb = new StringBuilder("ax:");
        sb.Append(pid.ToString(CultureInfo.InvariantCulture)).Append(':')
          .Append(windowId.ToString(CultureInfo.InvariantCulture)).Append(':');
        for (var i = 0; i < path.Count; i++)
        {
            if (i > 0) sb.Append('.');
            sb.Append(path[i].ToString(CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    public static string FormatReference(int pid, long windowId, long serial) =>
        string.Create(CultureInfo.InvariantCulture, $"axref:{pid}:{windowId}:{serial}");

    /// <summary>The parent's key, or null for the window element.</summary>
    public static string? ParentOf(int pid, long windowId, IReadOnlyList<int> path) =>
        path.Count <= 1 ? null : Format(pid, windowId, path.Take(path.Count - 1).ToArray());

    public static bool TryParse(string? key, out AxElementKey parsed)
    {
        parsed = null!;
        if (string.IsNullOrWhiteSpace(key)) return false;
        var parts = key.Trim().Split(':');
        if (parts.Length != 4) return false;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)) return false;
        if (!long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var windowId)) return false;

        switch (parts[0])
        {
            case "ax":
            {
                var segments = parts[3].Split('.');
                var path = new int[segments.Length];
                for (var i = 0; i < segments.Length; i++)
                {
                    if (!int.TryParse(segments[i], NumberStyles.None, CultureInfo.InvariantCulture, out path[i])) return false;
                }
                if (path[0] != 0) return false;
                parsed = new AxElementKey(pid, windowId, path, null);
                return true;
            }
            case "axref":
                if (!long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var serial)) return false;
                parsed = new AxElementKey(pid, windowId, null, serial);
                return true;
            default:
                return false;
        }
    }
}

/// <summary>Matches AX windows to CG windows by bounds when <c>_AXUIElementGetWindow</c> is unavailable. Pure.</summary>
internal static class AxWindowMatcher
{
    public const double DefaultTolerance = 2.0;

    /// <summary>Index of the candidate whose rect matches <paramref name="target"/> within <paramref name="tolerance"/> points on every edge (closest wins), or -1.</summary>
    public static int FindBestByBounds(IReadOnlyList<RectD?> candidates, RectD target, double tolerance = DefaultTolerance)
    {
        var best = -1;
        var bestScore = double.MaxValue;
        for (var i = 0; i < candidates.Count; i++)
        {
            if (candidates[i] is not { } c) continue;
            var dx = Math.Abs(c.X - target.X);
            var dy = Math.Abs(c.Y - target.Y);
            var dw = Math.Abs(c.Width - target.Width);
            var dh = Math.Abs(c.Height - target.Height);
            if (dx > tolerance || dy > tolerance || dw > tolerance || dh > tolerance) continue;
            var score = dx + dy + dw + dh;
            if (score < bestScore)
            {
                bestScore = score;
                best = i;
            }
        }
        return best;
    }
}

/// <summary>Display ordering and lookup. Pure.</summary>
internal static class DisplayLayout
{
    /// <summary>Main display first, then the rest by left edge, then top edge.</summary>
    public static IReadOnlyList<(uint Id, RectD Bounds)> Order(IEnumerable<(uint Id, RectD Bounds)> displays, uint mainId) =>
        displays
            .OrderBy(d => d.Id == mainId ? 0 : 1)
            .ThenBy(d => d.Bounds.X)
            .ThenBy(d => d.Bounds.Y)
            .ThenBy(d => d.Id)
            .ToList();

    /// <summary>1-based index of the display containing the rect's center; nearest display when the center is off every display; null when there are none.</summary>
    public static int? DisplayIndexFor(ScreenRect bounds, IReadOnlyList<DisplayInfo> displays)
    {
        if (displays.Count == 0) return null;
        var c = bounds.Center;
        foreach (var d in displays)
        {
            if (d.Bounds.Contains(c)) return d.Index;
        }
        DisplayInfo? nearest = null;
        var best = double.MaxValue;
        foreach (var d in displays)
        {
            var dx = Math.Max(Math.Max(d.Bounds.Left - c.X, 0), c.X - (d.Bounds.Right - 1));
            var dy = Math.Max(Math.Max(d.Bounds.Top - c.Y, 0), c.Y - (d.Bounds.Bottom - 1));
            var dist = (double)dx * dx + (double)dy * dy;
            if (dist < best)
            {
                best = dist;
                nearest = d;
            }
        }
        return nearest?.Index;
    }
}

/// <summary>
/// Decides which controls a default listing (<see cref="ControlQuery.IncludeUnnamed"/> = false) shows. Pure.
/// Interactive controls are always kept, even nameless and empty (a new TextEdit document's AXTextArea has
/// neither); only structural elements without a name or value are dropped.
/// </summary>
internal static class AxControlFilter
{
    private static readonly HashSet<string> s_interactiveTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "edit", "document", "combobox", "button", "menubutton", "splitbutton", "checkbox", "radiobutton",
        "menuitem", "tabitem", "listitem", "dataitem", "treeitem", "slider", "spinner", "hyperlink",
    };

    public static bool IsInteractive(string controlType, IReadOnlyCollection<ControlAction> supportedActions) =>
        s_interactiveTypes.Contains(controlType)
        || supportedActions.Contains(ControlAction.SetValue)
        || supportedActions.Contains(ControlAction.Invoke);

    /// <summary>True when the control belongs in a listing that excludes unnamed structural elements.</summary>
    public static bool KeepWhenUnnamedExcluded(string controlType, string? name, string? value, IReadOnlyCollection<ControlAction> supportedActions) =>
        IsInteractive(controlType, supportedActions)
        || !string.IsNullOrWhiteSpace(name)
        || !string.IsNullOrEmpty(value);

    public static bool KeepWhenUnnamedExcluded(ControlInfo info) =>
        KeepWhenUnnamedExcluded(info.ControlType, info.Name, info.Value, info.SupportedActions);
}

/// <summary>
/// Separates the person's input from input this process injected. macOS's HID idle counter
/// (CGEventSourceSecondsSinceLastEventType) also counts our own CGEventPost events, so every injection is recorded
/// and a last event within <see cref="InjectionTolerance"/> of one is treated as ours; the idle time is then the age
/// of the last event observed that was not ours. Times are monotonic seconds supplied by the caller. Pure and thread-safe.
/// </summary>
internal sealed class UserIdleTracker
{
    public const double InjectionTolerance = 0.050;
    private const int RecentInjections = 256;

    private readonly object _gate = new();
    private readonly Queue<double> _injections = new();
    private double? _lastUserEvent;

    /// <summary>Records that this process posted an event at <paramref name="now"/>.</summary>
    public void RecordInjection(double now)
    {
        lock (_gate)
        {
            _injections.Enqueue(now);
            while (_injections.Count > RecentInjections) _injections.Dequeue();
        }
    }

    /// <summary>
    /// Folds in an idle-counter reading (<paramref name="secondsSinceLastEvent"/> taken at <paramref name="now"/>) and
    /// returns how long the person has been idle, or null when no input of theirs has been observed yet and the latest
    /// event is ours.
    /// </summary>
    public TimeSpan? Observe(double now, double secondsSinceLastEvent)
    {
        if (double.IsNaN(secondsSinceLastEvent) || secondsSinceLastEvent < 0) return null;
        var lastEvent = now - secondsSinceLastEvent;
        lock (_gate)
        {
            var ours = false;
            foreach (var t in _injections)
            {
                if (Math.Abs(lastEvent - t) <= InjectionTolerance)
                {
                    ours = true;
                    break;
                }
            }
            if (!ours && (_lastUserEvent is null || lastEvent > _lastUserEvent)) _lastUserEvent = lastEvent;
            if (_lastUserEvent is not { } user)
            {
                // Nothing of the person's seen yet: before our first injection everything was theirs.
                if (_injections.Count == 0) return TimeSpan.FromSeconds(secondsSinceLastEvent);
                return null;
            }
            return TimeSpan.FromSeconds(Math.Max(0, now - user));
        }
    }
}

/// <summary>
/// Identity fingerprint stored with each runtime key (role, subrole, name, identifier). Index-path keys shift when
/// siblings are inserted or removed, so an element found at a key's path must still match its fingerprint. Pure.
/// </summary>
internal static class AxFingerprint
{
    private const char Separator = '\u001f';

    public static string Create(string? role, string? subrole, string? name, string? identifier) =>
        string.Join(Separator, role ?? "", subrole ?? "", name ?? "", identifier ?? "");

    /// <summary>True when <paramref name="actual"/> is the element <paramref name="expected"/> described. No expectation (null) accepts any live element; a gone element (null actual) never matches.</summary>
    public static bool Matches(string? expected, string? actual) =>
        actual is not null && (expected is null || string.Equals(expected, actual, StringComparison.Ordinal));
}

/// <summary>
/// Which follow-up AX calls an element is worth after its one batched attribute read. Every flag is one IPC round trip
/// to the target app, so they are spent only where the answer can change what the control supports: structural and
/// display elements (text, images, groups, panes, web areas, unknown roles) get none. Pure.
/// </summary>
internal readonly record struct AxCallPlan(bool ValueSettable, bool SelectedSettable, bool ExpandedSettable, bool FocusedSettable,
    bool ActionNames, bool TitleElement)
{
    /// <summary>Calls beyond the batched read (the title element lookup is extra and conditional, so not counted).</summary>
    public int ExtraCalls => (ValueSettable ? 1 : 0) + (SelectedSettable ? 1 : 0) + (ExpandedSettable ? 1 : 0) + (FocusedSettable ? 1 : 0) + (ActionNames ? 1 : 0);

    /// <summary>Everything, for single-element reads (Inspect, RefreshControl, after an action).</summary>
    public static AxCallPlan Full { get; } = new(true, true, true, true, true, true);

    private static readonly HashSet<string> s_value = new(StringComparer.Ordinal) { "edit", "document", "combobox", "slider", "spinner", "checkbox", "radiobutton" };
    private static readonly HashSet<string> s_selected = new(StringComparer.Ordinal) { "listitem", "treeitem", "dataitem", "tabitem" };
    private static readonly HashSet<string> s_expanded = new(StringComparer.Ordinal) { "treeitem", "combobox", "menubutton" };
    private static readonly HashSet<string> s_focused = new(StringComparer.Ordinal)
    {
        "edit", "document", "combobox", "button", "menubutton", "checkbox", "radiobutton", "slider", "spinner", "hyperlink",
        "listitem", "treeitem", "dataitem", "tabitem", "list", "table", "tree",
    };
    private static readonly HashSet<string> s_actions = new(StringComparer.Ordinal)
    {
        "button", "menubutton", "hyperlink", "menuitem", "checkbox", "radiobutton", "tabitem", "listitem", "dataitem", "treeitem",
        "combobox", "slider", "spinner", "edit",
    };
    private static readonly HashSet<string> s_titleElement = new(StringComparer.Ordinal) { "edit", "combobox", "slider", "spinner", "checkbox", "radiobutton" };

    /// <summary>The plan for a listed element of this role and mapped control type.</summary>
    public static AxCallPlan For(string? role, string controlType)
    {
        // A web area maps to "document" but is a container: its AXValue is not editable and it has no useful actions.
        var webArea = role == "AXWebArea";
        var disclosure = role == "AXDisclosureTriangle";
        return new AxCallPlan(
            ValueSettable: !webArea && s_value.Contains(controlType),
            SelectedSettable: s_selected.Contains(controlType),
            ExpandedSettable: disclosure || s_expanded.Contains(controlType),
            FocusedSettable: !webArea && s_focused.Contains(controlType),
            ActionNames: !webArea && s_actions.Contains(controlType),
            TitleElement: s_titleElement.Contains(controlType));
    }

    /// <summary>Roles whose AXChildren may be absent while AXVisibleChildren is not (worth one fallback call).</summary>
    public static bool UsesVisibleChildrenFallback(string? role) => role is "AXTable" or "AXOutline" or "AXList" or "AXGrid" or "AXBrowser";
}

/// <summary>
/// The walk's type filter: <see cref="ControlQuery.ControlType"/> and <see cref="ControlQuery.ControlTypes"/> (a set),
/// case-insensitive. When both are given an element must satisfy both. No filter accepts everything. Pure.
/// </summary>
internal sealed class AxTypeFilter
{
    private readonly string? _single;
    private readonly HashSet<string>? _set;

    private AxTypeFilter(string? single, HashSet<string>? set)
    {
        _single = single;
        _set = set;
    }

    public bool IsActive => _single is not null || _set is not null;

    public static AxTypeFilter Create(string? controlType, IReadOnlyList<string>? controlTypes)
    {
        var single = string.IsNullOrWhiteSpace(controlType) ? null : controlType.Trim();
        HashSet<string>? set = null;
        if (controlTypes is not null)
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in controlTypes)
            {
                if (!string.IsNullOrWhiteSpace(t)) set.Add(t.Trim());
            }
        }
        return new AxTypeFilter(single, set);
    }

    public bool Accepts(string controlType) =>
        (_single is null || string.Equals(controlType, _single, StringComparison.OrdinalIgnoreCase))
        && (_set is null || _set.Contains(controlType));
}

/// <summary>When a control counts as read-only. Pure.</summary>
internal static class AxReadOnlyRule
{
    /// <summary>
    /// Only text-entry and choice controls (edit, document, combobox family) can be read-only; a checkbox, radio, or
    /// button whose AXValue is not settable is simply not a value control.
    /// </summary>
    public static bool IsReadOnly(string controlType, bool hasValue, bool valueSettable) =>
        controlType is "edit" or "document" or "combobox" && hasValue && !valueSettable;
}

/// <summary>Which CGWindowList entries count as user windows. Pure.</summary>
internal static class CgWindowFilter
{
    public static bool IsCandidate(long? layer, double? alpha, RectD? bounds, string? ownerName) =>
        layer == 0
        && (alpha ?? 1) > 0
        && bounds is { IsEmpty: false }
        && !string.IsNullOrEmpty(ownerName);
}

/// <summary>One step of <see cref="MacTextChunker"/>: either literal text or a virtual key to press.</summary>
internal readonly record struct TypingSegment(string? Text, ushort? KeyCode);

/// <summary>Splits text for CGEventKeyboardSetUnicodeString: chunks of at most 20 UTF-16 units (never splitting a surrogate pair), with Return/Tab as real keys. Pure.</summary>
internal static class MacTextChunker
{
    public const int MaxChunk = 20;

    public static IReadOnlyList<TypingSegment> Split(string text)
    {
        var result = new List<TypingSegment>();
        var sb = new StringBuilder();

        void Flush()
        {
            if (sb.Length > 0)
            {
                result.Add(new TypingSegment(sb.ToString(), null));
                sb.Clear();
            }
        }

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '\r' || ch == '\n')
            {
                Flush();
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                result.Add(new TypingSegment(null, MacKeyMap.KeyReturn));
                continue;
            }
            if (ch == '\t')
            {
                Flush();
                result.Add(new TypingSegment(null, MacKeyMap.KeyTab));
                continue;
            }

            var unitLength = char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            if (sb.Length + unitLength > MaxChunk) Flush();
            sb.Append(ch);
            if (unitLength == 2) sb.Append(text[++i]);
        }
        Flush();
        return result;
    }
}

/// <summary>User-facing permission and failure messages.</summary>
internal static class MacMessages
{
    public const string AccessibilityHint =
        "macOS has not granted Accessibility permission to this process. Open System Settings > Privacy & Security > Accessibility, " +
        "turn on the app that runs this command (Terminal, iTerm, your IDE, or the Talavine app), then quit and reopen it. " +
        "MacDesktopAutomation.RequestAccessibility() shows the system prompt.";

    public const string ScreenRecordingHint =
        "macOS has not granted Screen Recording permission to this process. Open System Settings > Privacy & Security > Screen & System Audio Recording " +
        "(Screen Recording on older macOS), turn on the app that runs this command (Terminal, iTerm, your IDE, or the Talavine app), then quit and reopen it. " +
        "MacDesktopAutomation.RequestScreenRecordingAccess() shows the system prompt.";

    public const string LockedHint =
        "The Mac is locked (the login window or screen saver owns the display). A person must unlock it before the desktop can be read or controlled.";
}
