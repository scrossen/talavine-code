namespace SwarmCore.RemoteControl.Mac.Mapping;

[Flags]
internal enum MacModifiers
{
    None = 0,
    Control = 1,
    Option = 2,
    Shift = 4,
    Command = 8,
}

/// <summary>A parsed chord: modifiers plus an optional main key (null for a modifier-only chord such as "win").</summary>
internal sealed record MacKeyChord(MacModifiers Modifiers, ushort? KeyCode, string? KeyName);

/// <summary>
/// Key names to macOS virtual key codes (Carbon <c>kVK_*</c>, ANSI layout) and modifier flags. Pure: no native calls.
/// <para>Chords are <c>+</c>-joined and case-insensitive ("ctrl+shift+t"). <c>ctrl</c> is Control, <c>alt</c>/<c>option</c>
/// is Option, <c>shift</c> is Shift, and <c>win</c>/<c>cmd</c>/<c>command</c> are Command: the Windows key means Command on
/// macOS, so "win+space" opens Spotlight the way it opens Start on Windows.</para>
/// </summary>
internal static class MacKeyMap
{
    // kCGEventFlagMask* (CGEventFlags, uint64)
    public const ulong FlagMaskAlphaShift = 0x0001_0000;
    public const ulong FlagMaskShift = 0x0002_0000;
    public const ulong FlagMaskControl = 0x0004_0000;
    public const ulong FlagMaskAlternate = 0x0008_0000;
    public const ulong FlagMaskCommand = 0x0010_0000;

    // Modifier key codes
    public const ushort KeyCommand = 55;
    public const ushort KeyShift = 56;
    public const ushort KeyOption = 58;
    public const ushort KeyControl = 59;

    public const ushort KeyReturn = 36;
    public const ushort KeyTab = 48;

    private static readonly Dictionary<string, MacModifiers> s_modifiers = new(StringComparer.Ordinal)
    {
        ["ctrl"] = MacModifiers.Control,
        ["control"] = MacModifiers.Control,
        ["alt"] = MacModifiers.Option,
        ["option"] = MacModifiers.Option,
        ["opt"] = MacModifiers.Option,
        ["shift"] = MacModifiers.Shift,
        ["win"] = MacModifiers.Command,
        ["windows"] = MacModifiers.Command,
        ["cmd"] = MacModifiers.Command,
        ["command"] = MacModifiers.Command,
        // The platform's primary command modifier: Command on macOS (Control on Windows).
        ["primary"] = MacModifiers.Command,
        ["mod"] = MacModifiers.Command,
        ["cmdorctrl"] = MacModifiers.Command,
    };

    private static readonly Dictionary<string, ushort> s_keys = BuildKeys();

    private static readonly Dictionary<string, string> s_unavailable = new(StringComparer.Ordinal)
    {
        ["printscreen"] = "macOS has no Print Screen key; use the screenshot command (or cmd+shift+3) instead",
        ["prtsc"] = "macOS has no Print Screen key; use the screenshot command (or cmd+shift+3) instead",
        ["pause"] = "macOS has no Pause/Break key",
        ["break"] = "macOS has no Pause/Break key",
        ["numlock"] = "macOS has no Num Lock key",
        ["scrolllock"] = "macOS has no Scroll Lock key",
        ["apps"] = "macOS has no Menu (apps) key; use a right-click instead",
        ["menu"] = "macOS has no Menu (apps) key; use a right-click instead",
    };

    private static Dictionary<string, ushort> BuildKeys()
    {
        var k = new Dictionary<string, ushort>(StringComparer.Ordinal);
        // Letters (kVK_ANSI_*).
        ushort[] letters = [0, 11, 8, 2, 14, 3, 5, 4, 34, 38, 40, 37, 46, 45, 31, 35, 12, 15, 1, 17, 32, 9, 13, 7, 16, 6];
        for (var i = 0; i < 26; i++) k[((char)('a' + i)).ToString()] = letters[i];
        // Digits 0-9.
        ushort[] digits = [29, 18, 19, 20, 21, 23, 22, 26, 28, 25];
        for (var i = 0; i < 10; i++) k[i.ToString(System.Globalization.CultureInfo.InvariantCulture)] = digits[i];
        // Function keys f1-f20.
        ushort[] fkeys = [122, 120, 99, 118, 96, 97, 98, 100, 101, 109, 103, 111, 105, 107, 113, 106, 64, 79, 80, 90];
        for (var i = 0; i < 20; i++) k["f" + (i + 1)] = fkeys[i];
        // Numeric keypad 0-9.
        ushort[] numpad = [82, 83, 84, 85, 86, 87, 88, 89, 91, 92];
        for (var i = 0; i < 10; i++) k["numpad" + i] = numpad[i];

        void Add(ushort code, params string[] names)
        {
            foreach (var n in names) k[n] = code;
        }

        Add(36, "enter", "return");
        Add(48, "tab");
        Add(53, "escape", "esc");
        Add(49, "space", "spacebar");
        Add(51, "backspace", "delete-back", "bksp");
        Add(117, "delete", "del", "forwarddelete", "forward-delete");
        Add(114, "insert", "ins", "help");
        Add(115, "home");
        Add(119, "end");
        Add(116, "pageup", "pgup");
        Add(121, "pagedown", "pgdn");
        Add(126, "up", "arrowup");
        Add(125, "down", "arrowdown");
        Add(123, "left", "arrowleft");
        Add(124, "right", "arrowright");
        Add(57, "capslock");
        Add(24, "plus", "equals", "equal", "=");
        Add(27, "minus", "hyphen", "-");
        Add(43, "comma", ",");
        Add(47, "period", "dot", ".");
        Add(44, "slash", "/");
        Add(42, "backslash", "\\");
        Add(41, "semicolon", ";");
        Add(39, "quote", "apostrophe", "'");
        Add(50, "backquote", "backtick", "grave", "`");
        Add(33, "leftbracket", "[");
        Add(30, "rightbracket", "]");
        Add(67, "multiply");
        Add(75, "divide");
        Add(69, "add");
        Add(78, "subtract");
        Add(65, "decimal");
        Add(76, "numpadenter");
        return k;
    }

    /// <summary>Virtual key code for a single key name; throws <see cref="ArgumentException"/> for unknown or unavailable keys.</summary>
    public static ushort GetKeyCode(string name)
    {
        var key = name.Trim().ToLowerInvariant();
        if (s_keys.TryGetValue(key, out var code)) return code;
        if (s_unavailable.TryGetValue(key, out var why)) throw new ArgumentException($"Key '{name}' cannot be pressed on macOS: {why}.", nameof(name));
        throw new ArgumentException($"Unknown key '{name}'.", nameof(name));
    }

    /// <summary>Parses a chord such as "ctrl+shift+t", "cmd+s", "win+space", "f5", "ctrl++" (plus key).</summary>
    public static MacKeyChord Parse(string chord)
    {
        ArgumentNullException.ThrowIfNull(chord);
        var text = chord.Trim().ToLowerInvariant();
        if (text.Length == 0) throw new ArgumentException("The key chord is empty.", nameof(chord));

        var parts = new List<string>();
        if (text == "+")
        {
            parts.Add("plus");
        }
        else
        {
            var trailingPlus = text.EndsWith("++", StringComparison.Ordinal);
            if (trailingPlus) text = text[..^2];
            foreach (var raw in text.Split('+'))
            {
                var part = raw.Trim();
                if (part.Length == 0) throw new ArgumentException($"Key chord '{chord}' has an empty key name.", nameof(chord));
                parts.Add(part);
            }
            if (trailingPlus) parts.Add("plus");
        }

        var modifiers = MacModifiers.None;
        ushort? keyCode = null;
        string? keyName = null;
        foreach (var part in parts)
        {
            if (s_modifiers.TryGetValue(part, out var mod))
            {
                modifiers |= mod;
                continue;
            }
            if (keyCode is not null)
                throw new ArgumentException($"Key chord '{chord}' names more than one non-modifier key ('{keyName}' and '{part}').", nameof(chord));
            keyCode = GetKeyCode(part);
            keyName = part;
        }
        return new MacKeyChord(modifiers, keyCode, keyName);
    }

    /// <summary>Parses a modifiers-only chord ("shift", "ctrl+shift", "primary"); null/empty is none. Throws when it names a non-modifier key.</summary>
    public static MacModifiers ParseModifiers(string? modifiers)
    {
        if (string.IsNullOrWhiteSpace(modifiers)) return MacModifiers.None;
        var chord = Parse(modifiers);
        if (chord.KeyCode is not null)
            throw new ArgumentException($"'{modifiers}' names the key '{chord.KeyName}'; only modifiers (shift, ctrl, alt, win, primary) can be held during a click.", nameof(modifiers));
        return chord.Modifiers;
    }

    /// <summary>CGEventFlags for a modifier set.</summary>
    public static ulong ToEventFlags(MacModifiers modifiers)
    {
        ulong flags = 0;
        if (modifiers.HasFlag(MacModifiers.Control)) flags |= FlagMaskControl;
        if (modifiers.HasFlag(MacModifiers.Option)) flags |= FlagMaskAlternate;
        if (modifiers.HasFlag(MacModifiers.Shift)) flags |= FlagMaskShift;
        if (modifiers.HasFlag(MacModifiers.Command)) flags |= FlagMaskCommand;
        return flags;
    }

    /// <summary>The modifier keys of a set in press order (Command, Control, Option, Shift), each with its own flag mask.</summary>
    public static IReadOnlyList<(MacModifiers Modifier, ushort KeyCode, ulong Flag)> ModifierKeys(MacModifiers modifiers)
    {
        var list = new List<(MacModifiers, ushort, ulong)>(4);
        if (modifiers.HasFlag(MacModifiers.Command)) list.Add((MacModifiers.Command, KeyCommand, FlagMaskCommand));
        if (modifiers.HasFlag(MacModifiers.Control)) list.Add((MacModifiers.Control, KeyControl, FlagMaskControl));
        if (modifiers.HasFlag(MacModifiers.Option)) list.Add((MacModifiers.Option, KeyOption, FlagMaskAlternate));
        if (modifiers.HasFlag(MacModifiers.Shift)) list.Add((MacModifiers.Shift, KeyShift, FlagMaskShift));
        return list;
    }
}
