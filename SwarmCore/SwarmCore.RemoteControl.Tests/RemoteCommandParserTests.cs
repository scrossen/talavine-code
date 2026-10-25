namespace SwarmCore.RemoteControl.Tests;

public sealed class RemoteCommandParserTests
{
    private static RemoteCommand Parse(string text, CommandKind expected)
    {
        var cmd = RemoteCommandParser.Parse(text);
        Assert.True(cmd.Kind == expected, $"'{text}' parsed as {cmd.Kind} (error: {cmd.Error}), expected {expected}");
        Assert.Equal(text, cmd.RawText);
        return cmd;
    }

    // ------------------------------------------------------------ help and session

    [Theory]
    [InlineData("help")]
    [InlineData("?")]
    [InlineData("commands")]
    [InlineData("what can you do")]
    public void Help(string text) => Parse(text, CommandKind.Help);

    [Theory]
    [InlineData("quit")]
    [InlineData("exit")]
    [InlineData("bye")]
    [InlineData("q")]
    public void Quit(string text) => Parse(text, CommandKind.Quit);

    [Theory]
    [InlineData("where")]
    [InlineData("where is the mouse")]
    [InlineData("mouse position")]
    [InlineData("what has focus")]
    public void Where(string text) => Parse(text, CommandKind.Where);

    [Theory]
    [InlineData("wait 2", 2000)]
    [InlineData("wait 500 ms", 500)]
    [InlineData("pause 1.5 seconds", 1500)]
    [InlineData("sleep 3s", 3000)]
    [InlineData("wait for 2 seconds", 2000)]
    public void Wait(string text, int ms) => Assert.Equal(TimeSpan.FromMilliseconds(ms), Parse(text, CommandKind.Wait).Delay);

    // ------------------------------------------------------------ waiting for state

    [Theory]
    [InlineData("wait for window \"Save As\"", "Save As")]
    [InlineData("wait for a window called Save As", "Save As")]
    [InlineData("wait until notepad appears", "notepad")]
    [InlineData("wait for the Export dialog", "Export")]
    public void WaitForWindowAppears(string text, string name)
    {
        var cmd = Parse(text, CommandKind.WaitFor);
        Assert.Equal(WaitTarget.WindowAppears, cmd.Wait!.Target);
        Assert.Equal(name, cmd.Window!.NameText);
        Assert.Equal(TimeSpan.FromSeconds(30), cmd.Delay);
    }

    [Theory]
    [InlineData("wait until window w5 is gone")]
    [InlineData("wait for w5 to close")]
    public void WaitForWindowGoneByAlias(string text)
    {
        var cmd = Parse(text, CommandKind.WaitFor);
        Assert.Equal(WaitTarget.WindowGone, cmd.Wait!.Target);
        Assert.Equal("w5", cmd.Window!.Alias);
    }

    [Theory]
    [InlineData("wait until the Save As dialog closes")]
    [InlineData("wait until the Save As dialog disappears")]
    public void WaitForWindowGoneByName(string text)
    {
        var cmd = Parse(text, CommandKind.WaitFor);
        Assert.Equal(WaitTarget.WindowGone, cmd.Wait!.Target);
        Assert.Equal("Save As", cmd.Window!.NameText);
    }

    [Fact]
    public void WaitForControlAlias()
    {
        var cmd = Parse("wait for c12", CommandKind.WaitFor);
        Assert.Equal(WaitTarget.ControlAppears, cmd.Wait!.Target);
        Assert.Equal("c12", cmd.Control!.Alias);
    }

    [Fact]
    public void WaitForNamedControlInWindow()
    {
        var cmd = Parse("wait for the Save button in chrome", CommandKind.WaitFor);
        Assert.Equal(WaitTarget.ControlAppears, cmd.Wait!.Target);
        Assert.Equal("button", cmd.Control!.ControlType);
        Assert.Equal("Save", cmd.Control.NameText);
        Assert.Equal("chrome", cmd.Window!.NameText);
    }

    [Fact]
    public void WaitForCalledControlInAlias()
    {
        var cmd = Parse("wait until a button called Done appears in w3", CommandKind.WaitFor);
        Assert.Equal(WaitTarget.ControlAppears, cmd.Wait!.Target);
        Assert.Equal("button", cmd.Control!.ControlType);
        Assert.Equal("Done", cmd.Control.NameText);
        Assert.Equal("w3", cmd.Window!.Alias);
    }

    [Theory]
    [InlineData("wait until c12 is gone")]
    [InlineData("wait for c12 to disappear")]
    public void WaitForControlGone(string text)
    {
        var cmd = Parse(text, CommandKind.WaitFor);
        Assert.Equal(WaitTarget.ControlGone, cmd.Wait!.Target);
        Assert.Equal("c12", cmd.Control!.Alias);
    }

    [Theory]
    [InlineData("wait for c12 value to be \"Done\"")]
    [InlineData("wait until c12 says Done")]
    [InlineData("wait until c12 equals Done")]
    public void WaitForValue(string text)
    {
        var cmd = Parse(text, CommandKind.WaitFor);
        Assert.Equal(WaitTarget.ControlValueEquals, cmd.Wait!.Target);
        Assert.Equal("c12", cmd.Control!.Alias);
        Assert.Equal("Done", cmd.Value);
    }

    [Theory]
    [InlineData("wait for c12 to change")]
    [InlineData("wait until c12 changes")]
    public void WaitForChange(string text) => Assert.Equal(WaitTarget.ControlValueChanges, Parse(text, CommandKind.WaitFor).Wait!.Target);

    [Fact]
    public void WaitForEnabled() => Assert.Equal(WaitTarget.ControlEnabled, Parse("wait until c12 is enabled", CommandKind.WaitFor).Wait!.Target);

    [Theory]
    [InlineData("wait until c12 says Done up to 10 seconds", 10)]
    [InlineData("wait for c12 to change for 10 seconds", 10)]
    [InlineData("wait for window \"Save As\" timeout 10", 10)]
    [InlineData("wait until w5 is gone max 2 minutes", 120)]
    public void WaitForTimeout(string text, int seconds)
    {
        var cmd = Parse(text, CommandKind.WaitFor);
        Assert.Equal(TimeSpan.FromSeconds(seconds), cmd.Delay);
        if (cmd.Wait!.Target == WaitTarget.ControlValueEquals) Assert.Equal("Done", cmd.Value);
    }

    // ------------------------------------------------------------ screens

    [Theory]
    [InlineData("screens")]
    [InlineData("list screens")]
    [InlineData("monitors")]
    [InlineData("displays")]
    [InlineData("how many screens")]
    public void ListDisplays(string text) => Parse(text, CommandKind.ListDisplays);

    [Theory]
    [InlineData("screenshot")]
    [InlineData("take a screenshot")]
    [InlineData("capture the screen")]
    [InlineData("grab the screen")]
    public void ScreenshotPrimary(string text) => Assert.Equal(ScreenshotTarget.Primary, Parse(text, CommandKind.Screenshot).Target);

    [Theory]
    [InlineData("screenshot screen 2")]
    [InlineData("give me a screenshot of screen 2")]
    [InlineData("capture monitor 2")]
    [InlineData("screenshot display 2")]
    [InlineData("screenshot of the second screen")]
    public void ScreenshotDisplay(string text)
    {
        var cmd = Parse(text, CommandKind.Screenshot);
        Assert.Equal(ScreenshotTarget.Display, cmd.Target);
        Assert.Equal(2, cmd.DisplayIndex);
    }

    [Theory]
    [InlineData("screenshot all screens")]
    [InlineData("screenshot everything")]
    [InlineData("capture all monitors")]
    [InlineData("screenshot the whole desktop")]
    public void ScreenshotAll(string text) => Assert.Equal(ScreenshotTarget.AllDisplays, Parse(text, CommandKind.Screenshot).Target);

    [Theory]
    [InlineData("screenshot region 100,100 800x600", 100, 100, 800, 600)]
    [InlineData("screenshot area 100 100 900 700", 100, 100, 800, 600)]
    public void ScreenshotRegion(string text, int left, int top, int width, int height)
    {
        var cmd = Parse(text, CommandKind.Screenshot);
        Assert.Equal(ScreenshotTarget.Region, cmd.Target);
        Assert.Equal(new ScreenRect(left, top, width, height), cmd.Region);
    }

    // ------------------------------------------------------------ windows

    [Theory]
    [InlineData("windows")]
    [InlineData("list windows")]
    [InlineData("apps")]
    [InlineData("list apps")]
    [InlineData("give me a list of all running apps and their window IDs")]
    [InlineData("give me a list of all running apps and their window ids")]
    [InlineData("what is open")]
    [InlineData("what's running")]
    public void ListWindows(string text) => Parse(text, CommandKind.ListWindows);

    [Theory]
    [InlineData("find window chrome", "chrome")]
    [InlineData("which window is notepad", "notepad")]
    [InlineData("where is excel", "excel")]
    public void FindWindow(string text, string name) => Assert.Equal(name, Parse(text, CommandKind.FindWindow).Window!.NameText);

    [Theory]
    [InlineData("focus window w3", "w3", null)]
    [InlineData("activate chrome", null, "chrome")]
    [InlineData("switch to notepad", null, "notepad")]
    [InlineData("bring w2 to the front", "w2", null)]
    [InlineData("go to window w3", "w3", null)]
    [InlineData("show window chrome", null, "chrome")]
    public void FocusWindow(string text, string? alias, string? name)
    {
        var w = Parse(text, CommandKind.FocusWindow).Window!;
        Assert.Equal(alias, w.Alias);
        Assert.Equal(name, w.NameText);
        Assert.Null(w.Hint);
    }

    [Theory]
    [InlineData("screenshot window w3", "w3", null, null)]
    [InlineData("screenshot of window w3 chrome", "w3", null, "chrome")]
    [InlineData("screenshot chrome", null, "chrome", null)]
    [InlineData("capture the notepad window", null, "notepad", null)]
    [InlineData("screenshot w3", "w3", null, null)]
    public void ScreenshotWindow(string text, string? alias, string? name, string? hint)
    {
        var cmd = Parse(text, CommandKind.Screenshot);
        Assert.Equal(ScreenshotTarget.Window, cmd.Target);
        Assert.Equal(alias, cmd.Window!.Alias);
        Assert.Equal(name, cmd.Window.NameText);
        Assert.Equal(hint, cmd.Window.Hint);
        Assert.False(cmd.IncludeControls);
    }

    [Theory]
    [InlineData("give a screenshot of window w3 chrome and the names of all of the controls and their values", "w3", null, "chrome")]
    [InlineData("screenshot window w3 with controls", "w3", null, null)]
    [InlineData("screenshot and controls for chrome", null, "chrome", null)]
    public void ScreenshotWindowWithControls(string text, string? alias, string? name, string? hint)
    {
        var cmd = Parse(text, CommandKind.Screenshot);
        Assert.Equal(ScreenshotTarget.Window, cmd.Target);
        Assert.True(cmd.IncludeControls);
        Assert.Equal(alias, cmd.Window!.Alias);
        Assert.Equal(name, cmd.Window.NameText);
        Assert.Equal(hint, cmd.Window.Hint);
    }

    [Fact]
    public void LargeNumbersAreHandles()
    {
        Assert.Equal(133412, Parse("focus w133412", CommandKind.FocusWindow).Window!.Handle);
        Assert.Equal(133412, Parse("screenshot 133412", CommandKind.Screenshot).Window!.Handle);
    }

    // ------------------------------------------------------------ controls

    [Theory]
    [InlineData("controls", null, null, false)]
    [InlineData("list controls", null, null, false)]
    [InlineData("controls in window w3", "w3", null, false)]
    [InlineData("controls of chrome", null, "chrome", false)]
    [InlineData("what controls are in notepad", null, "notepad", false)]
    [InlineData("elements in w3", "w3", null, false)]
    [InlineData("list all the controls and their values in window w3", "w3", null, true)]
    public void ListControls(string text, string? alias, string? name, bool verbose)
    {
        var cmd = Parse(text, CommandKind.ListControls);
        Assert.Equal(alias, cmd.Window?.Alias);
        Assert.Equal(name, cmd.Window?.NameText);
        Assert.Equal(verbose, cmd.Verbose);
    }

    [Theory]
    [InlineData("controls in w3 matching save", "w3", null, null, "save")]
    [InlineData("buttons in chrome", null, "chrome", "button", null)]
    [InlineData("links in w3", "w3", null, "hyperlink", null)]
    [InlineData("edit boxes in notepad", null, "notepad", "edit", null)]
    [InlineData("find control Next Page in chrome", null, "chrome", null, "Next Page")]
    [InlineData("find link Next Page", null, null, "hyperlink", "Next Page")]
    [InlineData("is there a button called Save in w3", "w3", null, "button", "Save")]
    public void FindControl(string text, string? alias, string? windowName, string? type, string? name)
    {
        var cmd = Parse(text, CommandKind.FindControl);
        Assert.Equal(alias, cmd.Window?.Alias);
        Assert.Equal(windowName, cmd.Window?.NameText);
        Assert.Equal(type, cmd.Control!.ControlType);
        Assert.Equal(name, cmd.Control.NameText);
    }

    [Theory]
    [InlineData("inspect c12")]
    [InlineData("describe c12")]
    [InlineData("what is c12")]
    [InlineData("value of c12")]
    [InlineData("read c12")]
    [InlineData("what does c12 say")]
    public void Inspect(string text) => Assert.Equal("c12", Parse(text, CommandKind.Inspect).Control!.Alias);

    [Theory]
    [InlineData("click c12")]
    [InlineData("click on c12")]
    [InlineData("press c12")]
    [InlineData("tap c12")]
    [InlineData("hit c12")]
    public void ClickAlias(string text)
    {
        var cmd = Parse(text, CommandKind.Click);
        Assert.Equal("c12", cmd.Control!.Alias);
        Assert.Equal(MouseButton.Left, cmd.Button);
        Assert.Equal(1, cmd.Clicks);
    }

    [Theory]
    [InlineData("in the chrome window click on the link Next Page", null, "chrome", "hyperlink")]
    [InlineData("click the Next Page link in chrome", null, "chrome", "hyperlink")]
    [InlineData("click link \"Next Page\" in window w3", "w3", null, "hyperlink")]
    [InlineData("click Next Page", null, null, null)]
    public void ClickNamed(string text, string? alias, string? windowName, string? type)
    {
        var cmd = Parse(text, CommandKind.Click);
        Assert.Equal(alias, cmd.Window?.Alias);
        Assert.Equal(windowName, cmd.Window?.NameText);
        Assert.Equal(type, cmd.Control!.ControlType);
        Assert.Equal("Next Page", cmd.Control.NameText);
    }

    [Theory]
    [InlineData("double click c4", "c4", null, null)]
    [InlineData("double-click on the Report.docx item", null, "listitem", "Report.docx")]
    [InlineData("open c4", "c4", null, null)]
    public void DoubleClick(string text, string? alias, string? type, string? name)
    {
        var cmd = Parse(text, CommandKind.Click);
        Assert.Equal(2, cmd.Clicks);
        Assert.Equal(alias, cmd.Control!.Alias);
        Assert.Equal(type, cmd.Control.ControlType);
        Assert.Equal(name, cmd.Control.NameText);
    }

    [Theory]
    [InlineData("right click c4", "c4", null)]
    [InlineData("right-click on the file", null, "file")]
    [InlineData("context menu on c4", "c4", null)]
    public void RightClick(string text, string? alias, string? name)
    {
        var cmd = Parse(text, CommandKind.Click);
        Assert.Equal(MouseButton.Right, cmd.Button);
        Assert.Equal(alias, cmd.Control!.Alias);
        Assert.Equal(name, cmd.Control.NameText);
    }

    [Fact]
    public void MiddleClick() => Assert.Equal(MouseButton.Middle, Parse("middle click c4", CommandKind.Click).Button);

    [Theory]
    [InlineData("click at 100,200", 100, 200, null, 1, MouseButton.Left)]
    [InlineData("click 100 200", 100, 200, null, 1, MouseButton.Left)]
    [InlineData("click at 100,200 on screen 2", 100, 200, 2, 1, MouseButton.Left)]
    [InlineData("double click at 50,50", 50, 50, null, 2, MouseButton.Left)]
    [InlineData("right click at 300,400", 300, 400, null, 1, MouseButton.Right)]
    public void ClickPoint(string text, int x, int y, int? display, int clicks, MouseButton button)
    {
        var cmd = Parse(text, CommandKind.Click);
        Assert.Equal(new ScreenPoint(x, y), cmd.Point);
        Assert.Equal(display, cmd.DisplayIndex);
        Assert.Equal(display is not null, cmd.PointIsDisplayRelative);
        Assert.Equal(clicks, cmd.Clicks);
        Assert.Equal(button, cmd.Button);
    }

    [Theory]
    [InlineData("invoke c12", "c12", "invoke")]
    [InlineData("activate c12", "c12", "invoke")]
    [InlineData("toggle c7", "c7", "toggle")]
    [InlineData("check c7", "c7", "check")]
    [InlineData("uncheck c7", "c7", "uncheck")]
    [InlineData("select c9", "c9", "select")]
    [InlineData("expand c3", "c3", "expand")]
    [InlineData("collapse c3", "c3", "collapse")]
    public void Invoke(string text, string alias, string action)
    {
        var cmd = Parse(text, CommandKind.Invoke);
        Assert.Equal(alias, cmd.Control!.Alias);
        Assert.Equal(action, cmd.Value);
    }

    [Fact]
    public void OrdinalBeforeTypeWord()
    {
        var cmd = Parse("click the second button", CommandKind.Click);
        Assert.Equal("button", cmd.Control!.ControlType);
        Assert.Equal(2, cmd.Control.Ordinal);
    }

    // ------------------------------------------------------------ values

    [Theory]
    [InlineData("set c12 to \"This is fun!\"", "This is fun!")]
    [InlineData("set c12 = This is fun!", "This is fun!")]
    [InlineData("set the value of c12 to hello", "hello")]
    [InlineData("change c12 to hello", "hello")]
    [InlineData("put \"hello\" in c12", "hello")]
    [InlineData("enter hello into c12", "hello")]
    [InlineData("fill c12 with hello", "hello")]
    [InlineData("make c12 say hello", "hello")]
    [InlineData("clear c12", "")]
    [InlineData("set c12 to play with fire", "play with fire")]
    public void SetValueAlias(string text, string value)
    {
        var cmd = Parse(text, CommandKind.SetValue);
        Assert.Equal("c12", cmd.Control!.Alias);
        Assert.Equal(value, cmd.Value);
    }

    [Fact]
    public void UserSentenceChangeWindowControlId()
    {
        var cmd = Parse("change window w3 in chrome control id c7 and set the value to \"This is fun!\"", CommandKind.SetValue);
        Assert.Equal("w3", cmd.Window!.Alias);
        Assert.Equal("chrome", cmd.Window.Hint);
        Assert.Equal("c7", cmd.Control!.Alias);
        Assert.Equal("This is fun!", cmd.Value);
    }

    [Fact]
    public void RawFormWithOpaqueIds()
    {
        var cmd = Parse("change Window X in chrome control id Y and set the value to \"This is fun!\"", CommandKind.SetValue);
        Assert.Equal("X", cmd.Window!.NameText);
        Assert.Equal("chrome", cmd.Window.Hint);
        Assert.Equal("Y", cmd.Control!.IdText);
        Assert.Null(cmd.Control.Alias);
        Assert.Equal("This is fun!", cmd.Value);
    }

    [Theory]
    [InlineData("in window w3 set control c7 to \"This is fun!\"", "w3", null, "c7", null, null, null, "This is fun!")]
    [InlineData("set control id email in chrome to bob@example.com", null, "chrome", null, "email", null, null, "bob@example.com")]
    [InlineData("set the Email text box in w3 to bob@example.com", "w3", null, null, null, "edit", "Email", "bob@example.com")]
    [InlineData("type \"hello\" into the Search field in chrome", null, "chrome", null, null, "field", "Search", "hello")]
    public void SetValueWithWindow(string text, string? windowAlias, string? windowName, string? controlAlias, string? idText, string? type, string? name, string value)
    {
        var cmd = Parse(text, CommandKind.SetValue);
        Assert.Equal(windowAlias, cmd.Window?.Alias);
        Assert.Equal(windowName, cmd.Window?.NameText);
        Assert.Equal(controlAlias, cmd.Control!.Alias);
        Assert.Equal(idText, cmd.Control.IdText);
        Assert.Equal(type, cmd.Control.ControlType);
        Assert.Equal(name, cmd.Control.NameText);
        Assert.Equal(value, cmd.Value);
    }

    [Theory]
    [InlineData("type hello world", "hello world")]
    [InlineData("type \"hello world\"", "hello world")]
    [InlineData("type: hello", "hello")]
    [InlineData("write hello", "hello")]
    [InlineData("enter text hello", "hello")]
    [InlineData("type the cat in the hat", "the cat in the hat")]
    [InlineData("type ‘curly quotes’", "curly quotes")]
    [InlineData("type “smart quotes”", "smart quotes")]
    public void TypeText(string text, string value)
    {
        var cmd = Parse(text, CommandKind.TypeText);
        Assert.Equal(value, cmd.Value);
        Assert.Null(cmd.Window);
    }

    [Theory]
    [InlineData("press enter", "enter")]
    [InlineData("press ctrl+s", "ctrl+s")]
    [InlineData("hit enter", "enter")]
    [InlineData("key ctrl shift t", "ctrl+shift+t")]
    [InlineData("keys alt+f4", "alt+f4")]
    [InlineData("press the escape key", "escape")]
    [InlineData("hotkey win+r", "win+r")]
    [InlineData("send ctrl+a", "ctrl+a")]
    [InlineData("press Control+Shift+T", "ctrl+shift+t")]
    public void PressKeys(string text, string keys) => Assert.Equal(keys, Parse(text, CommandKind.PressKeys).Keys);

    // ------------------------------------------------------------ mouse

    [Theory]
    [InlineData("move mouse to 100,200", 100, 200, null)]
    [InlineData("mouse 100 200", 100, 200, null)]
    [InlineData("move the cursor to 100,200 on screen 2", 100, 200, 2)]
    public void MoveMousePoint(string text, int x, int y, int? display)
    {
        var cmd = Parse(text, CommandKind.MoveMouse);
        Assert.Equal(new ScreenPoint(x, y), cmd.Point);
        Assert.Equal(display, cmd.DisplayIndex);
        Assert.Equal(display is not null, cmd.PointIsDisplayRelative);
    }

    [Fact]
    public void Hover()
    {
        Assert.Equal("c12", Parse("hover c12", CommandKind.MoveMouse).Control!.Alias);
        var cmd = Parse("hover over the Save button in w3", CommandKind.MoveMouse);
        Assert.Equal("w3", cmd.Window!.Alias);
        Assert.Equal("button", cmd.Control!.ControlType);
        Assert.Equal("Save", cmd.Control.NameText);
    }

    [Theory]
    [InlineData("scroll down", 0, -3, null, null)]
    [InlineData("scroll down 5", 0, -5, null, null)]
    [InlineData("scroll up 3 in chrome", 0, 3, null, "chrome")]
    [InlineData("wheel down", 0, -3, null, null)]
    [InlineData("page down in w3", 0, -3, null, "w3")]
    [InlineData("scroll left 2", -2, 0, null, null)]
    [InlineData("scroll right", 3, 0, null, null)]
    [InlineData("scroll c12 down 2", 0, -2, "c12", null)]
    public void Scroll(string text, int dx, int dy, string? control, string? window)
    {
        var cmd = Parse(text, CommandKind.Scroll);
        Assert.Equal(dx, cmd.ScrollX);
        Assert.Equal(dy, cmd.ScrollY);
        Assert.Equal(control, cmd.Control?.Alias);
        Assert.Equal(window, cmd.Window?.Alias ?? cmd.Window?.NameText);
    }

    [Fact]
    public void ScrollAtPoint() => Assert.Equal(new ScreenPoint(500, 500), Parse("scroll down at 500,500", CommandKind.Scroll).Point);

    // ------------------------------------------------------------ errors

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("frobnicate the widget")]
    [InlineData("clik c12")]
    [InlineData("keys ctrl+nonsense")]
    public void UnknownHasHelpfulError(string text)
    {
        var cmd = RemoteCommandParser.Parse(text);
        Assert.Equal(CommandKind.Unknown, cmd.Kind);
        Assert.False(string.IsNullOrWhiteSpace(cmd.Error));
    }

    [Fact]
    public void MisspelledVerbSuggestsClosestForm()
    {
        var cmd = RemoteCommandParser.Parse("clik c12");
        Assert.Contains("click", cmd.Error);
    }

    [Fact]
    public void NeverThrowsOnOddInput()
    {
        foreach (var text in new[] { "\"", "'", "“", "= = =", ": :", "set", "set to", "click", "type", "wait for", "c12", "w3", "\u0001", "screenshot region" })
        {
            var cmd = RemoteCommandParser.Parse(text);
            Assert.NotNull(cmd);
        }
        Assert.Equal(CommandKind.Unknown, RemoteCommandParser.Parse(null!).Kind);
    }

    // ------------------------------------------------------------ live smoke regressions

    [Theory]
    [InlineData("focus window Remote Control Smoke")]
    [InlineData("switch to window Remote Control Smoke")]
    public void MultiWordWindowNameAfterWindowKeyword(string text) =>
        Assert.Equal("Remote Control Smoke", Parse(text, CommandKind.FocusWindow).Window!.NameText);

    [Fact]
    public void ScreenshotOfMultiWordWindowWithControls()
    {
        var cmd = Parse("give a screenshot of window Remote Control Smoke chrome and the names of all of the controls and their values", CommandKind.Screenshot);
        Assert.Equal(ScreenshotTarget.Window, cmd.Target);
        Assert.True(cmd.IncludeControls);
        Assert.Equal("Remote Control Smoke chrome", cmd.Window!.NameText);
    }

    [Fact]
    public void ControlsInMultiWordWindow()
    {
        var cmd = Parse("controls in Remote Control Page Two", CommandKind.ListControls);
        Assert.Equal("Remote Control Page Two", cmd.Window!.NameText);
    }

    [Theory]
    [InlineData("set the Email text box in Remote Control Page Two to \"This is fun!\"", CommandKind.SetValue, "edit", "Email", "This is fun!")]
    [InlineData("check the Include attachments checkbox in Remote Control Page Two", CommandKind.Invoke, "checkbox", "Include attachments", "check")]
    [InlineData("click the Save button in Remote Control Page Two", CommandKind.Click, "button", "Save", null)]
    public void ControlInMultiWordWindow(string text, CommandKind kind, string type, string name, string? value)
    {
        var cmd = Parse(text, kind);
        Assert.Equal("Remote Control Page Two", cmd.Window!.NameText);
        Assert.Equal(type, cmd.Control!.ControlType);
        Assert.Equal(name, cmd.Control.NameText);
        Assert.Equal(value, cmd.Value);
    }

    [Fact]
    public void WaitForQuotedControlNameIsOpaque()
    {
        var cmd = Parse("wait until a text called \"Saved: This is fun!\" appears in Remote Control Page Two up to 5 seconds", CommandKind.WaitFor);
        Assert.Equal(WaitTarget.ControlAppears, cmd.Wait!.Target);
        Assert.Equal("text", cmd.Control!.ControlType);
        Assert.Equal("Saved: This is fun!", cmd.Control.NameText);
        Assert.Equal("Remote Control Page Two", cmd.Window!.NameText);
        Assert.Equal(TimeSpan.FromSeconds(5), cmd.Delay);
        Assert.Null(cmd.Value);
    }

    [Fact]
    public void ChromeWindowLinkNextPageKeepsFullName()
    {
        var cmd = Parse("in the chrome window click on the link Next Page", CommandKind.Click);
        Assert.Equal("chrome", cmd.Window!.NameText);
        Assert.Equal("hyperlink", cmd.Control!.ControlType);
        Assert.Equal("Next Page", cmd.Control.NameText);
    }

    // ------------------------------------------------------------ batches

    [Theory]
    [InlineData("windows", new[] { "windows" })]
    [InlineData("windows ; screens", new[] { "windows", "screens" })]
    [InlineData("windows\nscreens\r\n\n# comment\n  controls in w3  ", new[] { "windows", "screens", "controls in w3" })]
    [InlineData("set c12 to \"a ; b\" ; press enter", new[] { "set c12 to \"a ; b\"", "press enter" })]
    [InlineData("type “x ; y” ; type 'p ; q'", new[] { "type “x ; y”", "type 'p ; q'" })]
    [InlineData("type a;b ; press enter", new[] { "type a;b", "press enter" })]
    [InlineData("try click c4 ; check c7", new[] { "try click c4", "check c7" })]
    [InlineData("", new string[0])]
    public void SplitCommands(string text, string[] expected) => Assert.Equal(expected, RemoteCommandParser.SplitCommands(text));

    [Fact]
    public void QuotedValueWithSemicolonParsesIntact()
    {
        var step = Assert.Single(RemoteCommandParser.SplitCommands("set c12 to \"x ; y\""));
        Assert.Equal("x ; y", Parse(step, CommandKind.SetValue).Value);
    }

    [Theory]
    [InlineData("try click c4", true)]
    [InlineData("TRY check c7", true)]
    [InlineData("tryst click", false)]
    [InlineData("click c4", false)]
    public void TryPrefix(string step, bool expected) => Assert.Equal(expected, RemoteCommandParser.IsTryStep(step));

    [Theory]
    [InlineData("set the text area in w3 to \"x\"")]
    [InlineData("set the textarea in w3 to \"x\"")]
    [InlineData("set the text view in w3 to \"x\"")]
    [InlineData("set the text editor in w3 to \"x\"")]
    [InlineData("set the editor in w3 to \"x\"")]
    [InlineData("set the document in w3 to \"x\"")]
    public void EditorPhrasesAreTheEditFamily(string text)
    {
        var cmd = Parse(text, CommandKind.SetValue);
        Assert.Equal("document", cmd.Control!.ControlType);
        Assert.Null(cmd.Control.NameText);
        Assert.Equal("w3", cmd.Window!.Alias);
        Assert.Equal("x", cmd.Value);
    }

    [Fact]
    public void TextAreasListByType() => Assert.Equal("document", Parse("text areas in w3", CommandKind.FindControl).Control!.ControlType);

    [Theory]
    [InlineData("headings in w3")]
    [InlineData("list the headings in w3")]
    public void HeadingsAreTextType(string text)
    {
        var cmd = Parse(text, CommandKind.FindControl);
        Assert.Equal("text", cmd.Control!.ControlType);
        Assert.Equal("w3", cmd.Window!.Alias);
    }

    [Fact]
    public void ClickQuotedButtonNameInHandle()
    {
        var cmd = Parse("click the \"Sign in or create account\" button in 133412", CommandKind.Click);
        Assert.Equal("button", cmd.Control!.ControlType);
        Assert.Equal("Sign in or create account", cmd.Control.NameText);
        Assert.Equal(133412, cmd.Window!.Handle);
    }

    // ------------------------------------------------------------ full review: parser

    // 1. primary modifier
    [Theory]
    [InlineData("primary+a", "primary+a")]
    [InlineData("mod+a", "primary+a")]
    [InlineData("cmdorctrl+shift+z", "primary+shift+z")]
    [InlineData("ctrl+primary+a", "primary+ctrl+a")]
    public void PrimaryModifier(string chord, string expected) => Assert.Equal(expected, KeyChord.Normalize(chord));

    [Fact]
    public void PrimaryIsAModifier()
    {
        Assert.True(KeyChord.IsModifier("primary"));
        Assert.True(KeyChord.IsModifier("mod"));
        Assert.False(KeyChord.IsModifier("a"));
    }

    // 2. quotes never swallow later lines
    [Fact]
    public void StrayInchMarkDoesNotSwallowNextLine()
    {
        var steps = RemoteCommandParser.SplitCommands("type 5\" monitor\nclick c3");
        Assert.Equal(["type 5\" monitor", "click c3"], steps);
        Assert.Equal("5\" monitor", Parse(steps[0], CommandKind.TypeText).Value);
    }

    [Fact]
    public void UnmatchedQuoteIsLiteralAndNeverSpansLines()
    {
        Assert.Equal(["type \"abc", "click c3"], RemoteCommandParser.SplitCommands("type \"abc\nclick c3"));
        Assert.Equal("\"abc", Parse("type \"abc", CommandKind.TypeText).Value);
    }

    // 3. press: keys, repeats, sequences
    [Theory]
    [InlineData("press tab 3 times", new[] { "tab" }, 3)]
    [InlineData("press enter twice", new[] { "enter" }, 2)]
    [InlineData("press down x5", new[] { "down" }, 5)]
    [InlineData("press ctrl+c ctrl+v", new[] { "ctrl+c", "ctrl+v" }, 1)]
    [InlineData("press ctrl+c then ctrl+v", new[] { "ctrl+c", "ctrl+v" }, 1)]
    [InlineData("press ctrl+c, ctrl+v", new[] { "ctrl+c", "ctrl+v" }, 1)]
    [InlineData("press ctrl shift t", new[] { "ctrl+shift+t" }, 1)]
    [InlineData("hit escape", new[] { "escape" }, 1)]
    [InlineData("select all", new[] { "primary+a" }, 1)]
    public void KeySequences(string text, string[] sequence, int repeat)
    {
        var cmd = Parse(text, CommandKind.PressKeys);
        Assert.Equal(sequence, cmd.KeySequence);
        Assert.Equal(sequence[0], cmd.Keys);
        Assert.Equal(repeat, cmd.Repeat);
    }

    [Theory]
    [InlineData("press ctrl+foo")]
    [InlineData("press enter banana")]
    public void BadKeysAreUnknownNotClicks(string text)
    {
        var cmd = RemoteCommandParser.Parse(text);
        Assert.Equal(CommandKind.Unknown, cmd.Kind);
        Assert.Contains("Unknown key", cmd.Error);
    }

    [Fact]
    public void PressAButtonIsStillAClick()
    {
        var cmd = Parse("press the Home button", CommandKind.Click);
        Assert.Equal("button", cmd.Control!.ControlType);
        Assert.Equal("Home", cmd.Control.NameText);
    }

    // 4. trailing words that belong to the name
    [Theory]
    [InlineData("click Log in", CommandKind.Click, null, "Log in")]
    [InlineData("click the Sign in button", CommandKind.Click, "button", "Sign in")]
    [InlineData("uncheck the Remember me checkbox", CommandKind.Invoke, "checkbox", "Remember me")]
    [InlineData("click Show all", CommandKind.Click, null, "Show all")]
    [InlineData("click Go", CommandKind.Click, null, "Go")]
    [InlineData("click log in", CommandKind.Click, null, "log in")]
    // 5. capitalized type words inside names
    [InlineData("click on the Page 2 link", CommandKind.Click, "hyperlink", "Page 2")]
    [InlineData("click the Text link", CommandKind.Click, "hyperlink", "Text")]
    [InlineData("click the Edit menu", CommandKind.Click, "menuitem", "Edit")]
    [InlineData("click the Item 3 row", CommandKind.Click, "listitem", "Item 3")]
    [InlineData("click the Link Page link", CommandKind.Click, "hyperlink", "Link Page")]
    [InlineData("click Page 2", CommandKind.Click, null, "Page 2")]
    [InlineData("click link Next Page", CommandKind.Click, "hyperlink", "Next Page")]
    public void NamesKeepTheirWords(string text, CommandKind kind, string? type, string name)
    {
        var cmd = Parse(text, kind);
        Assert.Equal(type, cmd.Control!.ControlType);
        Assert.Equal(name, cmd.Control.NameText);
        Assert.Null(cmd.Window);
    }

    // 6. dialogs, sheets and popups are windows
    [Fact]
    public void DialogIsAWindowEverywhere()
    {
        var click = Parse("click Save in the Save As dialog", CommandKind.Click);
        Assert.Equal("Save As", click.Window!.NameText);
        Assert.Equal("Save", click.Control!.NameText);
        Assert.Equal("Save As", Parse("switch to the Save As dialog", CommandKind.FocusWindow).Window!.NameText);
        var list = Parse("list buttons in the Save As dialog", CommandKind.FindControl);
        Assert.Equal("Save As", list.Window!.NameText);
        Assert.Equal("button", list.Control!.ControlType);
        Assert.Equal("Print", Parse("focus the Print sheet", CommandKind.FocusWindow).Window!.NameText);
    }

    // 7. window names with lowercase words
    [Fact]
    public void WindowNamesKeepLowercaseWords()
    {
        Assert.Equal("Welcome to Chrome", Parse("focus window Welcome to Chrome", CommandKind.FocusWindow).Window!.NameText);
        var click = Parse("click Save in window Welcome to Chrome", CommandKind.Click);
        Assert.Equal("Welcome to Chrome", click.Window!.NameText);
        Assert.Equal("Save", click.Control!.NameText);
        Assert.Equal("Welcome to Chrome", Parse("controls in window Welcome to Chrome", CommandKind.ListControls).Window!.NameText);
        Assert.Equal("Welcome to chrome", Parse("screenshot window Welcome to chrome", CommandKind.Screenshot).Window!.NameText);
    }

    // 10. box is the edit family
    [Theory]
    [InlineData("set the Email box in chrome to bob@example.com", CommandKind.SetValue, "Email", "bob@example.com")]
    [InlineData("clear the Search box", CommandKind.SetValue, "Search", "")]
    [InlineData("activate the Search box", CommandKind.Invoke, "Search", "invoke")]
    public void BoxIsEdit(string text, CommandKind kind, string name, string value)
    {
        var cmd = Parse(text, kind);
        Assert.Equal("edit", cmd.Control!.ControlType);
        Assert.Equal(name, cmd.Control.NameText);
        Assert.Equal(value, cmd.Value);
    }

    // 11. names containing "to"
    [Theory]
    [InlineData("set the Ship to field to Boston", "Ship to", "Boston")]
    [InlineData("set c12 to go to sleep", null, "go to sleep")]
    public void ToInsideNames(string text, string? name, string value)
    {
        var cmd = Parse(text, CommandKind.SetValue);
        Assert.Equal(name, cmd.Control!.NameText);
        Assert.Equal(value, cmd.Value);
    }

    // 12. wait conditions
    [Fact]
    public void WaitConditionWords()
    {
        var enabled = Parse("wait for the Next button to be enabled", CommandKind.WaitFor);
        Assert.Equal(WaitTarget.ControlEnabled, enabled.Wait!.Target);
        Assert.Equal("Next", enabled.Control!.NameText);
        Assert.Null(enabled.Value);
        var shows = Parse("wait until the Downloads window shows up", CommandKind.WaitFor);
        Assert.Equal(WaitTarget.WindowAppears, shows.Wait!.Target);
        Assert.Equal("Downloads", shows.Window!.NameText);
        Assert.Equal(WaitTarget.WindowAppears, Parse("wait until the Downloads window is open", CommandKind.WaitFor).Wait!.Target);
        Assert.Equal(WaitTarget.ControlGone, Parse("wait for the Spinner image to be gone", CommandKind.WaitFor).Wait!.Target);
    }

    // 13. screens and window numbers
    [Fact]
    public void ScreenNumbersAndWindowNumbers()
    {
        var two = Parse("screenshot of screen two", CommandKind.Screenshot);
        Assert.Equal(ScreenshotTarget.Display, two.Target);
        Assert.Equal(2, two.DisplayIndex);
        Assert.Equal(CommandKind.Unknown, RemoteCommandParser.Parse("screenshot screen foo").Kind);
        Assert.Equal("w2", Parse("screenshot of window 2", CommandKind.Screenshot).Window!.Alias);
        var bare = Parse("screenshot 2", CommandKind.Screenshot);
        Assert.Equal(ScreenshotTarget.Display, bare.Target);
        Assert.Equal(2, bare.DisplayIndex);
        Assert.Equal("w3", Parse("focus window 3", CommandKind.FocusWindow).Window!.Alias);
    }

    // 21. modifier clicks and drags
    [Theory]
    [InlineData("shift click c3", "shift")]
    [InlineData("ctrl click c3", "ctrl")]
    [InlineData("shift+click c3", "shift")]
    [InlineData("control-click c3", "ctrl")]
    [InlineData("cmd click c3", "primary")]
    [InlineData("ctrl shift click c3", "ctrl+shift")]
    public void ModifierClicks(string text, string modifiers)
    {
        var cmd = Parse(text, CommandKind.Click);
        Assert.Equal(modifiers, cmd.Modifiers);
        Assert.Equal("c3", cmd.Control!.Alias);
    }

    [Fact]
    public void Drags()
    {
        var controls = Parse("drag c3 to c4", CommandKind.Drag);
        Assert.Equal("c3", controls.Control!.Alias);
        Assert.Equal("c4", controls.TargetControl!.Alias);
        var toPoint = Parse("drag c3 to 100,200", CommandKind.Drag);
        Assert.Equal("c3", toPoint.Control!.Alias);
        Assert.Equal(new ScreenPoint(100, 200), toPoint.TargetPoint);
        var points = Parse("drag from 10,10 to 50,50", CommandKind.Drag);
        Assert.Equal(new ScreenPoint(10, 10), points.Point);
        Assert.Equal(new ScreenPoint(50, 50), points.TargetPoint);
        var named = Parse("drag the Report item to the Archive folder in w3", CommandKind.Drag);
        Assert.Equal("listitem", named.Control!.ControlType);
        Assert.Equal("Report", named.Control.NameText);
        Assert.Equal("Archive folder", named.TargetControl!.NameText);
        Assert.Equal("w3", named.Window!.Alias);
    }

    // 22. no silent caps
    [Fact]
    public void LargeScrollIsKept() => Assert.Equal(-2000, Parse("scroll down 2000", CommandKind.Scroll).ScrollY);

    // 24. reading a value
    [Theory]
    [InlineData("what is the value of the Email field in chrome", "chrome")]
    [InlineData("read the Email field", null)]
    [InlineData("what does the Email field say", null)]
    public void ReadValue(string text, string? window)
    {
        var cmd = Parse(text, CommandKind.Inspect);
        Assert.Equal("field", cmd.Control!.ControlType);
        Assert.Equal("Email", cmd.Control.NameText);
        Assert.Equal(window, cmd.Window?.NameText);
    }

    [Theory]
    [InlineData("wait for an edit box in w3 up to 10 seconds", "edit")]
    [InlineData("wait for the Email text box in w3", "edit")]
    [InlineData("wait for the Search entry in w3", "edit")]
    [InlineData("wait for the Search box in w3", "edit")]
    [InlineData("wait for the Search field in w3", "field")]
    [InlineData("wait for the Search input in w3", "field")]
    [InlineData("wait for the text area in w3", "document")]
    [InlineData("wait for the editor in w3", "document")]
    [InlineData("wait for the page in w3", "document")]
    public void SingleLineAndMultiLineTypeWords(string text, string type) =>
        Assert.Equal(type, Parse(text, CommandKind.WaitFor).Control!.ControlType);

    [Theory]
    [InlineData("guard off", "off", 0)]
    [InlineData("guard on", "on", -1)]
    [InlineData("guard 500 ms", "set", 500)]
    [InlineData("guard 500", "set", 500)]
    [InlineData("guard 1 s", "set", 1000)]
    [InlineData("guard", "status", -1)]
    public void GuardCommand(string text, string value, int ms)
    {
        var cmd = Parse(text, CommandKind.SetGuard);
        Assert.Equal(value, cmd.Value);
        Assert.Equal(ms < 0 ? null : TimeSpan.FromMilliseconds(ms), cmd.Delay);
    }
}
