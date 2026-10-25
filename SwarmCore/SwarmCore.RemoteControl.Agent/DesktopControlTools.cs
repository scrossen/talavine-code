using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Procedures;

namespace SwarmCore.RemoteControl.Agent;

/// <summary>
/// What one desktop tool call did. It is physical adapter evidence for the procedure engine:
/// <see cref="Succeeded"/> is the session's ok flag, <see cref="NoPhysicalAction"/> is true for reads and for
/// failures the session raises before sending anything (not found, ambiguous, user active, ...), and
/// <see cref="OutcomeUncertain"/> is true when input was sent but its effect was not confirmed
/// (the session said "issued, not verified", or a failure happened after dispatch).
/// </summary>
public sealed record DesktopToolResult(bool Ok, string Text) : IAgentProcedureEffectEvidence, IToolResultImages
{
    /// <summary>The downscaled screenshot for the model, only when the session's scaler produced one (never the full capture).</summary>
    [JsonIgnore] public ImagePart? Image { get; init; }
    IReadOnlyList<ImagePart> IToolResultImages.Images => Image is { } image ? [image] : [];

    public string? Error { get; init; }
    /// <summary>One of <see cref="FailureCategories"/> when <see cref="Ok"/> is false.</summary>
    public string? FailureCategory { get; init; }
    public IReadOnlyList<string> Files { get; init; } = [];
    /// <summary>The session's structured payload (listing, action receipt, wait receipt) as JSON, redacted of slot values.</summary>
    public JsonNode? Data { get; init; }
    /// <summary>"verified", "observed", "unchanged" or "issued, not verified" for actions; null for reads.</summary>
    public string? Outcome { get; init; }
    /// <summary>Whether anything may have been sent to the desktop by this call.</summary>
    public bool Dispatched { get; init; }

    [JsonIgnore] public bool Succeeded => Ok;
    [JsonIgnore] public bool NoPhysicalAction => !Dispatched;
    [JsonIgnore] public bool LocalProcessCompleted => false;
    public bool OutcomeUncertain => Dispatched && (!Ok || Outcome == "issued, not verified");
}

/// <summary>
/// A <see cref="RemoteControlSession"/> exposed as engine tools with the same names and argument shapes as the
/// Talavine.RemoteControl MCP tools (desktop_act becomes desktop.act). Reads are <see cref="ToolSideEffect.ReadOnly"/>;
/// act, input and command can change the desktop and are <see cref="ToolSideEffect.ExternalSend"/> (a desktop action
/// can submit a form or a purchase on a site). A step may name a private secret slot instead of a value.
/// </summary>
public sealed class DesktopControlTools
{
    public const string OwnerId = "desktop";
    public const string ListWindowsTool = "desktop.list_windows";
    public const string InspectTool = "desktop.inspect";
    public const string CaptureTool = "desktop.capture";
    public const string ActTool = "desktop.act";
    public const string InputTool = "desktop.input";
    public const string WaitForTool = "desktop.wait_for";
    public const string CommandTool = "desktop.command";

    public sealed record EmptyInput;
    public sealed record InspectInput(string Window, string? NameContains = null, string? ControlType = null, bool IncludeAll = false,
        int? MaxRows = null, bool InteractiveOnly = false);
    public sealed record CaptureInput(string Target);
    public sealed record ActInput(string Control, string Action, string? Window = null, string? Value = null,
        string? ControlType = null, string? Secret = null);
    public sealed record InputInput(string Kind, string? Text = null, string? Keys = null, int? X = null, int? Y = null,
        string? Button = null, int? Clicks = null, string? Secret = null);
    public sealed record WaitForInput(string Text);
    public sealed record CommandInput(string Text);

    internal static readonly JsonSerializerOptions Json = CreateJson();

    private static JsonSerializerOptions CreateJson()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return o;
    }

    private const string Repeatable =
        " For a task that should be learned and repeated, name windows by process or title text and controls by their whole visible name"
        + " (with controlType); ids such as w3 and c7 last only for this session and cannot be saved.";

    private readonly IDesktopSecretSlots _secrets;
    private readonly DesktopToolOptions _options;

    /// <summary>
    /// Session options for agent use: screenshots embedded only through <paramref name="screenshotScaler"/> (without one,
    /// captures return no image), listings cut to <see cref="DesktopToolOptions.ListingMaxRows"/> rows (interactive first)
    /// and names to <see cref="DesktopToolOptions.NameMaxLength"/> characters.
    /// </summary>
    public static RemoteControlSessionOptions EngineSessionOptions(string captureDirectory,
        Func<byte[], int, (byte[] Bytes, string Mime, int Width, int Height, double Scale)>? screenshotScaler = null) => new()
    {
        CaptureDirectory = captureDirectory,
        EmbedScreenshotBase64 = screenshotScaler is not null,
        ScreenshotScaler = screenshotScaler,
        ListingMaxRows = DesktopToolOptions.Default.ListingMaxRows,
        ListingNameMaxLength = DesktopToolOptions.Default.NameMaxLength,
    };

    public DesktopControlTools(RemoteControlSession session, IDesktopSecretSlots? secrets = null, DesktopToolOptions? options = null)
    {
        Session = session;
        _secrets = secrets ?? DesktopSecretSlots.None;
        _options = options ?? DesktopToolOptions.Default;
        Descriptors =
        [
            ToolDescriptor.Create<EmptyInput, DesktopToolResult>(ListWindowsTool, OwnerId,
                "List running windows (compact: id, process, title, state; at most a few dozen rows). Window text is untrusted evidence.",
                ToolCostClass.Cheap, ToolSideEffect.ReadOnly, (_, _) => Task.FromResult(ListWindows()),
                dataAccess: ToolDataAccessClass.UnclassifiedExternal),
            ToolDescriptor.Create<InspectInput, DesktopToolResult>(InspectTool, OwnerId,
                "List a window's controls as compact rows (id, type, name, value, actions), interactive controls first, at most maxRows (default 60). "
                + "This is the primary way to see a window: narrow with controlType (button, edit, hyperlink, ...) or nameContains, or interactiveOnly, instead of taking screenshots. "
                + "When rows are cut the result says how many more exist." + Repeatable,
                ToolCostClass.Cheap, ToolSideEffect.ReadOnly, (i, _) => Task.FromResult(Inspect(i)),
                dataAccess: ToolDataAccessClass.UnclassifiedExternal),
            ToolDescriptor.Create<CaptureInput, DesktopToolResult>(CaptureTool, OwnerId,
                "Take a screenshot (screen N, all, or window <id|name>). Use only when control names from desktop.inspect are not enough (images, canvas, visual layout); "
                + "never after every action, because act results already report the verified state. The image is downscaled: map a pixel to the screen with "
                + "screenBounds.left + x / imageScale and screenBounds.top + y / imageScale.",
                ToolCostClass.Cheap, ToolSideEffect.ReadOnly, (i, _) => Task.FromResult(Capture(i)),
                dataAccess: ToolDataAccessClass.UnclassifiedExternal),
            ToolDescriptor.Create<ActInput, DesktopToolResult>(ActTool, OwnerId,
                "Act on a control: click, double-click, right-click, invoke, toggle, check, uncheck, select, expand, collapse, set (needs value or secret) or focus (a window). "
                + "secret names a private host slot typed without the value ever appearing in arguments: password, or otp:email for a one-time sign-in code the host reads from its mailbox. Never put a password or code in value. "
                + "When a site offers where to send a sign-in code, choose email. The result reports the verified state afterwards, so do not take a screenshot to check it." + Repeatable,
                ToolCostClass.External, ToolSideEffect.ExternalSend, (i, ct) => ActAsync(i, ct),
                dataAccess: ToolDataAccessClass.UnclassifiedExternal),
            ToolDescriptor.Create<InputInput, DesktopToolResult>(InputTool, OwnerId,
                "Raw input: kind type (text, or secret to type a private host slot), press (keys chord such as enter, down, ctrl+s), click-at or move (x, y), scroll (text: down 5). "
                + "Prefer desktop.act with control names for repeatable tasks; coordinates cannot be saved.",
                ToolCostClass.External, ToolSideEffect.ExternalSend, (i, ct) => InputAsync(i, ct),
                dataAccess: ToolDataAccessClass.UnclassifiedExternal),
            ToolDescriptor.Create<WaitForInput, DesktopToolResult>(WaitForTool, OwnerId,
                "Wait for a condition, written as a wait for sentence, for example: window notepad, or button \"Checkout\" in chrome.",
                ToolCostClass.Cheap, ToolSideEffect.ReadOnly, (i, _) => Task.FromResult(WaitFor(i)),
                dataAccess: ToolDataAccessClass.UnclassifiedExternal),
            ToolDescriptor.Create<CommandInput, DesktopToolResult>(CommandTool, OwnerId,
                "Run any command from the remote-control text grammar (text: help lists it). Prefer the structured tools for repeatable tasks.",
                ToolCostClass.External, ToolSideEffect.ExternalSend, (i, _) => Task.FromResult(Command(i)),
                dataAccess: ToolDataAccessClass.UnclassifiedExternal),
        ];
    }

    /// <summary>The same session and tool set with additional slot sources (for example a per-task mailbox resolver for one-time codes).</summary>
    public DesktopControlTools WithSlots(IDesktopSecretSlots extra) => new(Session, new CompositeSecretSlots(_secrets, extra), _options);

    public RemoteControlSession Session { get; }
    public IReadOnlyCollection<string> SecretSlotNames => _secrets.SlotNames;
    public IReadOnlyList<ToolDescriptor> Descriptors { get; }

    /// <summary>Maps an MCP tool name (desktop_act) to its engine tool id (desktop.act); null when it is not a desktop tool.</summary>
    public static string? EngineToolId(string mcpToolName) => mcpToolName switch
    {
        "desktop_list_windows" => ListWindowsTool,
        "desktop_inspect" => InspectTool,
        "desktop_capture" => CaptureTool,
        "desktop_act" => ActTool,
        "desktop_input" => InputTool,
        "desktop_wait_for" => WaitForTool,
        "desktop_command" => CommandTool,
        _ => null,
    };

    /// <summary>
    /// Rebuilds the evidence for a call another host surface executed and replied to with the session's JSON shape
    /// (<c>{ ok, text, error, failureCategory, files, data }</c>, as the MCP tools return), so that host can journal its
    /// own observed calls for <see cref="DesktopProcedureRunner.SaveAsync"/>.
    /// </summary>
    public static DesktopToolResult ResultFromReply(string toolId, JsonObject arguments, JsonObject reply)
    {
        string? Str(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        var ok = reply["ok"] is JsonValue okValue && okValue.TryGetValue<bool>(out var b) && b;
        var category = Str(reply, "failureCategory");
        var data = reply["data"]?.DeepClone();
        var acts = toolId switch
        {
            ActTool or InputTool => true,
            CommandTool => !IsReadKind(RemoteCommandParser.Parse(Str(arguments, "text") ?? "").Kind),
            _ => false,
        };
        return new DesktopToolResult(ok, Str(reply, "text") ?? "")
        {
            Error = Str(reply, "error"), FailureCategory = category, Data = data,
            Files = reply["files"] is JsonArray files ? files.Select(f => f?.ToString() ?? "").ToArray() : [],
            Outcome = acts && data is JsonObject d ? Str(d, "outcome") : null,
            Dispatched = acts && (ok || !IsPreDispatchFailure(category)),
        };
    }

    /// <summary>Whether a tool id can change the desktop.</summary>
    public static bool MayAct(string toolId) => toolId is ActTool or InputTool or CommandTool;

    public DesktopToolResult ListWindows()
    {
        var r = Session.Execute(new RemoteCommand(CommandKind.ListWindows));
        if (!r.Succeeded || r.Data is not WindowListing listing) return Read(r);
        var max = _options.ListingMaxRows;
        var shown = listing.Windows.Take(max).ToList();
        var text = new StringBuilder($"{listing.Windows.Count} window{(listing.Windows.Count == 1 ? "" : "s")}:");
        foreach (var w in shown)
            text.Append('\n').Append(w.Alias).Append(' ').Append(w.Window.ProcessName).Append(" \"").Append(Cut(w.Window.Title)).Append('"')
                .Append(w.Window.IsForeground ? " [foreground]" : "").Append(w.Window.IsMinimized ? " [minimized]" : "");
        var more = listing.Windows.Count - shown.Count;
        if (more > 0) text.Append($"\n{more} more windows not shown; name the window by process or title text.");
        var data = Serialize(new WindowListing(shown))!.AsObject();
        data["total"] = listing.Windows.Count;
        data["more"] = more;
        return Read(r) with { Text = text.ToString(), Data = data };
    }

    public DesktopToolResult Inspect(InspectInput input)
    {
        var win = Window(input.Window);
        if (win is null) return Refused("A window is required.");
        if (input.MaxRows is < 1) return Refused("maxRows must be at least 1.");
        var type = NormalizeType(input.ControlType);
        CommandResult r;
        if (!string.IsNullOrWhiteSpace(input.NameContains) || type is not null)
        {
            var sel = new ControlSelector(ControlType: type, NameText: string.IsNullOrWhiteSpace(input.NameContains) ? null : input.NameContains);
            r = Session.Execute(new RemoteCommand(CommandKind.FindControl) { Window = win, Control = sel, Verbose = input.IncludeAll });
        }
        else r = Session.Execute(new RemoteCommand(CommandKind.ListControls) { Window = win, Verbose = input.IncludeAll });
        if (!r.Succeeded || r.Data is not ControlListing listing) return Read(r);
        return Compact(Read(r), listing, Math.Min(input.MaxRows ?? _options.ListingMaxRows, 500), input.InteractiveOnly);
    }

    // A compact listing for a model: interactive rows first when cut, short names, and the same rows in Data.
    private DesktopToolResult Compact(DesktopToolResult result, ControlListing listing, int maxRows, bool interactiveOnly)
    {
        var candidates = listing.Controls.Select((e, i) => (e, i)).Where(x => !interactiveOnly || IsInteractive(x.e.Control)).ToList();
        var shown = candidates.Count <= maxRows ? candidates.Select(x => x.e).ToList()
            : candidates.OrderBy(x => IsInteractive(x.e.Control) ? 0 : 1).ThenBy(x => x.i).Take(maxRows).OrderBy(x => x.i).Select(x => x.e).ToList();
        var w = listing.Window;
        var what = listing.Filter is null ? "Controls" : $"Controls matching {listing.Filter}";
        var text = new StringBuilder($"{what} in {w.Alias} ({w.Window.ProcessName}: {Cut(w.Window.Title)}):");
        foreach (var note in listing.Notes) text.Append('\n').Append(note);
        if (shown.Count == 0) text.Append("\n(none)");
        foreach (var e in shown)
        {
            var c = e.Control;
            text.Append('\n').Append(e.Alias).Append(' ').Append(c.ControlType).Append(" \"").Append(Cut(c.Name)).Append('"');
            if (c.Value is not null) text.Append(" = ").Append(c.IsPassword ? "(hidden)" : "\"" + Cut(c.Value) + "\"");
            var flags = new[] { c.IsEnabled ? null : "disabled", c.HasKeyboardFocus ? "focused" : null, c.IsOffscreen ? "offscreen" : null }
                .Where(f => f is not null).ToArray();
            if (flags.Length > 0) text.Append(" [").Append(string.Join(",", flags)).Append(']');
            if (c.SupportedActions.Count > 0) text.Append(" {").Append(string.Join(",", c.SupportedActions.Select(a => a.ToString().ToLowerInvariant()))).Append('}');
        }
        var more = candidates.Count - shown.Count;
        if (more > 0) text.Append($"\n{more} more controls not shown; narrow with controlType or nameContains (or interactiveOnly).");
        var data = Serialize(listing with { Controls = shown })!.AsObject();
        data["total"] = candidates.Count;
        data["more"] = more;
        return result with { Text = text.ToString(), Data = data };
    }

    private string? Cut(string? s) => s is null || _options.NameMaxLength <= 0 || s.Length <= _options.NameMaxLength
        ? s : s[..Math.Max(1, _options.NameMaxLength - 1)] + "\u2026";

    private static readonly HashSet<string> InteractiveTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "edit", "document", "button", "splitbutton", "menubutton", "hyperlink", "checkbox", "radiobutton", "combobox",
        "listitem", "dataitem", "treeitem", "tabitem", "menuitem", "slider",
    };

    private static bool IsInteractive(ControlInfo c) => InteractiveTypes.Contains(c.ControlType);

    private static JsonNode? Serialize(object value) => JsonSerializer.SerializeToNode(value, value.GetType(), Json);

    public DesktopToolResult Capture(CaptureInput input)
    {
        var t = (input.Target ?? "").Trim();
        if (t.Equals("all", StringComparison.OrdinalIgnoreCase))
            return Read(Session.Execute(new RemoteCommand(CommandKind.Screenshot) { Target = ScreenshotTarget.AllDisplays }));
        if (t.StartsWith("screen", StringComparison.OrdinalIgnoreCase) || t.StartsWith("display", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("monitor", StringComparison.OrdinalIgnoreCase))
        {
            var space = t.IndexOf(' ');
            var num = space < 0 ? "" : t[space..].Trim();
            return int.TryParse(num, out var n) && n >= 1
                ? Read(Session.Execute(new RemoteCommand(CommandKind.Screenshot) { Target = ScreenshotTarget.Display, DisplayIndex = n }))
                : Read(Session.Execute(new RemoteCommand(CommandKind.Screenshot) { Target = ScreenshotTarget.Primary }));
        }
        if (t.StartsWith("window ", StringComparison.OrdinalIgnoreCase)) t = t[7..].Trim();
        var win = Window(t);
        return win is null
            ? Refused("A target is required: screen N, all, or window <id|name>.")
            : Read(Session.Execute(new RemoteCommand(CommandKind.Screenshot) { Target = ScreenshotTarget.Window, Window = win }));
    }

    public async Task<DesktopToolResult> ActAsync(ActInput input, CancellationToken ct = default)
    {
        var a = (input.Action ?? "").Trim().ToLowerInvariant();
        var win = Window(input.Window);
        if (a == "focus")
        {
            var target = win ?? Window(input.Control);
            return target is null ? Refused("focus needs a window.") : Acted(Session.Execute(new RemoteCommand(CommandKind.FocusWindow) { Window = target }));
        }
        var sel = Control(input.Control, input.ControlType);
        if (sel is null) return Refused("A control is required.");
        switch (a)
        {
            case "set":
                if (input.Secret is not null)
                {
                    if (input.Value is not null) return Refused("Give either value or secret, not both.");
                    var secret = await _secrets.ResolveAsync(input.Secret, ct).ConfigureAwait(false);
                    if (secret is null) return Unresolved(input.Secret);
                    return Acted(Session.Execute(new RemoteCommand(CommandKind.SetValue) { Window = win, Control = sel, Value = secret }), input.Secret, secret);
                }
                return input.Value is null
                    ? Refused("The set action needs a value or a secret slot.")
                    : Acted(Session.Execute(new RemoteCommand(CommandKind.SetValue) { Window = win, Control = sel, Value = input.Value }));
            case "click":
                return Acted(Session.Execute(new RemoteCommand(CommandKind.Click) { Window = win, Control = sel }));
            case "double-click" or "doubleclick":
                return Acted(Session.Execute(new RemoteCommand(CommandKind.Click) { Window = win, Control = sel, Clicks = 2 }));
            case "right-click" or "rightclick":
                return Acted(Session.Execute(new RemoteCommand(CommandKind.Click) { Window = win, Control = sel, Button = MouseButton.Right }));
            case "invoke" or "toggle" or "check" or "uncheck" or "select" or "expand" or "collapse":
                return Acted(Session.Execute(new RemoteCommand(CommandKind.Invoke) { Window = win, Control = sel, Value = a }));
            default:
                return Refused($"Unknown action '{input.Action}'.");
        }
    }

    public async Task<DesktopToolResult> InputAsync(InputInput input, CancellationToken ct = default)
    {
        var k = (input.Kind ?? "").Trim().ToLowerInvariant();
        switch (k)
        {
            case "type":
                if (input.Secret is not null)
                {
                    if (input.Text is not null) return Refused("Give either text or secret, not both.");
                    var secret = await _secrets.ResolveAsync(input.Secret, ct).ConfigureAwait(false);
                    if (secret is null) return Unresolved(input.Secret);
                    return Acted(Session.Execute(new RemoteCommand(CommandKind.TypeText) { Value = secret }), input.Secret, secret);
                }
                return input.Text is null ? Refused("type needs text or a secret slot.") : Acted(Session.Execute(new RemoteCommand(CommandKind.TypeText) { Value = input.Text }));
            case "press":
                if (string.IsNullOrWhiteSpace(input.Keys)) return Refused("press needs keys.");
                try { return Acted(Session.Execute(new RemoteCommand(CommandKind.PressKeys) { Keys = KeyChord.Normalize(input.Keys) })); }
                catch (ArgumentException ex) { return Refused(ex.Message); }
            case "click-at" or "move":
                if (input.X is null || input.Y is null) return Refused("This kind needs x and y.");
                var point = new ScreenPoint(input.X.Value, input.Y.Value);
                if (k == "move") return Acted(Session.Execute(new RemoteCommand(CommandKind.MoveMouse) { Point = point }));
                var b = (input.Button ?? "left").Trim().ToLowerInvariant() switch { "right" => MouseButton.Right, "middle" => MouseButton.Middle, _ => MouseButton.Left };
                return Acted(Session.Execute(new RemoteCommand(CommandKind.Click) { Point = point, Button = b, Clicks = input.Clicks == 2 ? 2 : 1 }));
            case "scroll":
                var parts = (input.Text ?? "down").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var amount = parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : 3;
                var (dx, dy) = parts[0].ToLowerInvariant() switch
                {
                    "down" => (0, -amount), "up" => (0, amount), "left" => (-amount, 0), "right" => (amount, 0), _ => (0, 0),
                };
                return dx == 0 && dy == 0
                    ? Refused("scroll needs a direction: up, down, left or right, optionally followed by an amount.")
                    : Acted(Session.Execute(new RemoteCommand(CommandKind.Scroll) { ScrollX = dx, ScrollY = dy }));
            default:
                return Refused($"Unknown kind '{input.Kind}'.");
        }
    }

    public DesktopToolResult WaitFor(WaitForInput input) => Read(Session.Execute(WaitText(input.Text)));

    public DesktopToolResult Command(CommandInput input)
    {
        var parsed = RemoteCommandParser.Parse(input.Text ?? "");
        return IsReadKind(parsed.Kind) ? Read(Session.Execute(parsed)) : Acted(Session.Execute(parsed));
    }

    /// <summary>Normalizes a wait condition so it may be written with or without the leading "wait for".</summary>
    public static string WaitText(string? text)
    {
        var t = (text ?? "").Trim();
        return t.StartsWith("wait", StringComparison.OrdinalIgnoreCase) ? t : "wait for " + t;
    }

    internal static bool IsReadKind(CommandKind kind) => kind is CommandKind.Help or CommandKind.ListDisplays or CommandKind.ListWindows
        or CommandKind.FindWindow or CommandKind.Screenshot or CommandKind.ListControls or CommandKind.FindControl
        or CommandKind.Inspect or CommandKind.Wait or CommandKind.WaitFor or CommandKind.Where or CommandKind.Unknown;

    /// <summary>Failures the session raises while resolving or checking, before it sends any input.</summary>
    internal static bool IsPreDispatchFailure(string? category) => category is FailureCategories.NotFound or FailureCategories.Ambiguous
        or FailureCategories.NotUnderstood or FailureCategories.UserActive or FailureCategories.Permission
        or FailureCategories.AuthenticationRequired or FailureCategories.TargetElevated or FailureCategories.Unsupported;

    // A private value the host cannot supply now (no saved password, no sign-in code in the mailbox) needs a person.
    private static DesktopToolResult Unresolved(string slot) => Refused(
        $"The host could not supply the private value '{slot}' now; a person must enter it.", FailureCategories.AuthenticationRequired);

    private static DesktopToolResult Refused(string error, string category = FailureCategories.NotUnderstood) =>
        new(false, error) { Error = error, FailureCategory = category, Dispatched = false };

    private static DesktopToolResult Read(CommandResult r) => Convert(r, dispatched: false, null, null);

    private static DesktopToolResult Acted(CommandResult r, string? slot = null, string? secret = null) =>
        Convert(r, dispatched: r.Succeeded || !IsPreDispatchFailure(r.FailureCategory), slot, secret);

    private static DesktopToolResult Convert(CommandResult r, bool dispatched, string? slot, string? secret)
    {
        JsonNode? data = null;
        if (r.Data is not null)
        {
            var stripped = StripImage(r.Data)!;
            try { data = JsonSerializer.SerializeToNode(stripped, stripped.GetType(), Json); }
            catch (NotSupportedException) { data = null; }
        }
        var outcome = r.Data is ActionReceipt receipt ? receipt.Outcome : null;
        var shot = r.Data as ScreenshotReceipt ?? (r.Data as IEnumerable<ScreenshotReceipt>)?.FirstOrDefault();
        // Only a scaled image goes to the model; without a scaler the full capture stays on disk and no image is attached.
        var image = shot is { PngBase64: { } b64, ImageMime: { } mime, ImageScale: not null } ? new ImagePart(b64, mime) : null;
        var text = r.Text;
        if (shot is not null)
            text += image is null
                ? "\nNo image attached (no downscaler is configured); the capture is on disk at the path above."
                : $"\nImage {shot.ImageWidth}x{shot.ImageHeight} at scale {shot.ImageScale:0.####} of the {shot.Width}x{shot.Height} capture at screen {shot.ScreenBounds}: "
                  + $"screen x = {shot.ScreenBounds.Left} + x / {shot.ImageScale:0.####}, screen y = {shot.ScreenBounds.Top} + y / {shot.ImageScale:0.####}.";
        var result = new DesktopToolResult(r.Succeeded, text)
        {
            Error = r.Error, FailureCategory = r.FailureCategory, Files = r.Files, Data = data, Outcome = outcome, Dispatched = dispatched, Image = image,
        };
        return secret is null ? result : Redact(result, slot!, secret);
    }

    // Screenshots stay on disk; procedure receipts and model transcripts carry the path, never the pixels.
    private static object? StripImage(object? data) => data switch
    {
        ScreenshotReceipt s => s with { PngBase64 = null },
        IEnumerable<ScreenshotReceipt> list => list.Select(s => s with { PngBase64 = null }).ToList(),
        _ => data,
    };

    internal static DesktopToolResult Redact(DesktopToolResult r, string slot, string secret)
    {
        if (secret.Length == 0) return r;
        var mask = $"[secret:{slot}]";
        string? Scrub(string? s) => s?.Replace(secret, mask, StringComparison.Ordinal);
        JsonNode? Walk(JsonNode? node) => node switch
        {
            JsonObject o => new JsonObject(o.Select(p => KeyValuePair.Create(p.Key, Walk(p.Value)))),
            JsonArray a => new JsonArray(a.Select(Walk).ToArray()),
            JsonValue v when v.TryGetValue<string>(out var s) => JsonValue.Create(Scrub(s)),
            null => null,
            _ => node.DeepClone(),
        };
        return r with { Text = Scrub(r.Text)!, Error = Scrub(r.Error), Data = Walk(r.Data) };
    }

    // Window argument: w\d+ is an alias, an integer is a native handle, anything else is the name text.
    internal static WindowSelector? Window(string? window)
    {
        var w = window?.Trim();
        if (string.IsNullOrEmpty(w)) return null;
        if (IsWindowAlias(w)) return new WindowSelector(Alias: w.ToLowerInvariant());
        if (long.TryParse(w, out var handle)) return new WindowSelector(Handle: handle);
        return new WindowSelector(NameText: w);
    }

    // Control argument: c\d+ is an alias, anything else is the whole name text (with an optional type filter).
    internal static ControlSelector? Control(string? control, string? controlType)
    {
        var c = control?.Trim();
        if (string.IsNullOrEmpty(c)) return null;
        if (IsControlAlias(c)) return new ControlSelector(Alias: c.ToLowerInvariant());
        return new ControlSelector(ControlType: NormalizeType(controlType), NameText: c);
    }

    internal static bool IsWindowAlias(string s) => s.Length > 1 && (s[0] is 'w' or 'W') && s.Skip(1).All(char.IsAsciiDigit);
    internal static bool IsControlAlias(string s) => s.Length > 1 && (s[0] is 'c' or 'C') && s.Skip(1).All(char.IsAsciiDigit);

    internal static readonly Dictionary<string, string> TypeAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["link"] = "hyperlink", ["anchor"] = "hyperlink", ["btn"] = "button",
        ["textbox"] = "edit", ["text box"] = "edit", ["field"] = "edit", ["input"] = "edit", ["textfield"] = "edit",
        ["check box"] = "checkbox", ["radio"] = "radiobutton", ["dropdown"] = "combobox", ["combo"] = "combobox", ["select"] = "combobox",
        ["menu item"] = "menuitem", ["tab"] = "tabitem", ["list item"] = "listitem", ["row"] = "listitem",
        ["tree item"] = "treeitem", ["label"] = "text", ["picture"] = "image", ["icon"] = "image", ["page"] = "document",
    };

    internal static readonly HashSet<string> ControlTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "hyperlink", "button", "edit", "checkbox", "radiobutton", "combobox", "listitem", "menuitem", "tabitem", "text",
        "document", "image", "slider", "treeitem", "pane", "window", "list", "menu", "menubar", "toolbar", "group",
        "table", "dataitem", "spinner", "splitbutton", "header", "headeritem", "statusbar", "tab", "tree", "custom",
    };

    internal static string? NormalizeType(string? type)
    {
        var t = type?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        return TypeAliases.TryGetValue(t, out var mapped) ? mapped : t.ToLowerInvariant();
    }
}

/// <summary>How much a listing shows a model. Defaults: 60 rows, names cut at 90 characters.</summary>
public sealed record DesktopToolOptions
{
    public static readonly DesktopToolOptions Default = new();
    public int ListingMaxRows { get; init; } = 60;
    public int NameMaxLength { get; init; } = 90;
}
