using System.Globalization;
using System.Runtime.Versioning;
using static SwarmCore.RemoteControl.Windows.NativeMethods;

namespace SwarmCore.RemoteControl.Windows;

/// <summary>A virtual key plus whether its scan code carries the E0 (extended) prefix.</summary>
internal readonly record struct VirtualKey(ushort Code, bool Extended);

/// <summary>Maps the normalized key names of a chord ("ctrl+shift+t") to Windows virtual keys.</summary>
[SupportedOSPlatform("windows")]
internal static class KeyMap
{
    public const ushort VkShift = 0x10;
    public const ushort VkControl = 0x11;
    public const ushort VkMenu = 0x12;
    public const ushort VkLeftWin = 0x5B;
    public const ushort VkReturn = 0x0D;
    public const ushort VkTab = 0x09;
    public const ushort VkDelete = 0x2E;
    /// <summary>An unassigned virtual key: pressing it between Alt down and up keeps Alt from opening a menu bar.</summary>
    public const ushort VkUnassigned = 0xE8;

    private const ushort VkLetterA = 0x41;
    private const ushort VkDigit0 = 0x30;
    private const ushort VkF1 = 0x70;
    private const int FunctionKeyCount = 24;
    private const ushort VkNumpad0 = 0x60;
    private const string VirtualKeyPrefix = "vk:";
    private const int VkScanShiftFlag = 0x01;
    private const int VkScanControlFlag = 0x02;
    private const int VkScanAltFlag = 0x04;

    private static readonly Dictionary<string, VirtualKey> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = new(VkControl, false), ["control"] = new(VkControl, false),
        // The platform's primary command modifier: Control on Windows (Command on macOS).
        ["primary"] = new(VkControl, false), ["mod"] = new(VkControl, false), ["cmdorctrl"] = new(VkControl, false),
        ["alt"] = new(VkMenu, false), ["option"] = new(VkMenu, false),
        ["shift"] = new(VkShift, false),
        ["win"] = new(VkLeftWin, true), ["windows"] = new(VkLeftWin, true), ["command"] = new(VkLeftWin, true),
        ["cmd"] = new(VkLeftWin, true), ["meta"] = new(VkLeftWin, true), ["super"] = new(VkLeftWin, true),
        ["enter"] = new(VkReturn, false), ["return"] = new(VkReturn, false),
        ["tab"] = new(VkTab, false),
        ["escape"] = new(0x1B, false), ["esc"] = new(0x1B, false),
        ["space"] = new(0x20, false), ["spacebar"] = new(0x20, false),
        ["backspace"] = new(0x08, false), ["back"] = new(0x08, false),
        ["delete"] = new(0x2E, true), ["del"] = new(0x2E, true),
        ["insert"] = new(0x2D, true), ["ins"] = new(0x2D, true),
        ["home"] = new(0x24, true), ["end"] = new(0x23, true),
        ["pageup"] = new(0x21, true), ["pgup"] = new(0x21, true),
        ["pagedown"] = new(0x22, true), ["pgdn"] = new(0x22, true), ["pgdown"] = new(0x22, true),
        ["up"] = new(0x26, true), ["down"] = new(0x28, true), ["left"] = new(0x25, true), ["right"] = new(0x27, true),
        ["arrowup"] = new(0x26, true), ["arrowdown"] = new(0x28, true), ["arrowleft"] = new(0x25, true), ["arrowright"] = new(0x27, true),
        ["printscreen"] = new(0x2C, true), ["prtsc"] = new(0x2C, true), ["printscr"] = new(0x2C, true),
        ["pause"] = new(0x13, false), ["break"] = new(0x13, false),
        ["capslock"] = new(0x14, false), ["numlock"] = new(0x90, true), ["scrolllock"] = new(0x91, false),
        ["apps"] = new(0x5D, true), ["menu"] = new(0x5D, true), ["contextmenu"] = new(0x5D, true),
        ["plus"] = new(0xBB, false), ["equals"] = new(0xBB, false), ["equal"] = new(0xBB, false),
        ["minus"] = new(0xBD, false), ["hyphen"] = new(0xBD, false),
        ["comma"] = new(0xBC, false), ["period"] = new(0xBE, false), ["dot"] = new(0xBE, false),
        ["slash"] = new(0xBF, false), ["backslash"] = new(0xDC, false),
        ["semicolon"] = new(0xBA, false), ["quote"] = new(0xDE, false), ["apostrophe"] = new(0xDE, false),
        ["backquote"] = new(0xC0, false), ["backtick"] = new(0xC0, false), ["grave"] = new(0xC0, false),
        ["leftbracket"] = new(0xDB, false), ["rightbracket"] = new(0xDD, false),
        ["multiply"] = new(0x6A, false), ["add"] = new(0x6B, false), ["separator"] = new(0x6C, false),
        ["subtract"] = new(0x6D, false), ["decimal"] = new(0x6E, false), ["divide"] = new(0x6F, true),
        ["volumemute"] = new(0xAD, true), ["volumedown"] = new(0xAE, true), ["volumeup"] = new(0xAF, true),
        ["medianext"] = new(0xB0, true), ["mediaprevious"] = new(0xB1, true), ["mediaprev"] = new(0xB1, true),
        ["mediastop"] = new(0xB2, true), ["mediaplaypause"] = new(0xB3, true), ["playpause"] = new(0xB3, true),
        ["browserback"] = new(0xA6, true), ["browserforward"] = new(0xA7, true), ["browserrefresh"] = new(0xA8, true),
        ["browserhome"] = new(0xAC, true), ["sleep"] = new(0x5F, false),
    };

    public static bool IsModifier(ushort vk) => vk is VkShift or VkControl or VkMenu or VkLeftWin;

    /// <summary>
    /// Resolves one key name. A single character not on the keyboard layout returns null so the caller can type it as
    /// Unicode; <paramref name="requiredModifiers"/> receives Shift/Ctrl/Alt the layout needs for a single character.
    /// </summary>
    public static VirtualKey? Resolve(string name, List<VirtualKey> requiredModifiers)
    {
        if (Named.TryGetValue(name, out var named)) return named;
        var lower = name.ToLowerInvariant();
        if (lower.Length == 1)
        {
            var c = lower[0];
            if (c is >= 'a' and <= 'z') return new((ushort)(VkLetterA + (c - 'a')), false);
            if (c is >= '0' and <= '9') return new((ushort)(VkDigit0 + (c - '0')), false);
            var scan = VkKeyScanW(name[0]);
            if (scan == -1) return null;
            var shiftState = (scan >> 8) & 0xFF;
            if ((shiftState & VkScanShiftFlag) != 0) requiredModifiers.Add(new(VkShift, false));
            if ((shiftState & VkScanControlFlag) != 0) requiredModifiers.Add(new(VkControl, false));
            if ((shiftState & VkScanAltFlag) != 0) requiredModifiers.Add(new(VkMenu, false));
            return new((ushort)(scan & 0xFF), false);
        }
        if (lower.Length > 1 && lower[0] == 'f' && int.TryParse(lower[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var f)
            && f is >= 1 and <= FunctionKeyCount)
            return new((ushort)(VkF1 + f - 1), false);
        foreach (var prefix in (string[])["numpad", "num"])
            if (lower.StartsWith(prefix, StringComparison.Ordinal) && lower.Length == prefix.Length + 1 && char.IsAsciiDigit(lower[^1]))
                return new((ushort)(VkNumpad0 + (lower[^1] - '0')), false);
        if (lower.StartsWith(VirtualKeyPrefix, StringComparison.Ordinal))
        {
            // Escape hatch for any key Windows knows: "vk:0x5F" or "vk:95".
            var text = lower[VirtualKeyPrefix.Length..];
            var ok = text.StartsWith("0x", StringComparison.Ordinal)
                ? ushort.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)
                : ushort.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out code);
            if (ok && code is > 0 and <= 0xFE) return new(code, false);
        }
        throw NotUnderstood($"Unknown key '{name}'. Use names like ctrl, alt, shift, win, enter, tab, f5, pageup, a, 1, or vk:0xNN.");
    }

    /// <summary>Splits a chord on '+', treating a trailing "++" (or a lone "+") as the plus key.</summary>
    public static List<string> Split(string chord)
    {
        if (string.IsNullOrWhiteSpace(chord)) throw NotUnderstood("The key chord is empty.");
        var text = chord.Trim();
        if (text == "+") return ["plus"];
        var trailingPlus = text.EndsWith("++", StringComparison.Ordinal);
        if (trailingPlus) text = text[..^2];
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (trailingPlus) parts.Add("plus");
        if (parts.Count == 0) throw NotUnderstood($"The key chord '{chord}' names no keys.");
        return parts;
    }

    public static DesktopAutomationException NotUnderstood(string message) => new(message, FailureCategories.NotUnderstood);

    public static ushort ScanCode(ushort vk) => (ushort)MapVirtualKeyW(vk, MapVkVkToVsc);
}
