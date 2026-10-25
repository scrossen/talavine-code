using System.Reflection;
using SwarmCore.RemoteControl;
using SwarmCore.RemoteControl.Mac;
using SwarmCore.RemoteControl.Mac.Mapping;

namespace SwarmCore.RemoteControl.Mac.Tests;

public class AxRoleMapTests
{
    [Theory]
    [InlineData("AXButton", "button")]
    [InlineData("AXLink", "hyperlink")]
    [InlineData("AXTextField", "edit")]
    [InlineData("AXTextArea", "edit")]
    [InlineData("AXSearchField", "edit")]
    [InlineData("AXComboBox", "combobox")]
    [InlineData("AXPopUpButton", "combobox")]
    [InlineData("AXCheckBox", "checkbox")]
    [InlineData("AXRadioButton", "radiobutton")]
    [InlineData("AXMenuItem", "menuitem")]
    [InlineData("AXMenuBarItem", "menuitem")]
    [InlineData("AXMenu", "menu")]
    [InlineData("AXMenuBar", "menubar")]
    [InlineData("AXRow", "listitem")]
    [InlineData("AXCell", "dataitem")]
    [InlineData("AXStaticText", "text")]
    [InlineData("AXHeading", "text")]
    [InlineData("AXImage", "image")]
    [InlineData("AXSlider", "slider")]
    [InlineData("AXIncrementor", "spinner")]
    [InlineData("AXGroup", "group")]
    [InlineData("AXScrollArea", "pane")]
    [InlineData("AXSplitGroup", "pane")]
    [InlineData("AXLayoutArea", "pane")]
    [InlineData("AXWebArea", "document")]
    [InlineData("AXWindow", "window")]
    [InlineData("AXSheet", "window")]
    [InlineData("AXDrawer", "window")]
    [InlineData("AXToolbar", "toolbar")]
    [InlineData("AXTabGroup", "tab")]
    [InlineData("AXList", "list")]
    [InlineData("AXTable", "table")]
    [InlineData("AXOutline", "tree")]
    [InlineData("AXScrollBar", "scrollbar")]
    [InlineData("AXProgressIndicator", "progressbar")]
    [InlineData("AXDisclosureTriangle", "button")]
    [InlineData("AXUnknown", "custom")]
    [InlineData(null, "custom")]
    [InlineData("", "custom")]
    [InlineData("AXColorWell", "colorwell")]
    [InlineData("AXSplitter", "splitter")]
    public void MapsRoles(string? role, string expected) => Assert.Equal(expected, AxRoleMap.Map(role));

    [Fact]
    public void TabButtonSubroleIsTabItem() => Assert.Equal("tabitem", AxRoleMap.Map("AXRadioButton", "AXTabButton"));

    [Fact]
    public void DocumentSubroleOnTextAreaIsDocument() => Assert.Equal("document", AxRoleMap.Map("AXTextArea", "AXDocument"));

    [Fact]
    public void RowInsideOutlineIsTreeItem()
    {
        Assert.Equal("treeitem", AxRoleMap.Map("AXRow", null, "AXOutline"));
        Assert.Equal("treeitem", AxRoleMap.Map("AXRow", "AXOutlineRow", "AXGroup"));
        Assert.Equal("listitem", AxRoleMap.Map("AXRow", "AXTableRow", "AXTable"));
    }

    [Fact]
    public void SecureTextFieldIsStillEdit() => Assert.Equal("edit", AxRoleMap.Map("AXTextField", "AXSecureTextField"));

    [Theory]
    [InlineData("AXCheckBox", null, false, true)]
    [InlineData("AXRadioButton", null, false, true)]
    [InlineData("AXRadioButton", "AXTabButton", false, false)]
    [InlineData("AXButton", "AXToggle", false, true)]
    [InlineData("AXCheckBox", "AXSwitch", false, true)]
    [InlineData("AXMenuItem", null, false, false)]
    [InlineData("AXMenuItem", null, true, true)]
    [InlineData("AXButton", null, false, false)]
    public void ToggleLikeRoles(string role, string? subrole, bool mark, bool expected) =>
        Assert.Equal(expected, AxRoleMap.IsToggleLike(role, subrole, mark));
}

public class MacKeyMapTests
{
    [Fact]
    public void ParsesModifierChord()
    {
        var chord = MacKeyMap.Parse("ctrl+shift+t");
        Assert.Equal(MacModifiers.Control | MacModifiers.Shift, chord.Modifiers);
        Assert.Equal((ushort)17, chord.KeyCode);
        Assert.Equal(MacKeyMap.FlagMaskControl | MacKeyMap.FlagMaskShift, MacKeyMap.ToEventFlags(chord.Modifiers));
    }

    [Theory]
    [InlineData("win+space", MacModifiers.Command, 49)]
    [InlineData("cmd+s", MacModifiers.Command, 1)]
    [InlineData("command+q", MacModifiers.Command, 12)]
    [InlineData("alt+f4", MacModifiers.Option, 118)]
    [InlineData("option+tab", MacModifiers.Option, 48)]
    [InlineData("CTRL+ALT+Delete", MacModifiers.Control | MacModifiers.Option, 117)]
    [InlineData("enter", MacModifiers.None, 36)]
    [InlineData("return", MacModifiers.None, 36)]
    [InlineData("esc", MacModifiers.None, 53)]
    [InlineData("escape", MacModifiers.None, 53)]
    [InlineData("backspace", MacModifiers.None, 51)]
    [InlineData("delete-back", MacModifiers.None, 51)]
    [InlineData("forwarddelete", MacModifiers.None, 117)]
    [InlineData("insert", MacModifiers.None, 114)]
    [InlineData("home", MacModifiers.None, 115)]
    [InlineData("end", MacModifiers.None, 119)]
    [InlineData("pageup", MacModifiers.None, 116)]
    [InlineData("pagedown", MacModifiers.None, 121)]
    [InlineData("up", MacModifiers.None, 126)]
    [InlineData("down", MacModifiers.None, 125)]
    [InlineData("left", MacModifiers.None, 123)]
    [InlineData("right", MacModifiers.None, 124)]
    [InlineData("capslock", MacModifiers.None, 57)]
    [InlineData("f1", MacModifiers.None, 122)]
    [InlineData("f5", MacModifiers.None, 96)]
    [InlineData("f12", MacModifiers.None, 111)]
    [InlineData("f20", MacModifiers.None, 90)]
    [InlineData("a", MacModifiers.None, 0)]
    [InlineData("z", MacModifiers.None, 6)]
    [InlineData("0", MacModifiers.None, 29)]
    [InlineData("9", MacModifiers.None, 25)]
    [InlineData("numpad0", MacModifiers.None, 82)]
    [InlineData("numpad7", MacModifiers.None, 89)]
    [InlineData("numpad8", MacModifiers.None, 91)]
    [InlineData("numpad9", MacModifiers.None, 92)]
    [InlineData("plus", MacModifiers.None, 24)]
    [InlineData("equals", MacModifiers.None, 24)]
    [InlineData("minus", MacModifiers.None, 27)]
    [InlineData("comma", MacModifiers.None, 43)]
    [InlineData("period", MacModifiers.None, 47)]
    [InlineData("slash", MacModifiers.None, 44)]
    [InlineData("backslash", MacModifiers.None, 42)]
    [InlineData("semicolon", MacModifiers.None, 41)]
    [InlineData("quote", MacModifiers.None, 39)]
    [InlineData("backquote", MacModifiers.None, 50)]
    [InlineData("leftbracket", MacModifiers.None, 33)]
    [InlineData("rightbracket", MacModifiers.None, 30)]
    [InlineData("multiply", MacModifiers.None, 67)]
    [InlineData("divide", MacModifiers.None, 75)]
    [InlineData("add", MacModifiers.None, 69)]
    [InlineData("subtract", MacModifiers.None, 78)]
    [InlineData("decimal", MacModifiers.None, 65)]
    [InlineData("ctrl++", MacModifiers.Control, 24)]
    [InlineData(" shift + tab ", MacModifiers.Shift, 48)]
    public void ParsesKeys(string chord, object modifiers, int keyCode)
    {
        var parsed = MacKeyMap.Parse(chord);
        Assert.Equal((MacModifiers)modifiers, parsed.Modifiers);
        Assert.Equal((ushort)keyCode, parsed.KeyCode);
    }

    [Fact]
    public void ModifierOnlyChordHasNoKey()
    {
        var parsed = MacKeyMap.Parse("win");
        Assert.Equal(MacModifiers.Command, parsed.Modifiers);
        Assert.Null(parsed.KeyCode);
    }

    [Theory]
    [InlineData("printscreen")]
    [InlineData("pause")]
    [InlineData("numlock")]
    [InlineData("scrolllock")]
    [InlineData("apps")]
    [InlineData("ctrl+bogus")]
    [InlineData("")]
    [InlineData("ctrl+a+b")]
    [InlineData("ctrl++a")]
    public void RejectsUnknownOrUnavailableKeys(string chord)
    {
        var ex = Assert.Throws<ArgumentException>(() => MacKeyMap.Parse(chord));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void UnavailableKeyMessageNamesTheKey()
    {
        var ex = Assert.Throws<ArgumentException>(() => MacKeyMap.Parse("numlock"));
        Assert.Contains("numlock", ex.Message);
    }

    [Fact]
    public void ModifierKeysUseCarbonCodesAndFlags()
    {
        var keys = MacKeyMap.ModifierKeys(MacModifiers.Command | MacModifiers.Control | MacModifiers.Option | MacModifiers.Shift);
        Assert.Equal([(ushort)55, (ushort)59, (ushort)58, (ushort)56], keys.Select(k => k.KeyCode).ToArray());
        Assert.Equal([0x100000UL, 0x40000UL, 0x80000UL, 0x20000UL], keys.Select(k => k.Flag).ToArray());
    }
}

public class AxValueFormatterTests
{
    [Theory]
    [InlineData(0.0, "off", false)]
    [InlineData(1.0, "on", true)]
    [InlineData(2.0, "indeterminate", null)]
    public void CheckboxStates(double raw, string expected, bool? toggled)
    {
        var v = AxValueFormatter.Format("checkbox", raw, null);
        Assert.Equal(expected, v.Value);
        Assert.Equal(toggled, v.IsToggled);
    }

    [Fact]
    public void RadioButtonOnIsSelected()
    {
        var v = AxValueFormatter.Format("radiobutton", 1.0, null);
        Assert.Equal("on", v.Value);
        Assert.True(v.IsSelected);
    }

    [Fact]
    public void SelectedRowsSaySelected()
    {
        Assert.Equal("selected", AxValueFormatter.Format("listitem", null, true).Value);
        Assert.Equal("selected", AxValueFormatter.Format("treeitem", null, true).Value);
        Assert.True(AxValueFormatter.Format("dataitem", null, true).IsSelected);
        Assert.Null(AxValueFormatter.Format("listitem", null, false).Value);
    }

    [Fact]
    public void TabItemsUseSelectedOrValue()
    {
        Assert.Equal("selected", AxValueFormatter.Format("tabitem", 1.0, null).Value);
        Assert.Null(AxValueFormatter.Format("tabitem", 0.0, null).Value);
    }

    [Theory]
    [InlineData("slider", 42.0, "42")]
    [InlineData("slider", 0.5, "0.5")]
    [InlineData("progressbar", 75.25, "75.25")]
    [InlineData("spinner", -3.0, "-3")]
    public void RangeValuesUseInvariantNumbers(string type, double raw, string expected) =>
        Assert.Equal(expected, AxValueFormatter.Format(type, raw, null).Value);

    [Fact]
    public void TextFieldsKeepEmptyStrings()
    {
        Assert.Equal("", AxValueFormatter.Format("edit", "", null).Value);
        Assert.Equal("hello", AxValueFormatter.Format("edit", "hello", null).Value);
        Assert.Null(AxValueFormatter.Format("edit", null, null).Value);
    }

    [Fact]
    public void TextElementsUseTheirContent()
    {
        Assert.Equal("Welcome", AxValueFormatter.Format("text", "Welcome", null).Value);
        Assert.Null(AxValueFormatter.Format("text", "", null).Value);
        Assert.Null(AxValueFormatter.Format("button", null, null).Value);
    }
}

public class AxNamingTests
{
    [Fact]
    public void PrefersTitleThenDescriptionThenValueThenTitleElement()
    {
        Assert.Equal("Save", AxNaming.ChooseName("Save", "desc", "v", "AXButton", "label"));
        Assert.Equal("desc", AxNaming.ChooseName("", "desc", "v", "AXButton", "label"));
        Assert.Equal("Click me", AxNaming.ChooseName(null, null, "Click me", "AXStaticText", "label"));
        Assert.Equal("Docs", AxNaming.ChooseName(null, null, "Docs", "AXLink", null));
        Assert.Equal("label", AxNaming.ChooseName(null, null, "typed text", "AXTextField", "label"));
        Assert.Equal("", AxNaming.ChooseName(null, null, null, "AXGroup", null));
    }
}

public class AxActionMapTests
{
    [Fact]
    public void PressMapsToInvokeAndToggleForToggleLike()
    {
        Assert.Equal([ControlAction.Invoke], AxActionMap.Build(["AXPress"], false, false, false, false, false));
        Assert.Equal([ControlAction.Invoke, ControlAction.Toggle], AxActionMap.Build(["AXPress"], true, false, false, false, false));
    }

    [Fact]
    public void SettableAttributesAndActionsMap()
    {
        var actions = AxActionMap.Build(["AXShowMenu", "AXScrollToVisible"], false, true, true, true, false);
        Assert.Equal([ControlAction.Select, ControlAction.Expand, ControlAction.SetValue, ControlAction.Focus, ControlAction.ScrollIntoView], actions);
        Assert.Contains(ControlAction.Collapse, AxActionMap.Build([], false, false, false, false, true));
        Assert.DoesNotContain(ControlAction.Collapse, AxActionMap.Build(["AXShowMenu"], false, false, false, false, false));
    }

    [Fact]
    public void ConfirmOrPickAlsoInvoke()
    {
        Assert.Contains(ControlAction.Invoke, AxActionMap.Build(["AXConfirm"], false, false, false, false, false));
        Assert.Contains(ControlAction.Invoke, AxActionMap.Build(["AXPick"], false, false, false, false, false));
        Assert.Empty(AxActionMap.Build([], false, false, false, false, false));
    }
}

public class AxRuntimeKeyTests
{
    [Fact]
    public void RoundTripsIndexPath()
    {
        var key = AxRuntimeKey.Format(4242, 1183, [0, 3, 2, 1]);
        Assert.Equal("ax:4242:1183:0.3.2.1", key);
        Assert.True(AxRuntimeKey.TryParse(key, out var parsed));
        Assert.Equal(4242, parsed.Pid);
        Assert.Equal(1183, parsed.WindowId);
        Assert.Equal([0, 3, 2, 1], parsed.Path);
        Assert.Equal(3, parsed.Depth);
    }

    [Fact]
    public void WindowElementIsDepthZero()
    {
        Assert.True(AxRuntimeKey.TryParse("ax:1:2:0", out var parsed));
        Assert.Equal(0, parsed.Depth);
        Assert.Null(AxRuntimeKey.ParentOf(1, 2, parsed.Path!));
        Assert.Equal("ax:1:2:0.3", AxRuntimeKey.ParentOf(1, 2, [0, 3, 7]));
    }

    [Fact]
    public void RoundTripsReferenceKey()
    {
        var key = AxRuntimeKey.FormatReference(77, 0, 5);
        Assert.Equal("axref:77:0:5", key);
        Assert.True(AxRuntimeKey.TryParse(key, out var parsed));
        Assert.False(parsed.IsPath);
        Assert.Equal(5, parsed.Serial);
        Assert.Equal(-1, parsed.Depth);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("uia:1.2.3")]
    [InlineData("ax:1:2")]
    [InlineData("ax:x:2:0")]
    [InlineData("ax:1:2:1.2")]
    [InlineData("ax:1:2:0..2")]
    [InlineData("ax:1:2:0.-1")]
    [InlineData("axref:1:2:abc")]
    public void RejectsMalformedKeys(string? key) => Assert.False(AxRuntimeKey.TryParse(key, out _));

    [Fact]
    public void FormatRequiresWindowElement() => Assert.Throws<ArgumentException>(() => AxRuntimeKey.Format(1, 2, []));
}

public class AxWindowMatcherTests
{
    [Fact]
    public void MatchesWithinTolerance()
    {
        RectD?[] candidates = [new RectD(0, 0, 100, 100), null, new RectD(10.5, 20, 800, 600.75)];
        Assert.Equal(2, AxWindowMatcher.FindBestByBounds(candidates, new RectD(10, 21, 801, 600)));
    }

    [Fact]
    public void RejectsOutsideTolerance()
    {
        RectD?[] candidates = [new RectD(10, 20, 800, 600)];
        Assert.Equal(-1, AxWindowMatcher.FindBestByBounds(candidates, new RectD(13, 20, 800, 600)));
    }

    [Fact]
    public void PrefersClosestMatch()
    {
        RectD?[] candidates = [new RectD(11, 20, 800, 600), new RectD(10, 20, 800, 600)];
        Assert.Equal(1, AxWindowMatcher.FindBestByBounds(candidates, new RectD(10, 20, 800, 600)));
    }
}

public class MacLayoutTests
{
    [Fact]
    public void MainDisplayFirstThenLeftToRight()
    {
        var ordered = DisplayLayout.Order(
        [
            (5u, new RectD(1920, 0, 1920, 1080)),
            (7u, new RectD(-1440, 0, 1440, 900)),
            (1u, new RectD(0, 0, 1512, 982)),
        ], mainId: 1);
        Assert.Equal([1u, 7u, 5u], ordered.Select(d => d.Id).ToArray());
    }

    [Fact]
    public void DisplayIndexFromWindowCenter()
    {
        DisplayInfo[] displays =
        [
            new(1, "Display 1", true, new ScreenRect(0, 0, 1512, 982), new ScreenRect(0, 0, 1512, 982), 2),
            new(2, "Display 5", false, new ScreenRect(1512, 0, 1920, 1080), new ScreenRect(1512, 0, 1920, 1080), 1),
        ];
        Assert.Equal(2, DisplayLayout.DisplayIndexFor(new ScreenRect(1600, 100, 800, 600), displays));
        Assert.Equal(1, DisplayLayout.DisplayIndexFor(new ScreenRect(100, 100, 400, 300), displays));
        Assert.Equal(2, DisplayLayout.DisplayIndexFor(new ScreenRect(4000, 100, 400, 300), displays));
        Assert.Null(DisplayLayout.DisplayIndexFor(new ScreenRect(0, 0, 10, 10), []));
    }

    [Fact]
    public void RectRoundingAndIntersection()
    {
        Assert.Equal(new ScreenRect(10, 20, 101, 50), new RectD(10.4, 19.6, 100.6, 50.2).ToScreenRect());
        Assert.True(new RectD(0, 0, 10, 10).Intersects(new RectD(5, 5, 10, 10)));
        Assert.False(new RectD(0, 0, 10, 10).Intersects(new RectD(10, 0, 10, 10)));
        Assert.False(new RectD(0, 0, 0, 10).Intersects(new RectD(0, 0, 10, 10)));
        Assert.Equal(new RectD(-100, 0, 300, 50), new RectD(0, 0, 200, 50).Union(new RectD(-100, 10, 50, 10)));
    }

    [Fact]
    public void WindowFilterKeepsNormalWindowsOnly()
    {
        Assert.True(CgWindowFilter.IsCandidate(0, 1, new RectD(0, 0, 10, 10), "TextEdit"));
        Assert.False(CgWindowFilter.IsCandidate(25, 1, new RectD(0, 0, 10, 10), "Dock"));
        Assert.False(CgWindowFilter.IsCandidate(0, 0, new RectD(0, 0, 10, 10), "TextEdit"));
        Assert.False(CgWindowFilter.IsCandidate(0, 1, new RectD(0, 0, 0, 10), "TextEdit"));
        Assert.False(CgWindowFilter.IsCandidate(0, 1, null, "TextEdit"));
        Assert.False(CgWindowFilter.IsCandidate(0, 1, new RectD(0, 0, 10, 10), ""));
    }
}

public class MacTextChunkerTests
{
    [Fact]
    public void SplitsIntoTwentyUnitChunks()
    {
        var text = new string('x', 45);
        var segments = MacTextChunker.Split(text);
        Assert.Equal([20, 20, 5], segments.Select(s => s.Text!.Length).ToArray());
    }

    [Fact]
    public void NewlinesAndTabsBecomeKeys()
    {
        var segments = MacTextChunker.Split("a\r\nb\tc\nd");
        Assert.Equal(
            [new TypingSegment("a", null), new TypingSegment(null, 36), new TypingSegment("b", null), new TypingSegment(null, 48),
             new TypingSegment("c", null), new TypingSegment(null, 36), new TypingSegment("d", null)],
            segments);
    }

    [Fact]
    public void NeverSplitsSurrogatePairs()
    {
        var text = new string('a', 19) + "\U0001F600" + "b";
        var segments = MacTextChunker.Split(text);
        Assert.Equal(new string('a', 19), segments[0].Text);
        Assert.Equal("\U0001F600b", segments[1].Text);
        Assert.All(segments, s => Assert.True(s.Text!.Length <= MacTextChunker.MaxChunk));
    }

    [Fact]
    public void EmptyTextHasNoSegments() => Assert.Empty(MacTextChunker.Split(""));
}

public class MacDesktopAutomationConstructionTests
{
    [Fact]
    public void ConstructorThrowsOffMac()
    {
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst()) return;
        // Reflection avoids calling a [SupportedOSPlatform("macos")] constructor directly from a cross-platform test.
        var ex = Assert.Throws<TargetInvocationException>(() => Activator.CreateInstance(typeof(MacDesktopAutomation)));
        Assert.IsType<PlatformNotSupportedException>(ex.InnerException);
    }
}
