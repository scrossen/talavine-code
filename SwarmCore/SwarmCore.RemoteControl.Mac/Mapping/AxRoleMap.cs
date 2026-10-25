namespace SwarmCore.RemoteControl.Mac.Mapping;

/// <summary>
/// Maps macOS Accessibility roles/subroles to the platform-neutral lowercase control types used by
/// <see cref="ControlInfo.ControlType"/>. Pure: no native calls.
/// </summary>
internal static class AxRoleMap
{
    private static readonly Dictionary<string, string> s_roles = new(StringComparer.Ordinal)
    {
        ["AXButton"] = "button",
        ["AXDisclosureTriangle"] = "button",
        ["AXMenuButton"] = "menubutton",
        ["AXLink"] = "hyperlink",
        ["AXTextField"] = "edit",
        ["AXTextArea"] = "edit",
        ["AXSearchField"] = "edit",
        ["AXComboBox"] = "combobox",
        ["AXPopUpButton"] = "combobox",
        ["AXCheckBox"] = "checkbox",
        ["AXRadioButton"] = "radiobutton",
        ["AXMenuItem"] = "menuitem",
        ["AXMenuBarItem"] = "menuitem",
        ["AXMenu"] = "menu",
        ["AXMenuBar"] = "menubar",
        ["AXRow"] = "listitem",
        ["AXCell"] = "dataitem",
        ["AXStaticText"] = "text",
        ["AXHeading"] = "text",
        ["AXImage"] = "image",
        ["AXSlider"] = "slider",
        ["AXIncrementor"] = "spinner",
        ["AXGroup"] = "group",
        ["AXScrollArea"] = "pane",
        ["AXSplitGroup"] = "pane",
        ["AXLayoutArea"] = "pane",
        ["AXWebArea"] = "document",
        ["AXWindow"] = "window",
        ["AXSheet"] = "window",
        ["AXDrawer"] = "window",
        ["AXToolbar"] = "toolbar",
        ["AXTabGroup"] = "tab",
        ["AXList"] = "list",
        ["AXTable"] = "table",
        ["AXOutline"] = "tree",
        ["AXScrollBar"] = "scrollbar",
        ["AXProgressIndicator"] = "progressbar",
        ["AXBusyIndicator"] = "progressbar",
    };

    /// <summary>Roles whose physical press flips a state, so <see cref="ControlAction.Toggle"/> applies.</summary>
    public static bool IsToggleLike(string? role, string? subrole, bool hasMenuItemMark = false)
    {
        if (subrole is "AXTabButton") return false;
        if (subrole is "AXSwitch" or "AXToggle") return true;
        return role switch
        {
            "AXCheckBox" or "AXRadioButton" => true,
            "AXMenuItem" => hasMenuItemMark,
            _ => false,
        };
    }

    /// <summary>
    /// Control type for an element. <paramref name="parentRole"/> disambiguates rows (an AXRow inside an AXOutline is a tree item).
    /// Unknown roles become their lowercased name without the "AX" prefix; AXUnknown or a missing role is "custom".
    /// </summary>
    public static string Map(string? role, string? subrole = null, string? parentRole = null)
    {
        if (string.IsNullOrEmpty(role) || role == "AXUnknown") return "custom";

        switch (role)
        {
            case "AXRadioButton" when subrole == "AXTabButton":
                return "tabitem";
            case "AXTextArea" when subrole == "AXDocument":
                return "document";
            case "AXRow" when parentRole == "AXOutline" || subrole == "AXOutlineRow":
                return "treeitem";
        }

        if (s_roles.TryGetValue(role, out var mapped)) return mapped;

        var bare = role.StartsWith("AX", StringComparison.Ordinal) ? role[2..] : role;
        return bare.Length == 0 ? "custom" : bare.ToLowerInvariant();
    }
}
