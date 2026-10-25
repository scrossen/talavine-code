namespace SwarmCore.RemoteControl.Tests;

public sealed class RemoteControlSessionTests : IDisposable
{
    private const long Notepad = 100;
    private const long Chrome = 200;

    private readonly string captureDir = Path.Combine(Path.GetTempPath(), "rc-session-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeDesktopAutomation fake = new();
    private DateTimeOffset now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private Action? onSleep;
    private RemoteControlSession session;

    public RemoteControlSessionTests()
    {
        fake.AddWindow(Notepad, "notepad", "Untitled - Notepad", foreground: true,
            controls: FakeDesktopAutomation.Control("n1", "edit", "Text Editor", new ScreenRect(10, 40, 700, 500), "", 1, ControlAction.SetValue));
        fake.AddWindow(Chrome, "chrome", "Example - Google Chrome", new ScreenRect(0, 0, 1200, 800), false,
            FakeDesktopAutomation.Control("k1", "hyperlink", "Next Page", new ScreenRect(100, 100, 80, 20), null, 2, ControlAction.Invoke),
            FakeDesktopAutomation.Control("k2", "edit", "Email", new ScreenRect(100, 200, 200, 24), "", 2, ControlAction.SetValue),
            FakeDesktopAutomation.Control("k3", "checkbox", "Remember me", new ScreenRect(100, 240, 120, 20), "off", 2, ControlAction.Toggle) with { IsToggled = false },
            FakeDesktopAutomation.Control("k4", "button", "Save", new ScreenRect(100, 300, 60, 24), null, 2, ControlAction.Invoke),
            FakeDesktopAutomation.Control("k5", "text", "Status", new ScreenRect(100, 340, 100, 20), "Idle", 2),
            FakeDesktopAutomation.Control("k7", "edit", "Name", new ScreenRect(100, 380, 200, 24), "", 2));
        session = CreateSession(new RemoteControlSessionOptions { CaptureDirectory = captureDir });
    }

    private RemoteControlSession CreateSession(RemoteControlSessionOptions options) => new(fake, options)
    {
        Now = () => now,
        Sleep = d => { now += d; onSleep?.Invoke(); },
    };

    public void Dispose()
    {
        try { if (Directory.Exists(captureDir)) Directory.Delete(captureDir, recursive: true); }
        catch (IOException) { }
    }

    private CommandResult Ok(string text)
    {
        var result = session.Execute(text);
        Assert.True(result.Succeeded, $"'{text}' failed: {result.Text}");
        return result;
    }

    private CommandResult Fails(string text, string category)
    {
        var result = session.Execute(text);
        Assert.False(result.Succeeded, $"'{text}' unexpectedly succeeded: {result.Text}");
        Assert.Equal(category, result.FailureCategory);
        if (category == FailureCategories.Timeout) Assert.False(Assert.IsType<WaitForReceipt>(result.Data).Satisfied);
        else Assert.Equal(category, Assert.IsType<CommandFailure>(result.Data).Category);
        return result;
    }

    private string AliasOf(string runtimeKey, string window = "chrome")
    {
        var listing = Assert.IsType<ControlListing>(Ok($"controls in {window}").Data);
        return listing.Controls.Single(c => c.Control.RuntimeKey == runtimeKey).Alias;
    }

    private string WindowAliasOf(long handle) =>
        Assert.IsType<WindowListing>(Ok("windows").Data).Windows.Single(w => w.Window.Handle == handle).Alias;

    // ------------------------------------------------------------ aliases and listings

    [Fact]
    public void AliasesStayStableAcrossListings()
    {
        var first = Assert.IsType<WindowListing>(Ok("windows").Data).Windows.ToDictionary(w => w.Window.Handle, w => w.Alias);
        fake.AddWindow(300, "excel", "Book1 - Excel");
        var second = Assert.IsType<WindowListing>(Ok("list apps").Data).Windows.ToDictionary(w => w.Window.Handle, w => w.Alias);
        Assert.Equal(first[Notepad], second[Notepad]);
        Assert.Equal(first[Chrome], second[Chrome]);
        Assert.DoesNotContain(second[300], first.Values);

        var controlsBefore = Assert.IsType<ControlListing>(Ok("controls in chrome").Data).Controls.ToDictionary(c => c.Control.RuntimeKey, c => c.Alias);
        fake.ControlTrees[Chrome].Insert(0, FakeDesktopAutomation.Control("k0", "button", "Back", new ScreenRect(5, 5, 20, 20), null, 2, ControlAction.Invoke));
        var controlsAfter = Assert.IsType<ControlListing>(Ok("controls in chrome").Data).Controls.ToDictionary(c => c.Control.RuntimeKey, c => c.Alias);
        foreach (var (key, alias) in controlsBefore) Assert.Equal(alias, controlsAfter[key]);
    }

    [Fact]
    public void ControlListingIsAnAlignedTableWithEveryRow()
    {
        fake.UpdateControl(Chrome, "k5", c => c with { Value = "line one\nline two" });
        var text = Ok("controls in chrome").Text;
        Assert.Contains("line one⏎line two", text);
        Assert.Contains("Next Page", text);
        Assert.Contains("Alias", text);
        Assert.Equal(6, Assert.IsType<ControlListing>(session.Execute("controls in chrome").Data).Controls.Count);
    }

    // ------------------------------------------------------------ click

    [Fact]
    public void ClickByNameInNamedWindowFocusesWindowAndClicksCenter()
    {
        var result = Ok("in the chrome window click on the link Next Page");
        Assert.Equal(Chrome, fake.FocusRequests.Last());
        var click = Assert.Single(fake.Clicks);
        Assert.Equal(new ScreenPoint(140, 110), click.Point);
        Assert.Equal(MouseButton.Left, click.Button);
        var receipt = Assert.IsType<ActionReceipt>(result.Data);
        Assert.Equal("mouse-click", receipt.Method);
        Assert.Equal(new ScreenPoint(140, 110), receipt.Point);
        Assert.NotNull(receipt.ObservedState);
    }

    [Fact]
    public void ClickFallsBackToInvokeWhenControlHasNoBounds()
    {
        fake.UpdateControl(Chrome, "k4", c => c with { Bounds = default });
        var receipt = Assert.IsType<ActionReceipt>(Ok("click the Save button in chrome").Data);
        Assert.Equal("invoke-pattern", receipt.Method);
        Assert.Empty(fake.Clicks);
        Assert.Contains(fake.Actions, a => a.Key == "k4" && a.Action == ControlAction.Invoke);
    }

    [Fact]
    public void AmbiguousWindowRefusesActionAndListsCandidates()
    {
        fake.AddWindow(201, "chrome", "Other - Google Chrome");
        var result = Fails("click Save in chrome", FailureCategories.Ambiguous);
        Assert.Contains("Example - Google Chrome", result.Text);
        Assert.Contains("Other - Google Chrome", result.Text);
        Assert.Empty(fake.Clicks);
    }

    [Fact]
    public void DuplicateExactNamesAreTieBrokenNotRefused()
    {
        // Same name and type twice (page header and body): the larger on-screen one wins, with a note.
        fake.ControlTrees[Chrome].Add(FakeDesktopAutomation.Control("k6", "button", "Save", new ScreenRect(400, 300, 120, 40), null, 2, ControlAction.Invoke));
        var result = Ok("click the Save button in chrome");
        Assert.Equal(new ScreenPoint(460, 320), fake.Clicks.Single().Point);
        Assert.Matches(@"Using c\d+; also matching: c\d+\.", result.Text);
    }

    [Fact]
    public void DuplicateNamesPreferOnScreenThenFocused()
    {
        fake.UpdateControl(Chrome, "k4", c => c with { IsOffscreen = true });
        fake.ControlTrees[Chrome].Add(FakeDesktopAutomation.Control("k6", "button", "Save", new ScreenRect(400, 300, 10, 10), null, 2, ControlAction.Invoke));
        Ok("click the Save button in chrome");
        Assert.Equal(new ScreenPoint(405, 305), fake.Clicks.Single().Point);

        fake.UpdateControl(Chrome, "k4", c => c with { IsOffscreen = false, HasKeyboardFocus = true });
        Ok("click the Save button in chrome");
        Assert.Equal(new ScreenPoint(130, 312), fake.Clicks.Last().Point);
    }

    [Fact]
    public void TiesBetweenDifferentTypesAreRefused()
    {
        fake.ControlTrees[Chrome].Add(FakeDesktopAutomation.Control("k6", "text", "Save", new ScreenRect(400, 300, 60, 24), null, 2));
        var result = Fails("click Save in chrome", FailureCategories.Ambiguous);
        Assert.Matches(@"c\d+ button 'Save' at 100,300", result.Text);
        Assert.Matches(@"c\d+ text 'Save' at 400,300", result.Text);
        Assert.Empty(fake.Clicks);
    }

    [Fact]
    public void PartialSelectorMatchingDifferentNamesIsRefusedButExactWins()
    {
        fake.ControlTrees[Chrome].Add(FakeDesktopAutomation.Control("k6", "button", "Save As...", new ScreenRect(400, 300, 60, 24), null, 2, ControlAction.Invoke));
        Fails("click the Sav button in chrome", FailureCategories.Ambiguous);
        Assert.Empty(fake.Clicks);
        Ok("click the Save button in chrome");
        Assert.Equal(new ScreenPoint(130, 312), fake.Clicks.Single().Point);
    }

    [Fact]
    public void HeadingsListTextAndHeadingControls()
    {
        fake.ControlTrees[Chrome].Add(FakeDesktopAutomation.Control("k8", "heading", "Welcome", new ScreenRect(0, 0, 300, 30), null, 2));
        var listing = Assert.IsType<ControlListing>(Ok("headings in chrome").Data);
        Assert.Contains(listing.Controls, c => c.Control.RuntimeKey == "k8");
        Assert.Contains(listing.Controls, c => c.Control.RuntimeKey == "k5");
    }

    [Fact]
    public void AmbiguousWindowStillResolvesForReads()
    {
        fake.AddWindow(201, "chrome", "Other - Google Chrome");
        var result = Ok("find window chrome");
        var data = Assert.IsType<WindowSearchResult>(result.Data);
        Assert.Single(data.Alternatives);
    }

    [Fact]
    public void DisplayRelativeClickIsOffsetByDisplayBounds()
    {
        fake.Displays.Add(new DisplayInfo(2, @"\\.\DISPLAY2", false, new ScreenRect(1920, -100, 1280, 1024), new ScreenRect(1920, -100, 1280, 984), 1.25));
        Ok("click at 100,200 on screen 2");
        Assert.Equal(new ScreenPoint(2020, 100), fake.Clicks.Single().Point);
        Fails("click at 1,1 on screen 7", FailureCategories.NotFound);
    }

    // ------------------------------------------------------------ set value

    [Fact]
    public void SetValueUsesPatternAndVerifies()
    {
        var result = Ok("set the Email text box in chrome to bob@example.com");
        Assert.Equal("bob@example.com", fake.Get(Chrome, "k2").Value);
        var receipt = Assert.IsType<ActionReceipt>(result.Data);
        Assert.Equal("value-pattern", receipt.Method);
        Assert.Equal("verified", receipt.Outcome);
        Assert.Empty(fake.TypedText);
        Assert.Contains("verified", result.Text);
    }

    [Fact]
    public void SetValueFallsBackToKeyboardWhenPatternUnsupported()
    {
        var result = Ok("set the Name field in chrome to Ann");
        Assert.Equal(["primary+a"], fake.KeyChords);
        Assert.Equal(["Ann"], fake.TypedText);
        Assert.Equal(new ScreenPoint(200, 392), fake.Clicks.Single().Point);
        Assert.Equal("Ann", fake.Get(Chrome, "k7").Value);
        var receipt = Assert.IsType<ActionReceipt>(result.Data);
        Assert.Equal("keyboard-replace", receipt.Method);
        Assert.Equal("verified", receipt.Outcome);
    }

    [Fact]
    public void SetValueFallsBackToKeyboardWhenPatternDoesNotTake()
    {
        fake.SetValueIgnored.Add("k2");
        var receipt = Assert.IsType<ActionReceipt>(Ok("set the Email text box in chrome to bob@example.com").Data);
        Assert.Equal("keyboard-replace", receipt.Method);
        Assert.Equal("bob@example.com", fake.Get(Chrome, "k2").Value);
        Assert.Contains(receipt.Notes, n => n.Contains("typing instead"));
    }

    [Fact]
    public void SetValueByAliasInUserSentence()
    {
        var w = WindowAliasOf(Chrome);
        var c = AliasOf("k2");
        var result = Ok($"change window {w} in chrome control id {c} and set the value to \"This is fun!\"");
        Assert.Equal("This is fun!", fake.Get(Chrome, "k2").Value);
        Assert.DoesNotContain("Note:", result.Text);
    }

    [Fact]
    public void HintMismatchIsAWarningNotARefusal()
    {
        var w = WindowAliasOf(Notepad);
        var result = Ok($"screenshot of window {w} chrome");
        Assert.Contains("does not match", result.Text);
    }

    [Fact]
    public void ReadOnlyControlIsRefused()
    {
        fake.UpdateControl(Chrome, "k2", c => c with { IsReadOnly = true });
        Fails("set the Email text box in chrome to x", FailureCategories.Unsupported);
    }

    // ------------------------------------------------------------ ensure semantics

    [Fact]
    public void SetToCurrentValueIsUnchangedNoOp()
    {
        var result = Ok("set the Status text in chrome to Idle");
        Assert.Equal("unchanged", Assert.IsType<ActionReceipt>(result.Data).Outcome);
        Assert.Contains("unchanged", result.Text);
        Assert.Empty(fake.Actions);
        Assert.Empty(fake.TypedText);
    }

    [Fact]
    public void CheckWhenAlreadyOnIsUnchangedAndUncheckFlipsOnce()
    {
        var c = AliasOf("k3");
        var on = Ok($"check {c}");
        Assert.Equal("verified", Assert.IsType<ActionReceipt>(on.Data).Outcome);
        Assert.True(fake.Get(Chrome, "k3").IsToggled);

        var again = Ok($"check {c}");
        Assert.Equal("unchanged", Assert.IsType<ActionReceipt>(again.Data).Outcome);
        Assert.Contains("already on", again.Text);
        Assert.Single(fake.Actions, a => a.Action == ControlAction.Toggle);

        Ok($"uncheck {c}");
        Assert.False(fake.Get(Chrome, "k3").IsToggled);
        Assert.Contains("already off", Ok($"uncheck {c}").Text);
        Assert.Equal(2, fake.Actions.Count(a => a.Action == ControlAction.Toggle));
    }

    [Fact]
    public void FocusReportsVerifiedForeground()
    {
        var result = Ok("switch to chrome");
        var receipt = Assert.IsType<ActionReceipt>(result.Data);
        Assert.Equal("verified", receipt.Outcome);
        Assert.True(fake.GetWindow(Chrome)!.IsForeground);
    }

    [Fact]
    public void ScrollSaysIssuedNotVerified() => Assert.Contains("issued, not verified", Ok("scroll down 2").Text);

    // ------------------------------------------------------------ screenshots

    [Fact]
    public void ScreenshotWritesPngAndPrunesBeyondRetention()
    {
        session = CreateSession(new RemoteControlSessionOptions { CaptureDirectory = captureDir, MaxRetainedCaptures = 2, CaptureMaxAge = TimeSpan.Zero });
        string? last = null;
        for (var i = 0; i < 4; i++)
        {
            var result = Ok("screenshot");
            last = Assert.Single(result.Files);
            Assert.True(File.Exists(last));
            Assert.StartsWith("screen1-", Path.GetFileName(last));
            File.SetLastWriteTimeUtc(last, DateTime.UtcNow.AddMinutes(-10 + i));
        }
        var files = Directory.GetFiles(captureDir, "*.png");
        Assert.Equal(2, files.Length);
        Assert.Contains(files, f => string.Equals(Path.GetFullPath(f), last, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WindowScreenshotWithControlsListsControlsAndSetsCurrentWindow()
    {
        var result = Ok("give a screenshot of chrome with controls");
        var receipt = Assert.IsType<ScreenshotReceipt>(result.Data);
        Assert.StartsWith(receipt.Window!.Alias + "-chrome-", Path.GetFileName(receipt.Path));
        Assert.NotNull(receipt.Controls);
        Assert.Null(receipt.PngBase64);
        Assert.Contains("Next Page", result.Text);
        Assert.Equal(Chrome, session.CurrentWindowHandle);
    }

    [Fact]
    public void ScreenshotEmbedsBase64WhenAsked()
    {
        session = CreateSession(new RemoteControlSessionOptions { CaptureDirectory = captureDir, EmbedScreenshotBase64 = true });
        var receipt = Assert.IsType<ScreenshotReceipt>(Ok("screenshot screen 1").Data);
        Assert.False(string.IsNullOrEmpty(receipt.PngBase64));
        Fails("screenshot screen 3", FailureCategories.NotFound);
    }

    // ------------------------------------------------------------ session state

    [Fact]
    public void UnknownCommandSurfacesParserError()
    {
        var result = Fails("frobnicate the widget", FailureCategories.NotUnderstood);
        Assert.Contains("frobnicate", result.Text);
        Assert.Contains("help", result.Text);
    }

    [Fact]
    public void CurrentWindowIsRememberedForLaterControlCommands()
    {
        Assert.Null(session.CurrentWindowHandle);
        Ok("controls in chrome");
        Assert.Equal(Chrome, session.CurrentWindowHandle);
        Ok("click Save");
        Assert.Equal(new ScreenPoint(130, 312), fake.Clicks.Single().Point);
    }

    [Fact]
    public void NoCurrentWindowUsesForegroundAndSaysSo()
    {
        var result = Ok("controls");
        Assert.Contains("foreground window", result.Text);
        Assert.Equal(Notepad, session.CurrentWindowHandle);
    }

    [Fact]
    public void WhereReportsMouseControlFocusAndForeground()
    {
        fake.FocusWindow(Chrome);
        fake.MousePosition = new ScreenPoint(120, 105);
        fake.FocusedKey = "k2";
        var result = Ok("where");
        var report = Assert.IsType<WhereReport>(result.Data);
        Assert.Equal(new ScreenPoint(120, 105), report.Mouse);
        Assert.Equal(1, report.DisplayIndex);
        Assert.Equal("Next Page", report.ControlUnderMouse!.Control.Name);
        Assert.Equal("Email", report.FocusedControl!.Control.Name);
        Assert.Equal(Chrome, report.ForegroundWindow!.Window.Handle);
        Assert.Contains("Next Page", result.Text);
        Assert.Contains("Example - Google Chrome", result.Text);
    }

    [Fact]
    public void InspectPrintsEveryProperty()
    {
        var text = Ok($"inspect {AliasOf("k3")}").Text;
        foreach (var property in new[] { "Type:", "Name:", "Value:", "AutomationId:", "Enabled:", "Bounds:", "RuntimeKey:", "Toggled:", "Actions:" })
            Assert.Contains(property, text);
    }

    [Fact]
    public void PlainWaitUsesInjectedSleep()
    {
        var before = now;
        var receipt = Assert.IsType<WaitReceipt>(Ok("wait 2").Data);
        Assert.Equal(TimeSpan.FromSeconds(2), receipt.Delay);
        Assert.Equal(before + TimeSpan.FromSeconds(2), now);
    }

    // ------------------------------------------------------------ wait-for

    [Fact]
    public void WaitForWindowAppears()
    {
        var polls = 0;
        onSleep = () => { if (++polls == 3) fake.AddWindow(400, "notepad", "Save As"); };
        var result = Ok("wait for window \"Save As\"");
        var receipt = Assert.IsType<WaitForReceipt>(result.Data);
        Assert.True(receipt.Satisfied);
        Assert.Equal(TimeSpan.FromMilliseconds(225), receipt.Elapsed);
        Assert.Equal(400, receipt.Window!.Window.Handle);
    }

    [Fact]
    public void WaitForWindowGone()
    {
        var w = WindowAliasOf(Notepad);
        onSleep = () => fake.RemoveWindow(Notepad);
        var receipt = Assert.IsType<WaitForReceipt>(Ok($"wait until {w} is gone").Data);
        Assert.Equal(WaitTarget.WindowGone, receipt.Target);
        Assert.Equal(TimeSpan.FromMilliseconds(75), receipt.Elapsed);
    }

    [Fact]
    public void WaitForControlValueEquals()
    {
        var c = AliasOf("k5");
        var polls = 0;
        onSleep = () => { if (++polls == 2) fake.UpdateControl(Chrome, "k5", x => x with { Value = " done " }); };
        var result = Ok($"wait until {c} says Done");
        Assert.Contains("Done after", result.Text);
        Assert.Equal(TimeSpan.FromMilliseconds(150), Assert.IsType<WaitForReceipt>(result.Data).Elapsed);
    }

    [Fact]
    public void WaitForControlChangesAndAppears()
    {
        var c = AliasOf("k5");
        onSleep = () => fake.UpdateControl(Chrome, "k5", x => x with { Value = "Busy" });
        Ok($"wait for {c} to change");

        onSleep = () => fake.ControlTrees[Chrome].Add(FakeDesktopAutomation.Control("k9", "button", "Done", new ScreenRect(1, 1, 5, 5), null, 2, ControlAction.Invoke));
        var receipt = Assert.IsType<WaitForReceipt>(Ok("wait for the Done button in chrome").Data);
        Assert.Equal("k9", receipt.Control!.Control.RuntimeKey);
    }

    [Fact]
    public void WaitForTimesOutWithLastObservedState()
    {
        var c = AliasOf("k5");
        var result = Fails($"wait until {c} says Done up to 2 seconds", FailureCategories.Timeout);
        Assert.Contains("Idle", result.Text);
        Assert.Contains("2 s", result.Text);
        Assert.True(now - new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero) >= TimeSpan.FromSeconds(2));
    }

    // ------------------------------------------------------------ failure categories

    [Fact]
    public void WindowGoneCategory()
    {
        var w = WindowAliasOf(Notepad);
        fake.RemoveWindow(Notepad);
        Fails($"focus {w}", FailureCategories.WindowGone);
    }

    [Fact]
    public void ControlGoneCategory()
    {
        var c = AliasOf("k4");
        fake.ControlTrees[Chrome].RemoveAll(x => x.RuntimeKey == "k4");
        Fails($"click {c}", FailureCategories.ControlGone);
    }

    [Fact]
    public void NotFoundCategories()
    {
        Fails("focus excel", FailureCategories.NotFound);
        Fails("click the Launch button in chrome", FailureCategories.NotFound);
        Fails("click c99", FailureCategories.NotFound);
    }

    [Fact]
    public void UnsupportedCategory() => Fails($"expand {AliasOf("k4")}", FailureCategories.Unsupported);

    [Fact]
    public void OsRefusedWhenFocusIsDenied()
    {
        fake.RefuseFocus = true;
        Fails("click Save in chrome", FailureCategories.OsRefused);
    }

    [Fact]
    public void TargetElevatedWhenFocusIsDeniedForElevatedWindow()
    {
        fake.RefuseFocus = true;
        var i = fake.Windows.FindIndex(w => w.Handle == Chrome);
        fake.Windows[i] = fake.Windows[i] with { IsElevated = true };
        Fails("switch to chrome", FailureCategories.TargetElevated);
    }

    [Fact]
    public void PlatformExceptionCategoryIsPassedThrough()
    {
        var c = AliasOf("k1");
        fake.NextActionException = new DesktopAutomationException("The app is not responding.", FailureCategories.AppBusy);
        var result = Fails($"invoke {c}", FailureCategories.AppBusy);
        Assert.Contains("not responding", result.Text);

        fake.NextActionException = new DesktopAutomationException("Something odd.");
        Fails($"invoke {c}", FailureCategories.OsRefused);
    }

    [Fact]
    public void HelpTextIsCompactAndGrouped()
    {
        var lines = RemoteControlSession.HelpText.Split('\n');
        Assert.True(lines.Length < 70, $"help has {lines.Length} lines");
        foreach (var section in new[] { "Session", "Screens", "Windows", "Controls", "Mouse" }) Assert.Contains(section, RemoteControlSession.HelpText);
        Assert.Equal(RemoteControlSession.HelpText, Ok("help").Text);
    }

    // ------------------------------------------------------------ live smoke regressions

    private void AddSmokeWindows()
    {
        fake.AddWindow(500, "chrome", "Remote Control Smoke - Google Chrome", new ScreenRect(0, 0, 1000, 700), false,
            FakeDesktopAutomation.Control("s1", "text", "Remote Control Smoke page one", new ScreenRect(10, 10, 300, 20), "Remote Control Smoke page one", 2),
            FakeDesktopAutomation.Control("s2", "hyperlink", "Next Page", new ScreenRect(10, 40, 80, 20), null, 2, ControlAction.Invoke));
        fake.AddWindow(501, "ApplicationFrameHost", "Remote Desktop");
        fake.AddWindow(502, "chrome", "Remote Control Page Two - Google Chrome", new ScreenRect(0, 0, 1000, 700), false,
            FakeDesktopAutomation.Control("p1", "edit", "Email", new ScreenRect(10, 10, 200, 24), "", 2, ControlAction.SetValue),
            FakeDesktopAutomation.Control("p2", "checkbox", "Include attachments", new ScreenRect(10, 40, 150, 20), "off", 2, ControlAction.Toggle) with { IsToggled = false },
            FakeDesktopAutomation.Control("p3", "button", "Save", new ScreenRect(10, 70, 60, 24), null, 2, ControlAction.Invoke),
            FakeDesktopAutomation.Control("p4", "text", "Page two status", new ScreenRect(10, 100, 200, 20), "Ready", 2));
    }

    [Fact]
    public void MultiWordWindowNameMatchesOnlyThatWindow()
    {
        AddSmokeWindows();
        var result = Ok("focus window Remote Control Smoke");
        Assert.Equal(500, fake.FocusRequests.Last());
        Assert.DoesNotContain("Several", result.Text);
    }

    [Fact]
    public void AllWordsFallbackMatchesTitlePlusProcess()
    {
        AddSmokeWindows();
        var result = Ok("give a screenshot of window Remote Control Smoke chrome and the names of all of the controls and their values");
        var receipt = Assert.IsType<ScreenshotReceipt>(result.Data);
        Assert.Equal(500, receipt.Window!.Window.Handle);
        Assert.DoesNotContain("Also matching", result.Text);
        Assert.Contains("Next Page", result.Text);
    }

    [Fact]
    public void ActionsInMultiWordWindowTargetTheRightControls()
    {
        AddSmokeWindows();
        Ok("controls in Remote Control Page Two");
        Assert.Equal(502, session.CurrentWindowHandle);
        Ok("set the Email text box in Remote Control Page Two to \"This is fun!\"");
        Assert.Equal("This is fun!", fake.Get(502, "p1").Value);
        Ok("check the Include attachments checkbox in Remote Control Page Two");
        Assert.True(fake.Get(502, "p2").IsToggled);
        Ok("click the Save button in Remote Control Page Two");
        Assert.Equal(new ScreenPoint(40, 82), fake.Clicks.Last().Point);
    }

    [Fact]
    public void WaitForQuotedNameRequiresExactName()
    {
        AddSmokeWindows();
        var polls = 0;
        onSleep = () =>
        {
            if (++polls == 2)
                fake.ControlTrees[502].Add(FakeDesktopAutomation.Control("p5", "text", "Saved: This is fun!", new ScreenRect(10, 130, 200, 20), null, 2));
        };
        var result = Ok("wait until a text called \"Saved: This is fun!\" appears in Remote Control Page Two up to 5 seconds");
        var receipt = Assert.IsType<WaitForReceipt>(result.Data);
        Assert.Equal("p5", receipt.Control!.Control.RuntimeKey);
        Assert.Equal(TimeSpan.FromMilliseconds(150), receipt.Elapsed);
    }

    [Fact]
    public void WaitForQuotedNameDoesNotAcceptPartialMatch()
    {
        AddSmokeWindows();
        fake.ControlTrees[502].Add(FakeDesktopAutomation.Control("p6", "text", "Saved: This is fun! (draft)", new ScreenRect(10, 130, 200, 20), null, 2));
        Fails("wait until a text called \"Saved: This is fun!\" appears in Remote Control Page Two up to 1 second", FailureCategories.Timeout);
    }

    [Fact]
    public void TiedWindowsPreferCurrentWindowForActions()
    {
        AddSmokeWindows();
        fake.AddWindow(503, "chrome", "FlaUI UI Automation Overview - Google Chrome", new ScreenRect(0, 0, 1000, 700), false,
            FakeDesktopAutomation.Control("f1", "hyperlink", "Next Page", new ScreenRect(500, 500, 80, 20), null, 2, ControlAction.Invoke));
        Ok("controls in Remote Control Smoke");
        var result = Ok("in the chrome window click on the link Next Page");
        Assert.Equal(new ScreenPoint(50, 50), fake.Clicks.Single().Point);
        var w = Assert.IsType<ActionReceipt>(result.Data).Window!.Alias;
        Assert.Contains($"Using {w} (current window); also matching:", result.Text);
    }

    [Fact]
    public void TiedWindowsPreferForegroundWhenNoCurrentWindow()
    {
        AddSmokeWindows();
        var i = fake.Windows.FindIndex(x => x.Handle == 502);
        for (var k = 0; k < fake.Windows.Count; k++) fake.Windows[k] = fake.Windows[k] with { IsForeground = k == i };
        var result = Ok("click the Save button in chrome");
        Assert.Equal(new ScreenPoint(40, 82), fake.Clicks.Single().Point);
        Assert.Contains("(foreground window); also matching:", result.Text);
    }

    // ------------------------------------------------------------ settle polling (lagging accessibility trees)

    [Fact]
    public void SetValuePatternWaitsForLaggingTreeInsteadOfFallingBack()
    {
        fake.StaleReadsAfterChange = 3;
        var before = now;
        var result = Ok("set the Email text box in chrome to \"This is fun!\"");
        var receipt = Assert.IsType<ActionReceipt>(result.Data);
        Assert.Equal("value-pattern", receipt.Method);
        Assert.Equal("verified", receipt.Outcome);
        Assert.Empty(fake.TypedText);
        Assert.Contains("State observed after 120 ms.", receipt.Notes);
        Assert.Equal(TimeSpan.FromMilliseconds(120), now - before);
    }

    [Fact]
    public void KeyboardFallbackWaitsForLaggingTree()
    {
        fake.StaleReadsAfterChange = 5;
        var receipt = Assert.IsType<ActionReceipt>(Ok("set the Name field in chrome to \"This is fun!\"").Data);
        Assert.Equal("keyboard-replace", receipt.Method);
        Assert.Equal("verified", receipt.Outcome);
        Assert.Contains("State observed after 200 ms.", receipt.Notes);
    }

    [Fact]
    public void CheckWaitsForLaggingToggleState()
    {
        fake.StaleReadsAfterChange = 4;
        var c = AliasOf("k3");
        var result = Ok($"check {c}");
        var receipt = Assert.IsType<ActionReceipt>(result.Data);
        Assert.Equal("verified", receipt.Outcome);
        Assert.Contains("State observed after 160 ms.", receipt.Notes);
        Assert.Single(fake.Actions, a => a.Action == ControlAction.Toggle);
    }

    [Fact]
    public void ToggleWaitsForLaggingState()
    {
        fake.StaleReadsAfterChange = 2;
        var receipt = Assert.IsType<ActionReceipt>(Ok($"toggle {AliasOf("k3")}").Data);
        Assert.Contains("State observed after 80 ms.", receipt.Notes);
        Assert.Contains("on", receipt.ObservedState);
    }

    [Fact]
    public void FocusWaitsForLaggingForeground()
    {
        fake.StaleForegroundReads = 3;
        var receipt = Assert.IsType<ActionReceipt>(Ok("switch to chrome").Data);
        Assert.Equal("verified", receipt.Outcome);
        Assert.Contains("State observed after 120 ms.", receipt.Notes);
    }

    [Fact]
    public void SettleGivesUpAfterTimeoutAndStillFallsBack()
    {
        fake.SetValueIgnored.Add("k2");
        var before = now;
        var receipt = Assert.IsType<ActionReceipt>(Ok("set the Email text box in chrome to x").Data);
        Assert.Equal("keyboard-replace", receipt.Method);
        Assert.Contains(receipt.Notes, n => n.StartsWith("Expected state not observed within 1000 ms", StringComparison.Ordinal));
        Assert.True(now - before >= TimeSpan.FromSeconds(1));
    }

    // ------------------------------------------------------------ batches

    [Fact]
    public void ExecuteAllRunsStepsInOrder()
    {
        var results = session.ExecuteAll("controls in chrome ; set the Email text box to \"a ; b\"\ncheck the Remember me checkbox");
        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.True(r.Succeeded, r.Text));
        Assert.Equal("a ; b", fake.Get(Chrome, "k2").Value);
        Assert.True(fake.Get(Chrome, "k3").IsToggled);
    }

    [Fact]
    public void ExecuteAllStopsAtFirstFailure()
    {
        var results = session.ExecuteAll("controls in chrome ; click the Launch button ; check the Remember me checkbox");
        Assert.Equal(2, results.Count);
        Assert.False(results[1].Succeeded);
        Assert.Equal(FailureCategories.NotFound, results[1].FailureCategory);
        Assert.False(fake.Get(Chrome, "k3").IsToggled);
    }

    [Fact]
    public void ExecuteAllContinuesPastTryFailures()
    {
        var seen = new List<string>();
        var results = session.ExecuteAll("controls in chrome ; try click the Launch button ; check the Remember me checkbox",
            (step, _, _) => seen.Add(step));
        Assert.Equal(3, results.Count);
        Assert.False(results[1].Succeeded);
        Assert.True(results[2].Succeeded);
        Assert.True(fake.Get(Chrome, "k3").IsToggled);
        Assert.Equal(["controls in chrome", "click the Launch button", "check the Remember me checkbox"], seen);
    }

    [Fact]
    public void ExecuteAllStopsAfterQuit()
    {
        var results = session.ExecuteAll("windows ; quit ; screens");
        Assert.Equal(2, results.Count);
        Assert.True(results[1].QuitRequested);
    }

    [Fact]
    public void BatchSucceededCountsTryStepsAndQuit()
    {
        var text = "controls in chrome ; try click the Launch button ; screens";
        Assert.True(RemoteControlSession.BatchSucceeded(RemoteCommandParser.SplitCommands(text), session.ExecuteAll(text)));
        var failing = "controls in chrome ; click the Launch button ; screens";
        Assert.False(RemoteControlSession.BatchSucceeded(RemoteCommandParser.SplitCommands(failing), session.ExecuteAll(failing)));
        var quitting = "windows ; quit ; screens";
        Assert.True(RemoteControlSession.BatchSucceeded(RemoteCommandParser.SplitCommands(quitting), session.ExecuteAll(quitting)));
    }

    [Fact]
    public void TextAreaMatchesDocumentControl()
    {
        fake.AddWindow(600, "TextEdit", "Untitled", new ScreenRect(0, 0, 800, 600), false,
            FakeDesktopAutomation.Control("d0", "pane", "scroll area", new ScreenRect(0, 20, 800, 580), null, 1),
            FakeDesktopAutomation.Control("d1", "document", "", new ScreenRect(0, 20, 780, 560), "", 2, ControlAction.SetValue));
        Ok("set the text area in TextEdit to \"x\"");
        Assert.Equal("x", fake.Get(600, "d1").Value);
    }

    [Fact]
    public void KeyboardFallbackRefusesNonEditablePane()
    {
        fake.AddWindow(601, "TextEdit", "Notes", new ScreenRect(0, 0, 800, 600), false,
            FakeDesktopAutomation.Control("q0", "pane", "Scroll area", new ScreenRect(0, 20, 800, 580), null, 1, ControlAction.ScrollIntoView));
        var c = AliasOf("q0", "TextEdit");
        var result = Fails($"set {c} to \"x\"", FailureCategories.Unsupported);
        Assert.Contains("pane", result.Text);
        Assert.Contains("scrollintoview", result.Text);
        Assert.Empty(fake.Clicks);
        Assert.Empty(fake.TypedText);
        Assert.Empty(fake.KeyChords);
    }

    // ------------------------------------------------------------ fresh walks for pages that are still loading

    private const long SignIn = 700;

    private void AddSignIn() => fake.AddWindow(SignIn, "msedge", "Sign in - SignIn", new ScreenRect(0, 0, 1000, 800), false,
        FakeDesktopAutomation.Control("h1", "text", "Sign in or create account", new ScreenRect(10, 10, 300, 30), null, 2));

    private static ControlInfo PhoneOrEmail() =>
        FakeDesktopAutomation.Control("e1", "edit", "Phone number or email (required)", new ScreenRect(100, 500, 300, 24), "", 2, ControlAction.SetValue);

    [Fact]
    public void NameLookupMissReWalksBeforeFailing()
    {
        AddSignIn();
        Ok("controls in signin");
        var walks = fake.GetControlsCalls;
        fake.ControlTrees[SignIn].Add(PhoneOrEmail());
        Ok("click the Email text box in signin");
        Assert.Equal(new ScreenPoint(250, 512), fake.Clicks.Single().Point);
        Assert.Equal(walks + 1, fake.GetControlsCalls);
    }

    [Fact]
    public void CacheHitDoesNotReWalk()
    {
        Ok("controls in chrome");
        var walks = fake.GetControlsCalls;
        Ok("click the Save button in chrome");
        Assert.Equal(walks, fake.GetControlsCalls);
    }

    [Fact]
    public void WaitForControlReWalksEveryPollAndMatchesContains()
    {
        AddSignIn();
        Ok("controls in signin");
        var start = fake.GetControlsCalls;
        fake.BeforeWalk = n => { if (n == start + 4) fake.ControlTrees[SignIn].Add(PhoneOrEmail()); };
        var receipt = Assert.IsType<WaitForReceipt>(Ok("wait for the Email text box in signin up to 15 seconds").Data);
        Assert.Equal("e1", receipt.Control!.Control.RuntimeKey);
        Assert.Equal(start + 4, fake.GetControlsCalls);
        Assert.Equal(TimeSpan.FromMilliseconds(225), receipt.Elapsed);
    }

    [Fact]
    public void WaitForValueFindsControlThatAppearsLater()
    {
        AddSignIn();
        Ok("controls in signin");
        var start = fake.GetControlsCalls;
        fake.BeforeWalk = n => { if (n == start + 3) fake.ControlTrees[SignIn].Add(PhoneOrEmail() with { Value = "bob@example.com" }); };
        var result = Ok("wait until the Email text box in signin says bob@example.com up to 5 seconds");
        Assert.Equal("e1", Assert.IsType<WaitForReceipt>(result.Data).Control!.Control.RuntimeKey);
    }

    // ------------------------------------------------------------ type is a hard filter

    private const long Login = 710;

    private void AddLogin() => fake.AddWindow(Login, "chrome", "Login - Google Chrome", new ScreenRect(0, 0, 1000, 800), false,
        FakeDesktopAutomation.Control("m1", "button", "Manage your passwords", new ScreenRect(900, 10, 24, 24), null, 2, ControlAction.Invoke));

    [Fact]
    public void TypedSelectorNeverMatchesAnotherType()
    {
        AddLogin();
        var click = Fails("click the Password text box in login", FailureCategories.NotFound);
        Assert.Contains("No edit control matching 'Password'", click.Text);
        Fails("set the Password text box in login to \"x\"", FailureCategories.NotFound);
        Fails("inspect the Password text box in login", FailureCategories.NotFound);
        Assert.Empty(fake.Clicks);
        Assert.Empty(fake.Actions);
        var find = Assert.IsType<ControlListing>(Ok("find the Password text box in login").Data);
        Assert.Empty(find.Controls);
    }

    [Fact]
    public void WaitForTypedControlKeepsWaitingUntilThatTypeAppears()
    {
        AddLogin();
        var timeout = Fails("wait for the Password text box in login up to 1 second", FailureCategories.Timeout);
        Assert.Contains("No edit control matching 'Password'", timeout.Text);

        var walks = 0;
        fake.BeforeWalk = _ =>
        {
            if (++walks == 3)
                fake.ControlTrees[Login].Add(FakeDesktopAutomation.Control("m2", "edit", "Password", new ScreenRect(100, 300, 200, 24), "", 2, ControlAction.SetValue) with { IsPassword = true });
        };
        var receipt = Assert.IsType<WaitForReceipt>(Ok("wait for the Password text box in login up to 15 seconds").Data);
        Assert.Equal("m2", receipt.Control!.Control.RuntimeKey);
        Ok("click the Password text box in login");
        Assert.Equal(new ScreenPoint(200, 312), fake.Clicks.Single().Point);
    }

    [Fact]
    public void WaitForValueAndEnabledRespectType()
    {
        AddLogin();
        Fails("wait until the Password text box in login is enabled up to 1 second", FailureCategories.Timeout);
        Fails("wait until the Password text box in login says x up to 1 second", FailureCategories.Timeout);
    }

    // ------------------------------------------------------------ user-activity guard

    [Fact]
    public void ActionWaitsForUserToGoIdleThenProceeds()
    {
        fake.UserIdleTimes.Enqueue(TimeSpan.FromMilliseconds(100));
        fake.UserIdleTimes.Enqueue(TimeSpan.FromMilliseconds(300));
        fake.UserIdleTime = TimeSpan.FromSeconds(5);
        var before = now;
        Ok("click the Save button in chrome");
        Assert.Single(fake.Clicks);
        Assert.Equal(TimeSpan.FromMilliseconds(100), now - before);
    }

    [Fact]
    public void ActionFailsWithUserActiveWhenNeverIdle()
    {
        fake.UserIdleTime = TimeSpan.FromMilliseconds(20);
        var before = now;
        var result = Fails("click the Save button in chrome", FailureCategories.UserActive);
        Assert.StartsWith(RemoteControlSession.UserActiveMessage, result.Text);
        Assert.Contains("Last input 20 ms ago", result.Text);
        Assert.Empty(fake.Clicks);
        Assert.Empty(fake.FocusRequests);
        Assert.True(now - before >= TimeSpan.FromSeconds(3));

        Fails("type hello", FailureCategories.UserActive);
        Fails("press enter", FailureCategories.UserActive);
        Fails("switch to chrome", FailureCategories.UserActive);
        Fails("scroll down", FailureCategories.UserActive);
        Fails("move mouse to 5,5", FailureCategories.UserActive);
        Fails("set the Name field in chrome to x", FailureCategories.UserActive);
        Assert.Empty(fake.TypedText);
        Assert.Empty(fake.KeyChords);
        Assert.Empty(fake.Scrolls);
        Assert.Empty(fake.MouseMoves);
    }

    [Fact]
    public void ReadsAndPatternActionsIgnoreTheGuard()
    {
        fake.UserIdleTime = TimeSpan.FromMilliseconds(20);
        Ok("controls in chrome");
        Ok("set the Email text box in chrome to bob@example.com");
        Assert.Equal("bob@example.com", fake.Get(Chrome, "k2").Value);
    }

    [Fact]
    public void DisabledGuardNeverWaits()
    {
        session = CreateSession(new RemoteControlSessionOptions { CaptureDirectory = captureDir, UserIdleRequired = TimeSpan.Zero });
        fake.UserIdleTime = TimeSpan.FromMilliseconds(20);
        var before = now;
        Ok("click the Save button in chrome");
        Assert.Equal(before, now);
        Assert.Equal(0, fake.IdleChecks);
    }

    [Fact]
    public void UnknownIdleTimeNeverWaits()
    {
        fake.UserIdleTime = null;
        var before = now;
        Ok("type hello");
        Assert.Equal(before, now);
        Assert.Equal(["hello"], fake.TypedText);
    }

    [Fact]
    public void AdapterUserActiveExceptionPassesThrough()
    {
        var c = AliasOf("k1");
        fake.NextActionException = new DesktopAutomationException(RemoteControlSession.UserActiveMessage, FailureCategories.UserActive);
        Fails($"invoke {c}", FailureCategories.UserActive);
    }

    // ------------------------------------------------------------ full review: session

    // 1. select-all uses the primary modifier
    [Fact]
    public void ClearUsesPrimarySelectAll()
    {
        fake.UpdateControl(Chrome, "k7", c => c with { Value = "old" });
        Ok("clear the Name field in chrome");
        Assert.Equal(["primary+a", "delete"], fake.KeyChords);
        Assert.Equal("", fake.Get(Chrome, "k7").Value);
    }

    // 3. sequences and repeats
    [Fact]
    public void KeySequencesAndRepeatsArePressedInOrder()
    {
        Ok("press tab 3 times");
        Assert.Equal(["tab", "tab", "tab"], fake.KeyChords);
        fake.KeyChords.Clear();
        Ok("press ctrl+c then ctrl+v");
        Assert.Equal(["ctrl+c", "ctrl+v"], fake.KeyChords);
        Fails("press ctrl+foo", FailureCategories.NotUnderstood);
        Assert.Empty(fake.Clicks);
    }

    // 8. the current window closed
    [Fact]
    public void ClosedCurrentWindowFailsInsteadOfUsingForeground()
    {
        var w = WindowAliasOf(Chrome);
        Ok("controls in chrome");
        fake.RemoveWindow(Chrome);
        var result = Fails("click Save", FailureCategories.WindowGone);
        Assert.Contains($"{w} (chrome: Example - Google Chrome) has closed", result.Text);
        Assert.Empty(fake.Clicks);
        Assert.Contains("foreground window", Ok("controls").Text);
    }

    // 9. aliases stay with their window
    [Fact]
    public void AliasFromAnotherWindowIsRefused()
    {
        var c = AliasOf("k4");
        var notepad = WindowAliasOf(Notepad);
        var result = Fails($"click {c} in {notepad}", FailureCategories.NotFound);
        Assert.Contains("belongs to", result.Text);
        Assert.Empty(fake.Clicks);
    }

    [Fact]
    public void WhereNeverRebindsOrBindsToUnknownWindow()
    {
        var c = AliasOf("k1");
        fake.HitOverride = fake.Get(Chrome, "k1");
        fake.MousePosition = new ScreenPoint(50, 50);
        Ok("where");
        fake.HitOverride = null;
        Ok($"click {c}");
        Assert.Equal(Chrome, fake.FocusRequests.Last());

        for (var i = 0; i < fake.Windows.Count; i++) fake.Windows[i] = fake.Windows[i] with { IsForeground = false, Bounds = new ScreenRect(0, 0, 10, 10) };
        fake.HitOverride = FakeDesktopAutomation.Control("z1", "button", "Floating", new ScreenRect(5000, 5000, 10, 10));
        fake.MousePosition = new ScreenPoint(5005, 5005);
        var report = Assert.IsType<WhereReport>(Ok("where").Data);
        Assert.Equal("", report.ControlUnderMouse!.Alias);
    }

    // 14. check/uncheck need a readable state
    [Fact]
    public void CheckWithUnreadableStateIsRefused()
    {
        fake.UpdateControl(Chrome, "k3", x => x with { IsToggled = null });
        var result = Fails($"check {AliasOf("k3")}", FailureCategories.Unsupported);
        Assert.Contains("use toggle", result.Text);
        Assert.Empty(fake.Actions);
    }

    // 15. the typing fallback is only for text inputs
    [Fact]
    public void SetFallbackNeverTypesIntoAButton()
    {
        fake.ControlTrees[Chrome].Add(FakeDesktopAutomation.Control("k9", "button", "Delete account", new ScreenRect(500, 500, 80, 24), null, 2, ControlAction.Invoke, ControlAction.Focus));
        Fails("set the Delete account button in chrome to x", FailureCategories.Unsupported);
        Assert.Empty(fake.Clicks);
        Assert.Empty(fake.TypedText);
    }

    // 16. pruning only touches this tool's files
    [Fact]
    public void PruningLeavesForeignFilesAlone()
    {
        session = CreateSession(new RemoteControlSessionOptions { CaptureDirectory = captureDir, MaxRetainedCaptures = 1, CaptureMaxAge = TimeSpan.Zero });
        Directory.CreateDirectory(captureDir);
        var foreign = Path.Combine(captureDir, "holiday-photo.png");
        var lookalike = Path.Combine(captureDir, "wallpaper-big.png");
        var oldOwn = Path.Combine(captureDir, "screen1-20200101-000000-000.png");
        foreach (var f in new[] { foreign, lookalike, oldOwn })
        {
            File.WriteAllBytes(f, [1, 2, 3]);
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-30));
        }
        var shot = Assert.Single(Ok("screenshot all screens").Files);
        Assert.StartsWith("all-screens-", Path.GetFileName(shot));
        Assert.True(File.Exists(foreign));
        Assert.True(File.Exists(lookalike));
        Assert.False(File.Exists(oldOwn));
        Assert.True(File.Exists(shot));
    }

    // 17. passwords never reach structured output
    [Fact]
    public void PasswordsAreBlankedInData()
    {
        fake.ControlTrees[Chrome].Add(FakeDesktopAutomation.Control("pw", "edit", "Password", new ScreenRect(500, 400, 200, 24), "hunter2", 2, ControlAction.SetValue) with { IsPassword = true });
        var listing = Assert.IsType<ControlListing>(Ok("controls in chrome").Data);
        Assert.Null(listing.Controls.Single(c => c.Control.RuntimeKey == "pw").Control.Value);
        var detail = Assert.IsType<ControlDetail>(Ok($"inspect {AliasOf("pw")}").Data);
        Assert.Null(detail.Control.Control.Value);

        fake.FocusedKey = "pw";
        var typed = Ok("type s3cret");
        var receipt = Assert.IsType<ActionReceipt>(typed.Data);
        Assert.Null(receipt.Value);
        Assert.DoesNotContain("s3cret", typed.Text);
        Assert.DoesNotContain("hunter2", typed.Text);
    }

    // 18. categories and reformatted values
    [Fact]
    public void ChordErrorsAreNotUnderstood() =>
        Assert.Equal(FailureCategories.NotUnderstood, session.Execute(new RemoteCommand(CommandKind.PressKeys) { Keys = "ctrl+banana" }).FailureCategory);

    [Fact]
    public void UnwritableCaptureDirectoryIsPermission()
    {
        Directory.CreateDirectory(captureDir);
        var blocker = Path.Combine(captureDir, "not-a-directory");
        File.WriteAllText(blocker, "x");
        session = CreateSession(new RemoteControlSessionOptions { CaptureDirectory = blocker });
        Fails("screenshot", FailureCategories.Permission);
    }

    [Fact]
    public void ReformattedValueIsSuccessNotRetyped()
    {
        fake.ReformatOnSet["k2"] = v => v + ".00";
        var result = session.Execute("set the Email text box in chrome to 5");
        Assert.True(result.Succeeded, result.Text);
        Assert.Null(result.FailureCategory);
        var receipt = Assert.IsType<ActionReceipt>(result.Data);
        Assert.Equal("set; control shows \"5.00\"", receipt.Outcome);
        Assert.Contains(receipt.Notes, n => n.Contains("reformatted"));
        Assert.Empty(fake.TypedText);
    }

    // 19. hit test before clicking; outcome vocabulary
    [Fact]
    public void CoveredControlIsInvokedNotClickedThrough()
    {
        fake.HitOverride = FakeDesktopAutomation.Control("popup", "pane", "Saved passwords", new ScreenRect(0, 0, 2000, 2000));
        var receipt = Assert.IsType<ActionReceipt>(Ok("click the Save button in chrome").Data);
        Assert.Equal("invoke-pattern", receipt.Method);
        Assert.Equal("issued, not verified", receipt.Outcome);
        Assert.Contains(receipt.Notes, n => n.Contains("Saved passwords"));
        Assert.Empty(fake.Clicks);

        var refused = Fails("right click the Save button in chrome", FailureCategories.OsRefused);
        Assert.Contains("covered by pane 'Saved passwords'", refused.Text);
        Assert.Empty(fake.Clicks);
    }

    [Fact]
    public void ClickOutcomeIsIssuedNotVerified()
    {
        Assert.Equal("issued, not verified", Assert.IsType<ActionReceipt>(Ok("click the Save button in chrome").Data).Outcome);
        Assert.Equal("issued, not verified", Assert.IsType<ActionReceipt>(Ok("click at 5,5").Data).Outcome);
    }

    // 20. wait-for walks are bounded and filtered
    [Fact]
    public void WaitForWalkIsFilteredAndBounded()
    {
        AddSignIn();
        Fails("wait for the Email text box in signin up to 1 second", FailureCategories.Timeout);
        Assert.Equal("Email", fake.LastQuery!.NameContains);
        Assert.True(fake.LastQuery.Timeout <= TimeSpan.FromSeconds(1));

        fake.LastWalkWasTruncated = true;
        fake.LastWalkTruncationReason = "timed out after 2 s";
        var truncated = Fails("wait for the Email text box in signin up to 1 second", FailureCategories.Timeout);
        Assert.Contains("not reached (walk truncated: timed out after 2 s)", truncated.Text);
    }

    // 21. modifier clicks and drags
    [Fact]
    public void ModifierClickAndDrag()
    {
        Ok("shift click the Save button in chrome");
        Assert.Equal("shift", fake.ClickModifiers.Single());
        var from = AliasOf("k1");
        var to = AliasOf("k4");
        var result = Ok($"drag {from} to {to}");
        Assert.Equal((new ScreenPoint(140, 110), new ScreenPoint(130, 312), MouseButton.Left), fake.Drags.Single());
        Assert.Equal("issued, not verified", Assert.IsType<ActionReceipt>(result.Data).Outcome);
        Ok("drag from 10,10 to 50,50");
        Assert.Equal((new ScreenPoint(10, 10), new ScreenPoint(50, 50), MouseButton.Left), fake.Drags.Last());
    }

    // 22. no silent caps
    [Fact]
    public void LongWaitsAndLargeScrollsAreKept()
    {
        var before = now;
        var waited = Ok("wait 20 minutes");
        Assert.Equal(TimeSpan.FromMinutes(20), now - before);
        Assert.Contains("long wait", waited.Text);
        var scrolled = Ok("scroll down 2000");
        Assert.Equal(-2000, fake.Scrolls.Single().DeltaY);
        Assert.Contains("very large scroll", scrolled.Text);
    }

    // 23. handle reuse
    [Fact]
    public void ReusedHandleGetsANewAlias()
    {
        var old = WindowAliasOf(Chrome);
        var i = fake.Windows.FindIndex(w => w.Handle == Chrome);
        fake.Windows[i] = fake.Windows[i] with { ProcessId = 9999, ProcessName = "other", Title = "Something else" };
        Fails($"focus {old}", FailureCategories.WindowGone);
        Assert.NotEqual(old, WindowAliasOf(Chrome));
    }

    // 24. inspect leads with the value
    [Fact]
    public void InspectLeadsWithValue()
    {
        var text = Ok("what is the value of the Status text in chrome").Text;
        Assert.StartsWith("Value:", text);
        Assert.Contains("Idle", text.Split('\n')[0]);
    }

    // ------------------------------------------------------------ single-line words never match documents

    private const long Walmart = 720;

    private void AddWalmartLogin(bool withEdit) => fake.AddWindow(Walmart, "chrome", "Login | Walmart - Google Chrome", new ScreenRect(0, 0, 1000, 800), false,
        [FakeDesktopAutomation.Control("wd", "document", "Login | Walmart", new ScreenRect(0, 80, 1000, 700), null, 1, ControlAction.SetValue),
         FakeDesktopAutomation.Control("wc", "combobox", "Country", new ScreenRect(10, 600, 200, 24), "US", 2, ControlAction.Expand),
         .. withEdit ? new[] { FakeDesktopAutomation.Control("we", "edit", "Email", new ScreenRect(10, 200, 300, 24), "", 2, ControlAction.SetValue) } : []]);

    [Fact]
    public void EditBoxWaitIgnoresTheDocument()
    {
        AddWalmartLogin(withEdit: false);
        Fails("wait for an edit box in walmart up to 1 second", FailureCategories.Timeout);
        var walks = 0;
        fake.BeforeWalk = _ =>
        {
            if (++walks == 2)
                fake.ControlTrees[Walmart].Add(FakeDesktopAutomation.Control("we", "edit", "Email", new ScreenRect(10, 200, 300, 24), "", 2, ControlAction.SetValue));
        };
        var receipt = Assert.IsType<WaitForReceipt>(Ok("wait for an edit box in walmart up to 10 seconds").Data);
        Assert.Equal("we", receipt.Control!.Control.RuntimeKey);
    }

    [Fact]
    public void FieldMatchesComboboxButBoxDoesNot()
    {
        AddWalmartLogin(withEdit: true);
        Assert.Equal("wc", Assert.IsType<ControlDetail>(Ok("inspect the Country field in walmart").Data).Control.Control.RuntimeKey);
        Fails("inspect the Country box in walmart", FailureCategories.NotFound);
    }

    [Fact]
    public void MultiLineWordsMatchTheDocument()
    {
        AddWalmartLogin(withEdit: true);
        Assert.Equal("wd", Assert.IsType<ControlDetail>(Ok("inspect the text area in walmart").Data).Control.Control.RuntimeKey);
    }

    [Fact]
    public void EditListingPutsDocumentsAfterEdits()
    {
        AddWalmartLogin(withEdit: true);
        var listing = Assert.IsType<ControlListing>(Ok("edit boxes in walmart").Data);
        Assert.Equal(["we", "wd"], listing.Controls.Select(c => c.Control.RuntimeKey).ToArray());
    }

    // ------------------------------------------------------------ fewer walks

    [Fact]
    public void TypedLookupPassesTypesAndNameToTheWalk()
    {
        Ok("click the Save button in chrome");
        Assert.Equal(["button", "splitbutton", "menubutton"], fake.LastQuery!.ControlTypes);
        Assert.Equal("Save", fake.LastQuery.NameContains);
        Assert.Equal(1, session.LastExecutionStats.Walks);

        Fails("inspect the Country field in chrome", FailureCategories.NotFound);
        Assert.Equal(["edit", "combobox"], fake.LastQuery!.ControlTypes);
        Assert.Equal(1, session.LastExecutionStats.Walks);
    }

    [Fact]
    public void BareNameMissFallsBackToOneUnfilteredWalk()
    {
        Fails("click Launch rocket in chrome", FailureCategories.NotFound);
        Assert.Equal(2, session.LastExecutionStats.Walks);
        Assert.Null(fake.LastQuery!.NameContains);
        Assert.Equal("Launch", RemoteControlSession.SignificantWord("Launch rocket"));
        Assert.Equal("account", RemoteControlSession.SignificantWord("Sign in or create account"));
    }

    [Fact]
    public void FreshListingIsReusedWithoutWalking()
    {
        Ok("controls in chrome");
        now += TimeSpan.FromSeconds(8);
        Ok("click the Save button in chrome");
        Assert.Equal(0, session.LastExecutionStats.Walks);
        now += TimeSpan.FromSeconds(11);
        Ok("click the Next Page link in chrome");
        Assert.Equal(1, session.LastExecutionStats.Walks);
    }

    [Fact]
    public void ListingFreshnessIsConfigurable()
    {
        session = CreateSession(new RemoteControlSessionOptions { CaptureDirectory = captureDir, ListingFreshness = TimeSpan.FromSeconds(1) });
        Ok("controls in chrome");
        now += TimeSpan.FromSeconds(2);
        Ok("click the Save button in chrome");
        Assert.Equal(1, session.LastExecutionStats.Walks);
    }

    [Fact]
    public void AliasClickAndVerificationNeverWalk()
    {
        var c = AliasOf("k4");
        now += TimeSpan.FromMinutes(5);
        Ok($"click {c}");
        Assert.Equal(0, session.LastExecutionStats.Walks);
        Assert.True(session.LastExecutionStats.Refreshes >= 1);
        var check = AliasOf("k3");
        now += TimeSpan.FromMinutes(5);
        Ok($"check {check}");
        Assert.Equal(0, session.LastExecutionStats.Walks);
    }

    [Fact]
    public void WaitPollIsAtLeastAsLongAsTheLastWalk()
    {
        var sleeps = new List<TimeSpan>();
        session = new RemoteControlSession(fake, new RemoteControlSessionOptions { CaptureDirectory = captureDir })
        {
            Now = () => now,
            Sleep = d => { sleeps.Add(d); now += d; },
        };
        fake.BeforeWalk = _ => now += TimeSpan.FromMilliseconds(600);
        Fails("wait for the Launch button in chrome up to 3 seconds", FailureCategories.Timeout);
        Assert.NotEmpty(sleeps);
        Assert.All(sleeps, d => Assert.True(d >= TimeSpan.FromMilliseconds(600), $"slept {d}"));
    }

    [Fact]
    public void WaitForValueAndGoneQueriesCarryFilters()
    {
        Fails("wait until the Status text in chrome says Done up to 1 second", FailureCategories.Timeout);
        Assert.Equal(["text", "heading"], fake.LastQuery!.ControlTypes);
        Assert.Equal("Status", fake.LastQuery.NameContains);
        Assert.True(fake.LastQuery.Timeout <= TimeSpan.FromSeconds(1));
        Ok("wait until the Launch button in chrome is gone up to 1 second");
        Assert.Equal(["button", "splitbutton", "menubutton"], fake.LastQuery!.ControlTypes);
    }

    [Fact]
    public void StatsCountWalksAndRefreshes()
    {
        Ok("controls in chrome");
        Assert.Equal(1, session.LastExecutionStats.Walks);
        Ok("windows");
        Assert.Equal(new ExecutionStats(0, TimeSpan.Zero, 0), session.LastExecutionStats);
    }

    // ------------------------------------------------------------ gentler guard

    [Fact]
    public void GuardDefaultsAreShort()
    {
        var defaults = new RemoteControlSessionOptions { CaptureDirectory = captureDir };
        Assert.Equal(TimeSpan.FromMilliseconds(350), defaults.UserIdleRequired);
        Assert.Equal(TimeSpan.FromSeconds(1.5), defaults.UserIdleWaitLimit);
        Assert.Equal(TimeSpan.FromMilliseconds(350), session.UserIdleRequired);
    }

    [Fact]
    public void FirstStepWaitsTwiceLaterStepsOnce()
    {
        fake.UserIdleTime = TimeSpan.FromMilliseconds(20);
        var marks = new List<DateTimeOffset> { now };
        var results = session.ExecuteAll("try click the Save button in chrome ; try type hello", (_, _, _) => marks.Add(now));
        Assert.All(results, r => Assert.Equal(FailureCategories.UserActive, r.FailureCategory));
        Assert.True(marks[1] - marks[0] >= TimeSpan.FromSeconds(3), $"first step waited {marks[1] - marks[0]}");
        Assert.True(marks[2] - marks[1] < TimeSpan.FromSeconds(2), $"second step waited {marks[2] - marks[1]}");
        Assert.True(marks[2] - marks[1] >= TimeSpan.FromSeconds(1.5));
    }

    [Fact]
    public void SecondWaitWindowLetsTheActionThrough()
    {
        for (var i = 0; i < 40; i++) fake.UserIdleTimes.Enqueue(TimeSpan.FromMilliseconds(20));
        fake.UserIdleTime = TimeSpan.FromSeconds(5);
        var before = now;
        Ok("click the Save button in chrome");
        Assert.Single(fake.Clicks);
        Assert.True(now - before > TimeSpan.FromSeconds(1.5));
    }

    [Fact]
    public void GuardCommandTurnsTheGuardOffOnAndToAValue()
    {
        fake.UserIdleTime = TimeSpan.FromMilliseconds(20);
        Assert.Contains("off", Ok("guard off").Text);
        var before = now;
        Ok("click the Save button in chrome");
        Assert.Equal(before, now);

        Ok("guard 500 ms");
        Assert.Equal(TimeSpan.FromMilliseconds(500), session.UserIdleRequired);
        fake.UserIdleTimes.Enqueue(TimeSpan.FromMilliseconds(400));
        fake.UserIdleTime = TimeSpan.FromMilliseconds(600);
        before = now;
        Ok("click the Save button in chrome");
        Assert.Equal(TimeSpan.FromMilliseconds(50), now - before);

        Ok("guard on");
        Assert.Equal(TimeSpan.FromMilliseconds(350), session.UserIdleRequired);
        Assert.Contains("350 ms", Ok("guard").Text);
    }

    // ------------------------------------------------------------ agent-friendly output

    [Fact]
    public void ScalerProducesTheEmbeddedImage()
    {
        var asked = 0;
        session = CreateSession(new RemoteControlSessionOptions
        {
            CaptureDirectory = captureDir,
            EmbedScreenshotBase64 = true,
            ScreenshotScaler = (png, maxSide) => { asked = maxSide; return ([1, 2, 3], "image/jpeg", 100, 56, 0.052); },
        });
        var receipt = Assert.IsType<ScreenshotReceipt>(Ok("screenshot").Data);
        Assert.Equal(1568, asked);
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), receipt.PngBase64);
        Assert.Equal("image/jpeg", receipt.ImageMime);
        Assert.Equal((100, 56, 0.052), (receipt.ImageWidth, receipt.ImageHeight, receipt.ImageScale));
        Assert.Equal((1920, 1080), (receipt.Width, receipt.Height));
        Assert.NotEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(receipt.Path));
    }

    [Fact]
    public void WithoutScalerTheFullPngIsEmbedded()
    {
        session = CreateSession(new RemoteControlSessionOptions { CaptureDirectory = captureDir, EmbedScreenshotBase64 = true });
        var receipt = Assert.IsType<ScreenshotReceipt>(Ok("screenshot").Data);
        Assert.Equal(Convert.ToBase64String(File.ReadAllBytes(receipt.Path)), receipt.PngBase64);
        Assert.Null(receipt.ImageMime);
    }

    [Fact]
    public void CompactListingKeepsInteractiveRowsAndCutsNames()
    {
        for (var i = 0; i < 6; i++)
            fake.ControlTrees[Chrome].Insert(0, FakeDesktopAutomation.Control($"t{i}", "text", $"Paragraph number {i} with a long description", new ScreenRect(0, i * 10, 50, 10), null, 2));
        session = CreateSession(new RemoteControlSessionOptions { CaptureDirectory = captureDir, ListingMaxRows = 4, ListingNameMaxLength = 10 });
        var result = Ok("controls in chrome");
        var listing = Assert.IsType<ControlListing>(result.Data);
        Assert.Equal(12, listing.Controls.Count);
        Assert.Contains("8 more controls not shown; narrow with a type word or `matching <text>`.", result.Text);
        Assert.Contains("Next Page", result.Text);
        Assert.Contains("Remember…", result.Text);
        Assert.DoesNotContain("Paragraph", result.Text);
        Assert.Equal("Paragraph number 0 with a long description", listing.Controls.First(c => c.Control.RuntimeKey == "t0").Control.Name);
    }

    [Fact]
    public void DefaultListingShowsEveryRow()
    {
        var text = Ok("controls in chrome").Text;
        Assert.DoesNotContain("not shown", text);
        Assert.Contains("Remember me", text);
    }
}
