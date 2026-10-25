using System.Globalization;
using System.Text.RegularExpressions;

namespace SwarmCore.RemoteControl;

/// <summary>
/// Turns one line of plain text into a <see cref="RemoteCommand"/> following COMMANDS.md. The parser never touches
/// the desktop and never throws: anything it cannot place becomes <see cref="CommandKind.Unknown"/> whose
/// <see cref="RemoteCommand.Error"/> says what was not understood and suggests the nearest valid form.
/// </summary>
public static partial class RemoteCommandParser
{
    internal const string GeneralHint =
        "Try: windows, screenshot screen 2, controls in w3, click link Next Page in chrome, set c7 to \"text\", press ctrl+s, or help.";

    public static RemoteCommand Parse(string text)
    {
        var raw = text ?? "";
        try
        {
            return new Parser(raw).Parse() with { RawText = raw };
        }
        catch (Exception ex)
        {
            return new RemoteCommand(CommandKind.Unknown)
            {
                RawText = raw,
                Error = $"Could not understand \"{raw.Trim()}\" ({ex.Message}). {GeneralHint}",
            };
        }
    }

    /// <summary>
    /// Splits text that may carry several commands: one per line, or several on a line separated by
    /// <c>" ; "</c> (a semicolon with a space on each side). Quoted text is never split. Blank lines and
    /// lines starting with <c>#</c> are dropped.
    /// </summary>
    public static IReadOnlyList<string> SplitCommands(string text)
    {
        var steps = new List<string>();
        if (string.IsNullOrEmpty(text)) return steps;
        var current = new System.Text.StringBuilder();
        void Flush()
        {
            var step = current.ToString().Trim();
            current.Clear();
            if (step.Length > 0 && !step.StartsWith('#')) steps.Add(step);
        }
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\r' or '\n') { Flush(); continue; }
            if (Parser.OpensQuote(text, i))
            {
                // Quotes never span a line; an unmatched quote is a literal character.
                var j = Parser.FindClose(text, i, c);
                if (j >= 0) { current.Append(text, i, j - i + 1); i = j; continue; }
            }
            if (c == ';' && i > 0 && text[i - 1] == ' ' && i + 1 < text.Length && text[i + 1] == ' ') { Flush(); continue; }
            current.Append(c);
        }
        Flush();
        return steps;
    }

    /// <summary>True when a batch step starts with <c>try </c>: its failure does not stop the batch.</summary>
    public static bool IsTryStep(string step) => step.StartsWith("try ", StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- vocabulary

    private static HashSet<string> Set(params string[] items) => new(items, StringComparer.Ordinal);

    private static readonly HashSet<string> Filler = Set(
        "give", "me", "a", "an", "the", "of", "please", "and", "all", "their", "its", "to", "on", "in", "at", "for",
        "with", "then", "now", "can", "you", "could", "would", "go", "ahead", "just", "show", "get", "want", "i",
        "need", "let's", "lets", "my", "this", "that", "some", "kindly", "also", "us");

    private static readonly HashSet<string> EdgeNoise = Set(
        "over", "onto", "into", "inside", "within", "from", "by", "is", "are", "it", "called", "named", "labeled",
        "labelled", "titled", "says", "say", "there", "any");

    private static readonly HashSet<string> Articles = Set("the", "a", "an", "my");
    private static readonly HashSet<string> WindowPrepositions = Set("in", "on", "of", "inside", "within", "for", "from");
    private static readonly HashSet<string> WindowNouns = Set("window", "app", "application", "program", "dialog", "sheet", "popup");
    private static readonly HashSet<string> CurrentWords = Set("current", "active", "foreground", "focused", "selected", "this");
    private static readonly HashSet<string> ControlKeywords = Set("control", "element", "id", "controlid");
    private static readonly HashSet<string> ControlNouns = Set("controls", "elements", "widgets", "control", "element");
    private static readonly HashSet<string> IncludeControlWords = Set("controls", "control", "elements", "element", "widgets");
    private static readonly HashSet<string> IncludeControlExtras = Set("names", "name", "values", "value", "ids", "list", "listing", "plus", "including", "include", "types");
    private static readonly HashSet<string> WindowListNouns = Set("windows", "apps", "applications", "programs", "processes", "tasks", "running", "open");
    private static readonly HashSet<string> ScreenNouns = Set("screens", "monitors", "displays");
    private static readonly HashSet<string> ScreenWords = Set("screen", "monitor", "display", "screens", "monitors", "displays");
    private static readonly HashSet<string> RegionWords = Set("region", "area", "rect", "rectangle", "portion");
    private static readonly HashSet<string> Directions = Set("up", "down", "left", "right");
    private static readonly HashSet<string> NameMarkers = Set("matching", "named", "called", "containing", "contains", "like", "labeled", "labelled", "titled", "filter", "filtered");
    private static readonly HashSet<string> Stops = Set(
        "and", "then", "to", "into", "onto", "=", ":", "with", "matching", "called", "named", "labeled", "labelled",
        "titled", "containing", "as", "say", "says", "value", "at", "is", "has", "contains", "using", "by", "via");
    private static readonly HashSet<string> MouseWords = Set("mouse", "cursor", "pointer", "focus", "focused", "focussed", "keyboard", "caret", "mouseposition");
    private static readonly HashSet<string> QuestionWords = Set("what", "what's", "whats", "which", "where", "where's", "wheres", "is", "are", "how", "who", "who's");

    private static readonly HashSet<string> ScreenshotVerbs = Set("screenshot", "screenshots", "snapshot", "capture", "grab", "snap", "screengrab", "screencap", "screencapture");
    private static readonly HashSet<string> DragVerbs = Set("drag");
    private static readonly HashSet<string> GuardVerbs = Set("guard");
    private static readonly HashSet<string> ClickVerbs = Set("click", "tap", "press", "hit", "push", "doubleclick", "dblclick", "rightclick", "middleclick", "leftclick", "open", "contextmenu", "doubletap");
    private static readonly HashSet<string> InvokeVerbs = Set("invoke", "toggle", "check", "uncheck", "select", "expand", "collapse", "activate", "focus", "tick", "untick");
    private static readonly HashSet<string> FocusVerbs = Set("switch", "bring", "raise", "restore", "go", "show");
    private static readonly HashSet<string> SetVerbs = Set("set", "change", "update", "fill", "make", "assign");
    private static readonly HashSet<string> TypeFamily = Set("put", "enter", "type", "write", "input", "insert");
    private static readonly HashSet<string> ClearVerbs = Set("clear", "empty", "erase");
    private static readonly HashSet<string> KeyVerbs = Set("key", "keys", "hotkey", "shortcut", "send", "keystroke", "keystrokes", "chord");
    private static readonly HashSet<string> MoveVerbs = Set("move", "hover", "mouse", "cursor", "pointer");
    private static readonly HashSet<string> ScrollVerbs = Set("scroll", "wheel", "pagedown", "pageup");
    private static readonly HashSet<string> WaitVerbs = Set("wait", "pause", "sleep", "delay");
    private static readonly HashSet<string> ListVerbs = Set("list", "enumerate");
    private static readonly HashSet<string> FindVerbs = Set("find", "search", "locate", "look");
    private static readonly HashSet<string> InspectVerbs = Set("inspect", "describe", "read", "examine", "properties", "details", "info", "value");
    private static readonly HashSet<string> HelpWords = Set("help", "commands", "usage", "?");
    private static readonly HashSet<string> QuitWords = Set("quit", "exit", "bye", "goodbye", "q", "logout");

    private static readonly HashSet<string> AllVerbs = BuildAllVerbs();

    private static HashSet<string> BuildAllVerbs()
    {
        var all = Set("mouseposition");
        foreach (var set in new[] { ScreenshotVerbs, DragVerbs, GuardVerbs, ClickVerbs, InvokeVerbs, FocusVerbs, SetVerbs, TypeFamily, ClearVerbs, KeyVerbs, MoveVerbs, ScrollVerbs, WaitVerbs, ListVerbs, FindVerbs, InspectVerbs })
            all.UnionWith(set);
        return all;
    }

    private static readonly HashSet<string> ClickNoise = Set("mouse", "button-click");
    private static readonly HashSet<string> SetNoise = Set("set", "change", "update", "fill", "make", "assign", "value", "values", "contents", "content", "and", "then");
    private static readonly HashSet<string> MoveNoise = Set("mouse", "cursor", "pointer", "over", "to");
    private static readonly HashSet<string> ScrollNoise = Set("times", "notches", "notch", "lines", "clicks", "steps", "pages", "page", "wheel", "mouse", "by");
    private static readonly HashSet<string> InspectNoise = Set("value", "values", "properties", "details", "info", "of", "does", "say", "says", "what", "what's", "whats", "is", "show", "tell", "about", "state", "type", "who");
    private static readonly HashSet<string> ListNoise = Set("list", "values", "value", "names", "ids", "what", "which", "are", "is", "there", "any", "show", "find", "their", "types", "how", "many", "do", "have", "has");
    private static readonly HashSet<string> FindNoise = Set("find", "search", "locate", "look", "for", "is", "there", "any");
    private static readonly HashSet<string> KeyNoise = Set("the", "key", "keys", "please", "shortcut", "combination", "combo", "chord", "now", "hotkey", "keystroke", "arrow");
    private static readonly HashSet<string> FocusNoise = Set("to", "front", "foreground", "top", "forward", "up", "over", "focus");
    private static readonly HashSet<string> ShotNoise = Set("take", "picture", "photo", "shot", "image", "please", "capture", "screenshot", "grab", "current", "active", "whole", "entire");

    private static readonly HashSet<string> WaitGoneWords = Set("gone", "close", "closes", "closed", "disappear", "disappears", "disappeared", "vanish", "vanishes", "vanished", "exits", "exited");
    private static readonly HashSet<string> WaitAppearWords = Set("appear", "appears", "appeared", "exist", "exists", "opens", "opened", "open", "shows", "shown", "visible", "present", "ready", "up");
    private static readonly HashSet<string> WaitChangeWords = Set("change", "changes", "changed");
    private static readonly HashSet<string> WaitEnabledWords = Set("enabled", "enable", "active");
    private static readonly HashSet<string> WaitValueMarkers = Set("says", "say", "equals", "equal", "reads", "be", "becomes", "become", "=", "shows");
    private static readonly HashSet<string> WaitNoise = Set("is", "are", "to", "has", "been", "be", "value", "until", "till", "for");
    private static readonly HashSet<string> WaitTimeoutMarkers = Set("timeout", "max", "maximum", "within", "for");

    /// <summary>Type words (singular and plural) mapped to platform-neutral control types.</summary>
    private static readonly Dictionary<string, string> TypeWords = BuildTypeWords();

    private static readonly HashSet<string> PluralTypeWords = Set(
        "links", "hyperlinks", "anchors", "buttons", "btns", "textboxes", "textfields", "fields", "inputs", "edits",
        "editboxes", "editfields", "entries", "textareas", "textviews", "texteditors", "editors", "checkboxes", "tickboxes", "radios", "radiobuttons", "optionbuttons",
        "dropdowns", "combos", "comboboxes", "selects", "pickers", "menus", "menuitems", "tabs", "tabitems",
        "listitems", "items", "rows", "treeitems", "nodes", "sliders", "images", "pictures", "icons", "texts", "labels", "headings",
        "documents", "pages", "panes", "panels", "areas", "groups", "boxes");

    private static Dictionary<string, string> BuildTypeWords()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string type, params string[] words) { foreach (var w in words) map[w] = type; }
        Add("hyperlink", "link", "links", "hyperlink", "hyperlinks", "anchor", "anchors");
        Add("button", "button", "buttons", "btn", "btns");
        // Single-line words match edits only; "field"/"input" also match comboboxes; multi-line words match documents too.
        Add("edit", "textbox", "textboxes", "edit", "edits", "editbox", "editboxes", "entry", "entries", "box", "boxes");
        Add("field", "field", "fields", "input", "inputs", "textfield", "textfields", "editfield", "editfields");
        Add("document", "textarea", "textareas", "textview", "textviews", "texteditor", "texteditors", "editor", "editors", "document", "documents");
        Add("checkbox", "checkbox", "checkboxes", "tickbox", "tickboxes");
        Add("radiobutton", "radio", "radios", "radiobutton", "radiobuttons", "optionbutton", "optionbuttons");
        Add("combobox", "dropdown", "dropdowns", "combo", "combos", "combobox", "comboboxes", "select", "selects", "picker", "pickers");
        Add("menuitem", "menu", "menus", "menuitem", "menuitems");
        Add("tabitem", "tab", "tabs", "tabitem", "tabitems");
        Add("listitem", "listitem", "listitems", "item", "items", "row", "rows");
        Add("treeitem", "treeitem", "treeitems", "node", "nodes");
        Add("slider", "slider", "sliders");
        Add("image", "image", "images", "picture", "pictures", "icon", "icons");
        Add("text", "text", "texts", "label", "labels", "heading", "headings");
        Add("document", "page", "pages");
        Add("pane", "pane", "panes", "panel", "panels", "area", "areas");
        Add("group", "group", "groups");
        return map;
    }

    private static readonly Dictionary<string, string> Merges = BuildMerges();

    private static Dictionary<string, string> BuildMerges()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["double click"] = "doubleclick", ["double clicks"] = "doubleclick", ["double tap"] = "doubletap",
            ["dbl click"] = "dblclick", ["right click"] = "rightclick", ["middle click"] = "middleclick",
            ["left click"] = "leftclick", ["screen shot"] = "screenshot", ["screen shots"] = "screenshot",
            ["context menu"] = "contextmenu", ["page down"] = "pagedown", ["page up"] = "pageup",
            ["mouse position"] = "mouseposition", ["mouse location"] = "mouseposition", ["cursor position"] = "mouseposition",
            ["cursor location"] = "mouseposition", ["pointer position"] = "mouseposition", ["print screen"] = "printscreen",
        };
        foreach (var (a, b) in new[]
                 {
                     ("text", "box"), ("text", "field"), ("edit", "box"), ("edit", "field"), ("check", "box"), ("tick", "box"),
                     ("radio", "button"), ("option", "button"), ("drop", "down"), ("combo", "box"), ("menu", "item"),
                     ("tab", "item"), ("list", "item"), ("tree", "item"), ("text", "area"), ("text", "view"), ("text", "editor"),
                 })
        {
            map[$"{a} {b}"] = a + b;
            map[$"{a} {b}{(b.EndsWith('x') ? "es" : "s")}"] = a + b + (b.EndsWith('x') ? "es" : "s");
        }
        return map;
    }

    private static readonly HashSet<string> Compounds = BuildCompounds();

    private static HashSet<string> BuildCompounds()
    {
        var set = Set("hotkey", "screengrab", "screencap", "screencapture");
        set.UnionWith(Merges.Values);
        return set;
    }

    private static readonly Dictionary<string, int> NumberWords = new(StringComparer.Ordinal)
    {
        ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8,
        ["nine"] = 9, ["ten"] = 10,
    };

    /// <summary>Trailing words kept on a name when they have no object ("Log in", "Remember me", "Show all").</summary>
    private static readonly HashSet<string> Particles = Set("in", "on", "out", "up", "down", "off", "over", "at", "to", "for", "with", "by", "of", "all", "me", "back", "away", "more");
    private static readonly HashSet<string> ClickModifierWords = Set("shift", "ctrl", "control", "alt", "option", "cmd", "command", "win", "primary", "meta", "super");

    private static readonly Dictionary<string, int> OrdinalWords = new(StringComparer.Ordinal)
    {
        ["first"] = 1, ["second"] = 2, ["third"] = 3, ["fourth"] = 4, ["fifth"] = 5, ["sixth"] = 6,
        ["seventh"] = 7, ["eighth"] = 8, ["ninth"] = 9, ["tenth"] = 10,
    };

    private static readonly Dictionary<string, string> SuggestionExamples = new(StringComparer.Ordinal)
    {
        ["click"] = "click link Next Page in chrome",
        ["screenshot"] = "screenshot screen 2",
        ["windows"] = "windows",
        ["controls"] = "controls in w3",
        ["screens"] = "screens",
        ["set"] = "set c7 to \"text\"",
        ["type"] = "type \"hello\"",
        ["press"] = "press ctrl+s",
        ["scroll"] = "scroll down 3",
        ["focus"] = "focus w3",
        ["inspect"] = "inspect c12",
        ["hover"] = "hover c12",
        ["wait"] = "wait 2",
        ["find"] = "find window chrome",
        ["move"] = "move mouse to 100,200",
        ["invoke"] = "invoke c12",
        ["toggle"] = "toggle c7",
        ["expand"] = "expand c3",
        ["collapse"] = "collapse c3",
        ["select"] = "select c9",
        ["check"] = "check c7",
        ["help"] = "help",
        ["list"] = "list windows",
        ["capture"] = "capture monitor 2",
        ["double"] = "double click c4",
        ["right"] = "right click c4",
        ["where"] = "where",
        ["clear"] = "clear c12",
    };

    private const string EdgePunctuation = "?!.,;()[]{}\"“”‘’'";

    [GeneratedRegex(@"\u0001\d+\u0001|[^\s\u0001]+")] private static partial Regex TokenRegex();
    [GeneratedRegex(@"\u0001(\d+)\u0001")] private static partial Regex PlaceholderRegex();
    [GeneratedRegex(@"^w(\d+)$")] private static partial Regex WindowAliasRegex();
    [GeneratedRegex(@"^c\d+$")] private static partial Regex ControlAliasRegex();
    [GeneratedRegex(@"^(-?\d+),(-?\d+)$")] private static partial Regex PointRegex();
    [GeneratedRegex(@"^(\d+)x(\d+)$")] private static partial Regex SizeRegex();
    [GeneratedRegex(@"^(\d+(?:\.\d+)?)(ms|msecs?|millis|milliseconds?|s|secs?|seconds?|m|mins?|minutes?)?$")] private static partial Regex DurationRegex();
    [GeneratedRegex(@"^(ms|msecs?|millis|milliseconds?|s|secs?|seconds?|m|mins?|minutes?)$")] private static partial Regex UnitRegex();
    [GeneratedRegex(@"^(?:(\d+)(?:st|nd|rd|th)|#(\d+))$")] private static partial Regex OrdinalRegex();
    [GeneratedRegex(@"^[x×](\d+)$")] private static partial Regex RepeatRegex();
    [GeneratedRegex(@"^((?:shift|ctrl|control|alt|option|cmd|command|win|primary|meta|super)[+-])+(click|doubleclick|rightclick|middleclick|leftclick|tap)$")] private static partial Regex ModifierClickRegex();

    // ---------------------------------------------------------------- tokens

    private sealed class Token
    {
        public required string Text { get; init; }
        public required string Word { get; set; }
        public string? Quoted { get; init; }
        public int Start { get; init; }
        public int End { get; init; }
        public bool Used { get; set; }
        public bool IsQuoted => Quoted is not null;
        public string Display => Quoted ?? Trim(Text);
        public bool SentenceStart { get; set; }
        /// <summary>Typed with an initial capital somewhere other than the start of the sentence: part of a name.</summary>
        public bool Capitalized => !IsQuoted && !SentenceStart && Text.Length > 0 && char.IsUpper(Text[0]);
        private static string Trim(string s) { var t = s.Trim(EdgePunctuation.ToCharArray()); return t.Length == 0 ? s : t; }
    }

    // ---------------------------------------------------------------- parser

    private sealed class Parser
    {
        private readonly string raw;
        private readonly string processed;
        private readonly List<string> originals = [];
        private readonly List<Token> t;
        private Token? verb;
        private bool windowMentioned;
        /// <summary>Set by commands after which nothing but a window can follow: a window name then runs to a hard stop.</summary>
        private bool windowNameToEnd;
        private readonly List<string> clickModifiers = [];

        [Flags]
        private enum WindowMode { Explicit = 0, Prepositions = 1, SkipAfterVerb = 2, NoBareHandles = 4 }

        private const WindowMode Read = WindowMode.Prepositions;
        private const WindowMode Act = WindowMode.Prepositions | WindowMode.SkipAfterVerb;

        public Parser(string raw)
        {
            this.raw = raw;
            var contents = new List<string>();
            processed = ExtractQuotes(raw, originals, contents);
            t = Tokenize(processed, contents);
        }

        private int N => t.Count;
        private int VerbIndex => verb is null ? -1 : t.IndexOf(verb);
        private int AfterVerb => VerbIndex + 1;

        // ------------------------------------------------ preprocessing

        private static string ExtractQuotes(string raw, List<string> originals, List<string> contents)
        {
            var sb = new System.Text.StringBuilder(raw.Length);
            var i = 0;
            while (i < raw.Length)
            {
                var c = raw[i];
                if (OpensQuote(raw, i) && FindClose(raw, i, c) is var j && j >= 0)
                {
                    contents.Add(raw[(i + 1)..j]);
                    originals.Add(raw[i..(j + 1)]);
                    sb.Append('\u0001').Append((contents.Count - 1).ToString(CultureInfo.InvariantCulture)).Append('\u0001');
                    i = j + 1;
                    continue;
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        /// <summary>
        /// A straight quote (" or ') opens only at the start of a word, so the inch mark in <c>5" monitor</c> and the
        /// apostrophe in <c>what's</c> stay literal; curly opening quotes always open.
        /// </summary>
        internal static bool OpensQuote(string raw, int i)
        {
            var c = raw[i];
            var wordStart = i == 0 || char.IsWhiteSpace(raw[i - 1]) || "=:(,[".Contains(raw[i - 1]);
            return c is '“' or '‘' || (c is '"' or '\'' && wordStart);
        }

        internal static int FindClose(string raw, int open, char c)
        {
            for (var j = open + 1; j < raw.Length; j++)
            {
                var d = raw[j];
                if (d is '\r' or '\n') return -1;
                switch (c)
                {
                    case '"' when d is '"' or '”':
                    case '“' when d is '”' or '"':
                    case '‘' when d is '’':
                        return j;
                    case '\'' when d == '\'' && (j + 1 == raw.Length || char.IsWhiteSpace(raw[j + 1]) || ".,!?;:)".Contains(raw[j + 1])):
                        return j;
                }
            }
            return -1;
        }

        private static List<Token> Tokenize(string processed, List<string> contents)
        {
            var list = new List<Token>();
            foreach (Match m in TokenRegex().Matches(processed))
            {
                if (m.Value[0] == '\u0001')
                {
                    var n = int.Parse(m.Value[1..^1], CultureInfo.InvariantCulture);
                    list.Add(new Token { Text = m.Value, Word = "", Quoted = contents[n], Start = m.Index, End = m.Index + m.Length });
                    continue;
                }
                foreach (var (s, len) in SplitPieces(m.Value))
                {
                    var text = m.Value.Substring(s, len);
                    list.Add(new Token { Text = text, Word = Normalize(text), Start = m.Index + s, End = m.Index + s + len });
                }
            }
            for (var i = 0; i < list.Count - 1; i++)
            {
                var (a, b) = (list[i], list[i + 1]);
                if (a.IsQuoted || b.IsQuoted || !Merges.TryGetValue($"{a.Word} {b.Word}", out var merged)) continue;
                list[i] = new Token { Text = processed[a.Start..b.End], Word = merged, Start = a.Start, End = b.End };
                list.RemoveAt(i + 1);
            }
            if (list.Count > 0) list[0].SentenceStart = true;
            return list;
        }

        private static List<(int Start, int Length)> SplitPieces(string v)
        {
            var pieces = new List<(int, int)>();
            var start = 0;
            for (var i = 0; i < v.Length; i++)
            {
                if (v[i] != '=') continue;
                if (i > start) pieces.Add((start, i - start));
                pieces.Add((i, 1));
                start = i + 1;
            }
            if (start < v.Length) pieces.Add((start, v.Length - start));
            if (pieces.Count > 0)
            {
                var (s, l) = pieces[^1];
                if (l > 1 && v[s + l - 1] == ':')
                {
                    pieces[^1] = (s, l - 1);
                    pieces.Add((s + l - 1, 1));
                }
            }
            return pieces;
        }

        private static string Normalize(string text)
        {
            var w = text.ToLowerInvariant().Replace('’', '\'');
            var trimmed = w.Trim(EdgePunctuation.ToCharArray());
            if (trimmed.Length == 0) return w;
            w = trimmed;
            if (w.Contains('-'))
            {
                var joined = w.Replace("-", "");
                if (Compounds.Contains(joined)) w = joined;
            }
            return w;
        }

        // ------------------------------------------------ token helpers

        private bool Open(int i) => i >= 0 && i < N && !t[i].Used;
        private bool OpenWord(int i) => Open(i) && !t[i].IsQuoted;
        private bool IsWord(int i, HashSet<string> set) => OpenWord(i) && set.Contains(t[i].Word);
        private bool IsFiller(int i) => !t[i].IsQuoted && Filler.Contains(t[i].Word);
        private bool IsControlAlias(int i) => !t[i].IsQuoted && ControlAliasRegex().IsMatch(t[i].Word);
        private bool IsWindowAlias(int i) => !t[i].IsQuoted && WindowAliasRegex().IsMatch(t[i].Word);
        private bool IsControlKeyword(int i) => OpenWord(i) && ControlKeywords.Contains(t[i].Word) && !t[i].Capitalized;
        private bool IsTypeWord(int i) => !t[i].IsQuoted && TypeWords.ContainsKey(t[i].Word);
        private bool IsLowerTypeWord(int i) => IsTypeWord(i) && !t[i].Capitalized;
        private static bool IsInt(string w, out int n) => int.TryParse(w, NumberStyles.Integer, CultureInfo.InvariantCulture, out n);
        private IEnumerable<int> Range(int from, int to) => Enumerable.Range(Math.Max(0, from), Math.Max(0, Math.Min(to, N) - Math.Max(0, from)));
        private bool AnyOpen(HashSet<string> set) => t.Any(x => !x.Used && !x.IsQuoted && set.Contains(x.Word));
        private bool AnyOpen(params string[] words) => t.Any(x => !x.Used && !x.IsQuoted && words.Contains(x.Word));
        private bool AnyOpenLowerTypeWord(bool pluralOnly = false) =>
            Range(0, N).Any(i => OpenWord(i) && IsLowerTypeWord(i) && (!pluralOnly || PluralTypeWords.Contains(t[i].Word)));
        private void Consume(int i) { if (i >= 0 && i < N) t[i].Used = true; }
        private void ConsumeAll(HashSet<string> set, int from = 0) { foreach (var i in Range(from, N)) if (IsWord(i, set) && !t[i].Capitalized) Consume(i); }
        private string Join(IEnumerable<int> indices) => string.Join(" ", indices.Select(i => t[i].Display)).Trim();

        private string Restore(string s) => PlaceholderRegex().Replace(s, m => originals[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)]);

        /// <summary>Literal text of tokens [a, b): one quoted token yields its content; otherwise the original slice.</summary>
        private string ValueText(int a, int b)
        {
            if (a >= b) return "";
            if (b - a == 1 && t[a].IsQuoted) return t[a].Quoted!;
            return Restore(processed[t[a].Start..t[b - 1].End]).Trim();
        }

        private static RemoteCommand Cmd(CommandKind kind) => new(kind);
        private static RemoteCommand Unknown(string error) => new(CommandKind.Unknown) { Error = error };

        private bool OnlyFillersBetween(int a, int b) => Range(a + 1, b).All(i => t[i].Used || IsFiller(i));

        private bool IsNameStop(int i)
        {
            var tok = t[i];
            if (tok.IsQuoted) return false;
            var w = tok.Word;
            if (ControlAliasRegex().IsMatch(w) || WindowAliasRegex().IsMatch(w) || IsInt(w, out _)) return true;
            if (tok.Capitalized) return false;
            if (WindowPrepositions.Contains(w) || ControlKeywords.Contains(w) || IncludeControlWords.Contains(w)) return true;
            if (w is "timeout" or "max" or "maximum" || (w == "up" && i + 1 < N && t[i + 1].Word == "to")) return true;
            // "Welcome to Chrome": a lowercase "to" between capitalized words belongs to the name.
            if (w == "to" && i > 0 && t[i - 1].Capitalized && i + 1 < N && t[i + 1].Capitalized) return false;
            return Stops.Contains(w) || Filler.Contains(w) || CurrentWords.Contains(w) || ScreenWords.Contains(w)
                   || AllVerbs.Contains(w) || TypeWords.ContainsKey(w) || IncludeControlExtras.Contains(w);
        }

        /// <summary>Where a window name that may run to the end of the command still has to stop.</summary>
        private bool IsHardStop(int i)
        {
            if (!Open(i)) return true;
            var tok = t[i];
            if (tok.IsQuoted) return false;
            var w = tok.Word;
            if (ControlAliasRegex().IsMatch(w) || WindowAliasRegex().IsMatch(w)) return true;
            if (tok.Capitalized) return false;
            if (w is "timeout" or "max" or "maximum" || (w == "up" && i + 1 < N && t[i + 1].Word == "to")) return true;
            return w is "and" or "then" or "with" or "=" or ":" or "matching" or "called" or "named" or "labeled" or "labelled" or "containing"
                   || ControlKeywords.Contains(w) || IncludeControlWords.Contains(w) || IncludeControlExtras.Contains(w);
        }

        /// <summary>Collects a window name starting at <paramref name="j"/>; returns the indices and where it stopped.</summary>
        private (List<int> Words, int End) CollectWindowName(int j, int to)
        {
            var words = new List<int>();
            if (windowNameToEnd)
            {
                while (j < to && !IsHardStop(j)) words.Add(j++);
                while (words.Count > 0 && !t[words[^1]].IsQuoted && !t[words[^1]].Capitalized && (Filler.Contains(t[words[^1]].Word) || EdgeNoise.Contains(t[words[^1]].Word)))
                    words.RemoveAt(words.Count - 1);
                return (words, words.Count == 0 ? j : words[^1] + 1);
            }
            while (j < to && Open(j) && !IsNameStop(j)) words.Add(j++);
            return (words, j);
        }

        // ------------------------------------------------ entry

        public RemoteCommand Parse()
        {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0) return Unknown($"Nothing to do: the command was empty. {GeneralHint}");
            if (trimmed == "?") return Cmd(CommandKind.Help);
            var words = t.Where(x => !x.IsQuoted).Select(x => x.Word).ToList();
            if (words.Count == 0) return Unknown($"Only quoted text was given; say what to do with it, for example: type {trimmed}");
            if (HelpWords.Contains(words[0])) return Cmd(CommandKind.Help);
            if (words.Any(QuitWords.Contains) && words.All(w => QuitWords.Contains(w) || w is "please" or "now" or "ok" or "okay"))
                return Cmd(CommandKind.Quit);
            if (TryQuestion() is { } question) return question;
            ExtractClickModifiers();
            verb = FindVerb();
            if (verb is null) return TryNounCommand() ?? UnknownWithSuggestion();
            verb.Used = true;
            return Dispatch(verb.Word);
        }

        /// <summary>"shift click", "ctrl+click", "control-click", "cmd click": record the modifiers and leave the click verb.</summary>
        private void ExtractClickModifiers()
        {
            for (var i = 0; i < N; i++)
            {
                if (t[i].IsQuoted || t[i].Used) continue;
                var m = ModifierClickRegex().Match(t[i].Word);
                if (m.Success)
                {
                    clickModifiers.AddRange(t[i].Word.Split(['+', '-'])[..^1]);
                    t[i].Word = m.Groups[2].Value;
                    continue;
                }
                if (!ClickModifierWords.Contains(t[i].Word)) continue;
                var j = i;
                while (j < N && !t[j].IsQuoted && ClickModifierWords.Contains(t[j].Word)) j++;
                if (j < N && !t[j].IsQuoted && t[j].Word is "click" or "doubleclick" or "rightclick" or "middleclick" or "leftclick" or "tap")
                {
                    for (var k = i; k < j; k++) { clickModifiers.Add(t[k].Word); t[k].Used = true; }
                    i = j;
                }
            }
        }

        private string? NormalizedClickModifiers()
        {
            if (clickModifiers.Count == 0) return null;
            var names = clickModifiers.Select(m => m is "cmd" or "command" or "meta" or "super" ? "primary" : m);
            return KeyChord.Normalize(string.Join("+", names));
        }

        private Token? FindVerb()
        {
            var firstContent = Range(0, N).FirstOrDefault(i => !t[i].IsQuoted && !Filler.Contains(t[i].Word), -1);
            for (var i = 0; i < N; i++)
            {
                var tok = t[i];
                if (tok.IsQuoted || !AllVerbs.Contains(tok.Word)) continue;
                switch (tok.Word)
                {
                    case "go":
                        if (i + 1 < N && !t[i + 1].IsQuoted && (t[i + 1].Word == "to" || WindowNouns.Contains(t[i + 1].Word) || IsWindowAlias(i + 1))) return tok;
                        continue;
                    case "show":
                        var j = i + 1;
                        while (j < N && !t[j].IsQuoted && (Articles.Contains(t[j].Word) || t[j].Word == "me")) j++;
                        if (j < N && (IsWindowAlias(j) || (!t[j].IsQuoted && WindowNouns.Contains(t[j].Word)))) return tok;
                        continue;
                    case "mouse" or "cursor" or "pointer" or "value":
                        if (i == firstContent) return tok;
                        continue;
                    case "look":
                        if (i + 1 < N && t[i + 1].Word == "for") return tok;
                        continue;
                    default:
                        return tok;
                }
            }
            return null;
        }

        private RemoteCommand Dispatch(string w) => w switch
        {
            _ when ScreenshotVerbs.Contains(w) => ParseScreenshot(),
            _ when DragVerbs.Contains(w) => ParseDrag(),
            _ when GuardVerbs.Contains(w) => ParseGuard(),
            "select" when IsSelectAll() => Cmd(CommandKind.PressKeys) with { Keys = "primary+a", KeySequence = ["primary+a"] },
            "mouseposition" => Cmd(CommandKind.Where),
            "activate" or "focus" => LooksLikeControlTarget() ? ParseInvoke(w == "focus" ? "focus" : "invoke") : ParseFocus(),
            "tick" => ParseInvoke("check"),
            "untick" => ParseInvoke("uncheck"),
            _ when InvokeVerbs.Contains(w) => ParseInvoke(w),
            _ when ClickVerbs.Contains(w) => ParseClick(w),
            _ when FocusVerbs.Contains(w) => ParseFocus(),
            _ when ClearVerbs.Contains(w) => ParseClear(),
            _ when TypeFamily.Contains(w) => ParseTypeFamily(w),
            _ when SetVerbs.Contains(w) => ParseSetFamily(),
            _ when KeyVerbs.Contains(w) => ParseKeys(),
            _ when MoveVerbs.Contains(w) => ParseMove(),
            _ when ScrollVerbs.Contains(w) => ParseScroll(w),
            _ when WaitVerbs.Contains(w) => ParseWait(),
            _ when ListVerbs.Contains(w) => ParseList(),
            _ when FindVerbs.Contains(w) => ParseFind(),
            _ when InspectVerbs.Contains(w) => ParseInspect(),
            _ => UnknownWithSuggestion(),
        };

        private bool IsSelectAll()
        {
            var rest = Range(AfterVerb, N).Where(i => Open(i) && !(OpenWord(i) && t[i].Word is "the" or "please" or "now")).ToList();
            return rest.Count is 1 or 2 && OpenWord(rest[0]) && t[rest[0]].Word == "all"
                   && (rest.Count == 1 || (OpenWord(rest[1]) && t[rest[1]].Word is "text" or "of" or "content" or "contents"));
        }

        private bool LooksLikeControlTarget() =>
            Range(AfterVerb, N).Any(i => OpenWord(i) && (IsControlAlias(i) || IsControlKeyword(i) || IsLowerTypeWord(i)));

        // ------------------------------------------------ questions and nouns

        private RemoteCommand? TryQuestion()
        {
            var f = Range(0, N).FirstOrDefault(i => !t[i].IsQuoted, -1);
            if (f < 0 || !QuestionWords.Contains(t[f].Word)) return null;
            var w0 = t[f].Word;
            var words = t.Where(x => !x.IsQuoted).Select(x => x.Word).ToHashSet(StringComparer.Ordinal);

            if ((w0.StartsWith("what", StringComparison.Ordinal) || w0 == "how")
                && (words.Contains("commands") || words.Contains("help") || (words.Contains("can") && words.Contains("do"))))
                return Cmd(CommandKind.Help);

            if (w0 == "how" && words.Contains("many"))
            {
                if (words.Overlaps(ScreenWords)) return Cmd(CommandKind.ListDisplays);
                if (words.Overlaps(WindowListNouns)) return Cmd(CommandKind.ListWindows);
                if (words.Overlaps(ControlNouns)) { Consume(f); return ParseControlListing(); }
            }

            if (w0 is "where" or "where's" or "wheres")
            {
                windowNameToEnd = true;
                Consume(f);
                var rest = Range(0, N).Where(i => OpenWord(i) && !Filler.Contains(t[i].Word) && t[i].Word is not ("is" or "are" or "was")).ToList();
                if (rest.Count == 0 || rest.Any(i => MouseWords.Contains(t[i].Word))) return Cmd(CommandKind.Where);
                ConsumeAll(Set("is", "are", "was"));
                var win = ExtractWindow(0, N, WindowMode.Explicit);
                if (win is null)
                {
                    var names = Range(0, N).Where(i => Open(i) && !IsFiller(i)).ToList();
                    if (names.Count == 0) return Cmd(CommandKind.Where);
                    win = new WindowSelector(NameText: Join(names));
                }
                return Cmd(CommandKind.FindWindow) with { Window = win };
            }

            if (words.Overlaps(Set("focus", "focused", "focussed"))) return Cmd(CommandKind.Where);

            if (w0 == "which" && words.Overlaps(WindowNouns))
            {
                ConsumeAll(Set("which", "window", "app", "application", "program", "is", "has", "contains", "shows", "called", "named", "titled", "running"));
                var names = Range(0, N).Where(i => Open(i) && !IsFiller(i)).ToList();
                if (names.Count == 0) return Cmd(CommandKind.ListWindows);
                return Cmd(CommandKind.FindWindow) with { Window = new WindowSelector(NameText: Join(names)) };
            }

            if (Range(0, N).Any(i => OpenWord(i) && IsControlAlias(i))
                || (words.Overlaps(Set("value", "say", "says", "show", "shows", "contain", "contains")) && AnyOpenLowerTypeWord() && !words.Overlaps(ControlNouns)))
            {
                Consume(f);
                return ParseInspect();
            }

            if (words.Overlaps(ControlNouns) || (AnyOpenLowerTypeWord() && (w0 is "is" or "are" || words.Contains("there"))))
            {
                Consume(f);
                return ParseControlListing();
            }

            if (words.Overlaps(WindowListNouns)) return Cmd(CommandKind.ListWindows);
            if (words.Overlaps(ScreenWords)) return Cmd(CommandKind.ListDisplays);
            return null;
        }

        private RemoteCommand? TryNounCommand()
        {
            if (AnyOpen(ControlNouns) || AnyOpenLowerTypeWord(pluralOnly: true)) return ParseControlListing();
            if (AnyOpen(WindowListNouns)) return Cmd(CommandKind.ListWindows);
            if (AnyOpen(ScreenWords)) return Cmd(CommandKind.ListDisplays);
            if (AnyOpen("where", "mouse", "cursor", "focus")) return Cmd(CommandKind.Where);
            return null;
        }

        private RemoteCommand UnknownWithSuggestion()
        {
            string? best = null;
            var bestDistance = int.MaxValue;
            foreach (var w in t.Where(x => !x.IsQuoted).Select(x => x.Word).Where(w => w.Length >= 3))
            {
                foreach (var candidate in SuggestionExamples.Keys)
                {
                    var d = Levenshtein(w, candidate);
                    if (d < bestDistance && d <= (candidate.Length <= 4 ? 1 : 2)) { bestDistance = d; best = candidate; }
                }
            }
            return best is null
                ? Unknown($"I did not understand \"{raw.Trim()}\". {GeneralHint}")
                : Unknown($"I did not understand \"{raw.Trim()}\". Did you mean \"{best}\"? For example: {SuggestionExamples[best]}");
        }

        private static int Levenshtein(string a, string b)
        {
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (var j = 0; j <= b.Length; j++) prev[j] = j;
            for (var i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (var j = 1; j <= b.Length; j++)
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                (prev, cur) = (cur, prev);
            }
            return prev[b.Length];
        }

        // ------------------------------------------------ window selector

        private WindowSelector? ExtractWindow(int from, int to, WindowMode mode)
        {
            // 1. alias or native handle
            foreach (var i in Range(from, to))
            {
                if (!OpenWord(i)) continue;
                var w = t[i].Word;
                WindowSelector? sel = null;
                var m = WindowAliasRegex().Match(w);
                if (m.Success && long.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    sel = n >= 1000 ? new WindowSelector(Handle: n) : new WindowSelector(Alias: "w" + n.ToString(CultureInfo.InvariantCulture));
                else if (!mode.HasFlag(WindowMode.NoBareHandles) && long.TryParse(w, NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) && h >= 1000)
                    sel = new WindowSelector(Handle: h);
                if (sel is null) continue;
                Consume(i);
                ConsumeLeadIn(i, from);
                var hint = CollectHint(i + 1, to);
                return hint is null ? sel : sel with { Hint = hint };
            }

            // 2. the word "window": words after it, else words before it
            foreach (var i in Range(from, to))
            {
                if (!IsWord(i, WindowNouns)) continue;
                var j = i + 1;
                var skipped = new List<int>();
                while (j < to && OpenWord(j) && (Articles.Contains(t[j].Word) || t[j].Word is "named" or "called" or "titled")) skipped.Add(j++);
                // "window 2" is the alias w2.
                if (j < to && OpenWord(j) && IsInt(t[j].Word, out var number) && number is > 0 and < 1000)
                {
                    Consume(i);
                    skipped.ForEach(Consume);
                    Consume(j);
                    ConsumeLeadIn(i, from);
                    var aliasHint = CollectHint(j + 1, to);
                    return new WindowSelector(Alias: "w" + number.ToString(CultureInfo.InvariantCulture)) { Hint = aliasHint };
                }
                var (after, afterEnd) = CollectWindowName(j, to);
                j = afterEnd;
                if (after.Count > 0)
                {
                    Consume(i);
                    skipped.ForEach(Consume);
                    after.ForEach(Consume);
                    ConsumeLeadIn(i, from);
                    var hint = CollectHint(j, to);
                    return new WindowSelector(NameText: Join(after)) { Hint = hint };
                }

                var before = new List<int>();
                var k = i - 1;
                while (k >= from && Open(k) && !IsNameStop(k) && !(IsFiller(k) && !t[k].Capitalized)) before.Insert(0, k--);
                Consume(i);
                if (before.Count > 0)
                {
                    before.ForEach(Consume);
                    ConsumeLeadIn(before[0], from);
                    var hint = CollectHint(i + 1, to);
                    return new WindowSelector(NameText: Join(before)) { Hint = hint };
                }
                // "the current window" / "this window": consume the describing word too.
                if (k >= from && IsWord(k, CurrentWords)) Consume(k);
                ConsumeLeadIn(i, from);
                windowMentioned = true;
                return null;
            }

            if (!mode.HasFlag(WindowMode.Prepositions)) return null;

            // 3. "in/on/of X" when X is not a control description
            (int Prep, List<int> Skipped, List<int> Words, int End)? best = null;
            foreach (var i in Range(from, to))
            {
                if (!IsWord(i, WindowPrepositions)) continue;
                if (mode.HasFlag(WindowMode.SkipAfterVerb) && verb is not null && i > VerbIndex && OnlyFillersBetween(VerbIndex, i)) continue;
                var j = i + 1;
                var skipped = new List<int>();
                while (j < to && OpenWord(j) && Articles.Contains(t[j].Word)) skipped.Add(j++);
                var (words, wordsEnd) = CollectWindowName(j, to);
                j = wordsEnd;
                if (words.Count == 0) continue;
                if (j < to && Open(j) && !t[j].IsQuoted && (IsLowerTypeWord(j) || IsControlKeyword(j) || IsControlAlias(j))) continue;
                if (words.Any(x => !t[x].IsQuoted && !t[x].Capitalized && IsLowerTypeWord(x))) continue;
                if (words.Any(x => IsWord(x, Directions) || IsWord(x, CurrentWords))) continue;
                best = (i, skipped, words, j);
            }
            if (best is not { } b) return null;
            Consume(b.Prep);
            b.Skipped.ForEach(Consume);
            b.Words.ForEach(Consume);
            if (IsWord(b.End, WindowNouns)) Consume(b.End);
            return new WindowSelector(NameText: Join(b.Words));
        }

        private void ConsumeLeadIn(int i, int from)
        {
            var j = i - 1;
            var sawNoun = false;
            while (j >= from && OpenWord(j))
            {
                var w = t[j].Word;
                if (!sawNoun && WindowNouns.Contains(w)) { Consume(j); sawNoun = true; j--; continue; }
                if (Articles.Contains(w)) { j--; continue; }
                if (WindowPrepositions.Contains(w)) Consume(j);
                break;
            }
        }

        private string? CollectHint(int start, int to)
        {
            var i = start;
            var skipped = new List<int>();
            while (i < to && OpenWord(i) && (WindowPrepositions.Contains(t[i].Word) || Articles.Contains(t[i].Word))) skipped.Add(i++);
            var words = new List<int>();
            while (i < to && OpenWord(i) && !IsNameStop(i)) words.Add(i++);
            if (words.Count == 0) return null;
            skipped.ForEach(Consume);
            words.ForEach(Consume);
            return Join(words);
        }

        // ------------------------------------------------ control selector

        private ControlSelector? ExtractControl(int from, int to, HashSet<string> noise, bool multiwordAfterKeyword = false)
        {
            foreach (var i in Range(from, to))
            {
                if (!OpenWord(i) || !IsControlAlias(i)) continue;
                Consume(i);
                return new ControlSelector(Alias: t[i].Word);
            }

            foreach (var i in Range(from, to))
            {
                if (!IsControlKeyword(i)) continue;
                var j = i + 1;
                if (t[i].Word is "control" or "element" && j < to && OpenWord(j) && t[j].Word == "id") j++;
                while (j < to && OpenWord(j) && (Articles.Contains(t[j].Word) || t[j].Word is "named" or "called" or "with")) j++;
                if (!(j < to && Open(j))) continue;
                for (var k = i; k < j; k++) Consume(k);
                if (multiwordAfterKeyword && !t[j].IsQuoted)
                {
                    var words = new List<int>();
                    while (j < to && Open(j) && (t[j].IsQuoted || !(Stops.Contains(t[j].Word) || WindowPrepositions.Contains(t[j].Word) || IsControlAlias(j)))) words.Add(j++);
                    words.ForEach(Consume);
                    return new ControlSelector(NameText: Join(words));
                }
                Consume(j);
                if (t[j].IsQuoted) return new ControlSelector(NameText: t[j].Quoted);
                if (IsControlAlias(j)) return new ControlSelector(Alias: t[j].Word);
                return new ControlSelector(IdText: t[j].Display);
            }

            var list = Range(from, to).Where(i => Open(i) && (t[i].IsQuoted || !noise.Contains(t[i].Word))).ToList();
            int? ordinal = null;
            for (var x = 0; x < list.Count; x++)
            {
                var i = list[x];
                if (t[i].IsQuoted) continue;
                if (OrdinalWords.TryGetValue(t[i].Word, out var o) || TryRegexOrdinal(t[i].Word, out o))
                {
                    ordinal = o;
                    Consume(i);
                    list.RemoveAt(x);
                    break;
                }
                if (t[i].Word is "number" or "no" && x + 1 < list.Count && IsInt(t[list[x + 1]].Word, out o))
                {
                    ordinal = o;
                    Consume(i);
                    Consume(list[x + 1]);
                    list.RemoveRange(x, 2);
                    break;
                }
            }

            var q = list.FindIndex(i => t[i].IsQuoted);
            if (q >= 0)
            {
                // The type word may sit before the quote with "called"/"named"/articles between, or right after it.
                string? quotedType = null;
                for (var b = q - 1; b >= 0; b--)
                {
                    var bi = list[b];
                    if (IsTypeWord(bi)) { quotedType = TypeWords[t[bi].Word]; break; }
                    if (t[bi].IsQuoted || !(Filler.Contains(t[bi].Word) || EdgeNoise.Contains(t[bi].Word))) break;
                }
                if (quotedType is null && q + 1 < list.Count && IsLowerTypeWord(list[q + 1])) quotedType = TypeWords[t[list[q + 1]].Word];
                list.ForEach(Consume);
                return new ControlSelector(ControlType: quotedType, NameText: t[list[q]].Quoted) { Ordinal = ordinal };
            }

            TrimEdges(list);
            if (list.Count == 0)
                return null;

            // A trailing lowercase type word wins ("the Page 2 link", "the Text link"); a leading lowercase one names the
            // type when no trailing one does ("link Next Page"). A capitalized leading type word is part of the name.
            string? type = null;
            string? name;
            var first = list[0];
            var leading = IsLowerTypeWord(first);
            var trailing = list.Count > 1 && IsLowerTypeWord(list[^1]);
            if (trailing && !(leading && list.Count > 2))
            {
                type = TypeWords[t[list[^1]].Word];
                var rest = list.Take(list.Count - 1).ToList();
                TrimEdges(rest);
                name = rest.Count > 0 ? Join(rest) : null;
            }
            else if (leading)
            {
                type = TypeWords[t[first].Word];
                var rest = list.Skip(1).ToList();
                TrimEdges(rest);
                name = rest.Count > 0 ? Join(rest) : null;
            }
            else
            {
                name = Join(list);
            }
            list.ForEach(Consume);
            return new ControlSelector(ControlType: type, NameText: name) { Ordinal = ordinal };
        }

        /// <summary>
        /// Drops filler at the edges of a name, but never a capitalized word, and never a trailing particle that has no
        /// object (end of the command, or followed by a type word) or follows a capitalized word: "Log in", "Remember me".
        /// </summary>
        private void TrimEdges(List<int> list)
        {
            bool Edge(int i) => !t[i].IsQuoted && !t[i].Capitalized && (Filler.Contains(t[i].Word) || EdgeNoise.Contains(t[i].Word));
            while (list.Count > 0 && Edge(list[0])) list.RemoveAt(0);
            while (list.Count > 0 && Edge(list[^1]))
            {
                var last = list[^1];
                if (list.Count >= 2 && Particles.Contains(t[last].Word))
                {
                    var noObject = last == N - 1 || (last + 1 < N && IsLowerTypeWord(last + 1));
                    if (t[list[^2]].Capitalized || noObject) break;
                }
                list.RemoveAt(list.Count - 1);
            }
        }

        private static bool TryRegexOrdinal(string w, out int n)
        {
            n = 0;
            var m = OrdinalRegex().Match(w);
            if (!m.Success) return false;
            return int.TryParse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out n);
        }

        // ------------------------------------------------ points, screens

        private int? ExtractDisplay(int from, int to)
        {
            foreach (var i in Range(from, to))
            {
                if (!IsWord(i, ScreenWords)) continue;
                if (OpenWord(i + 1) && i + 1 < to && (IsInt(t[i + 1].Word, out var n) || NumberWords.TryGetValue(t[i + 1].Word, out n)) && n is >= 1 and < 100)
                {
                    Consume(i);
                    Consume(i + 1);
                    if (IsWord(i - 1, WindowPrepositions)) Consume(i - 1);
                    return n;
                }
                if (OpenWord(i - 1) && (OrdinalWords.TryGetValue(t[i - 1].Word, out n) || TryRegexOrdinal(t[i - 1].Word, out n)))
                {
                    Consume(i);
                    Consume(i - 1);
                    return n;
                }
            }
            return null;
        }

        private ScreenPoint? ExtractPoint(int from, int to)
        {
            foreach (var i in Range(from, to))
            {
                if (!OpenWord(i)) continue;
                var m = PointRegex().Match(t[i].Word);
                if (m.Success)
                {
                    Consume(i);
                    if (IsWord(i - 1, Set("at", "to"))) Consume(i - 1);
                    return new ScreenPoint(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
                }
                if (IsInt(t[i].Word, out var x) && i + 1 < to && OpenWord(i + 1) && IsInt(t[i + 1].Word, out var y))
                {
                    Consume(i);
                    Consume(i + 1);
                    if (IsWord(i - 1, Set("at", "to"))) Consume(i - 1);
                    return new ScreenPoint(x, y);
                }
            }
            return null;
        }

        private static TimeSpan ToDuration(double value, string? unit) => unit switch
        {
            null or "" => TimeSpan.FromSeconds(value),
            var u when u.StartsWith("ms", StringComparison.Ordinal) || u.StartsWith("milli", StringComparison.Ordinal) => TimeSpan.FromMilliseconds(value),
            var u when u.StartsWith('m') => TimeSpan.FromMinutes(value),
            _ => TimeSpan.FromSeconds(value),
        };

        /// <summary>Reads "2", "500 ms", "1.5 seconds", "3s" starting at token <paramref name="i"/>.</summary>
        private TimeSpan? ReadDuration(int i)
        {
            if (!OpenWord(i)) return null;
            var m = DurationRegex().Match(t[i].Word);
            if (!m.Success) return null;
            var value = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var unit = m.Groups[2].Success ? m.Groups[2].Value : null;
            Consume(i);
            if (unit is null && OpenWord(i + 1) && UnitRegex().IsMatch(t[i + 1].Word))
            {
                unit = t[i + 1].Word;
                Consume(i + 1);
            }
            return ToDuration(value, unit);
        }

        // ------------------------------------------------ commands

        private RemoteCommand ParseScreenshot()
        {
            var include = AnyOpen(IncludeControlWords);
            var verbose = false;
            if (include)
            {
                verbose = AnyOpen("all", "every", "everything");
                ConsumeAll(IncludeControlWords);
                ConsumeAll(IncludeControlExtras);
            }

            foreach (var i in Range(0, N))
            {
                if (!IsWord(i, RegionWords)) continue;
                Consume(i);
                var rect = ReadRegion(i + 1);
                return rect is null
                    ? Unknown("A region needs left,top WIDTHxHEIGHT or left top right bottom, for example: screenshot region 100,100 800x600")
                    : Cmd(CommandKind.Screenshot) with { Target = ScreenshotTarget.Region, Region = rect };
            }

            if (!include && (AnyOpen("everything", "desktop") || (AnyOpen("all", "every", "both", "entire", "whole") && AnyOpen(ScreenNouns))))
                return Cmd(CommandKind.Screenshot) with { Target = ScreenshotTarget.AllDisplays };

            windowNameToEnd = true;
            if (ExtractDisplay(0, N) is { } display)
                return Cmd(CommandKind.Screenshot) with { Target = ScreenshotTarget.Display, DisplayIndex = display };

            // A screen word followed by something that is not a number ("screen foo") is an error, not screen 1.
            foreach (var i in Range(0, N))
            {
                if (!IsWord(i, ScreenWords) || !OpenWord(i + 1)) continue;
                var next = t[i + 1].Word;
                if (Filler.Contains(next) || Stops.Contains(next) || WindowPrepositions.Contains(next)) continue;
                return Unknown($"'{t[i + 1].Display}' is not a screen number. Try: screens, then screenshot screen 2.");
            }

            // "screenshot 2" is screen 2 (a window is "screenshot window 2" or "screenshot w2").
            if (!AnyOpen(WindowNouns) && !include)
            {
                foreach (var i in Range(AfterVerb, N))
                {
                    if (!OpenWord(i) || !IsInt(t[i].Word, out var bare) || bare is < 1 or >= 100) continue;
                    Consume(i);
                    return Cmd(CommandKind.Screenshot) with { Target = ScreenshotTarget.Display, DisplayIndex = bare };
                }
            }

            if (!include && (AnyOpen(ScreenWords) || AnyOpen("primary", "main")))
                return Cmd(CommandKind.Screenshot) with { Target = ScreenshotTarget.Primary };

            var win = ExtractWindow(0, N, Read);
            if (win is null)
            {
                var rest = Range(0, N).Where(i => Open(i) && !IsFiller(i) && !(OpenWord(i) && ShotNoise.Contains(t[i].Word))).ToList();
                if (rest.Count > 0)
                {
                    rest.ForEach(Consume);
                    win = new WindowSelector(NameText: Join(rest));
                }
            }
            if (win is not null || include || windowMentioned)
                return Cmd(CommandKind.Screenshot) with { Target = ScreenshotTarget.Window, Window = win, IncludeControls = include, Verbose = verbose };
            return Cmd(CommandKind.Screenshot) with { Target = ScreenshotTarget.Primary };
        }

        private ScreenRect? ReadRegion(int start)
        {
            var nums = new List<int>();
            var sizeForm = false;
            for (var j = start; j < N && OpenWord(j); j++)
            {
                var w = t[j].Word;
                var size = SizeRegex().Match(w);
                if (size.Success)
                {
                    nums.Add(int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture));
                    nums.Add(int.Parse(size.Groups[2].Value, CultureInfo.InvariantCulture));
                    sizeForm = true;
                    Consume(j);
                    continue;
                }
                var parts = w.Split(',', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0 || !parts.All(p => IsInt(p, out _))) break;
                nums.AddRange(parts.Select(p => int.Parse(p, CultureInfo.InvariantCulture)));
                Consume(j);
            }
            if (nums.Count != 4) return null;
            var rect = sizeForm ? new ScreenRect(nums[0], nums[1], nums[2], nums[3]) : ScreenRect.FromEdges(nums[0], nums[1], nums[2], nums[3]);
            return rect.IsEmpty ? null : rect;
        }

        private RemoteCommand ParseClick(string w)
        {
            var (button, clicks) = w switch
            {
                "doubleclick" or "dblclick" or "open" or "doubletap" => (MouseButton.Left, 2),
                "rightclick" or "contextmenu" => (MouseButton.Right, 1),
                "middleclick" => (MouseButton.Middle, 1),
                _ => (MouseButton.Left, 1),
            };
            if (w is "press" or "hit" or "push" && StartsWithKey()) return ParseKeySequence();

            string? modifiers;
            try { modifiers = NormalizedClickModifiers(); }
            catch (ArgumentException ex) { return Unknown($"{ex.Message} Try: shift click c3."); }

            var display = ExtractDisplay(AfterVerb, N);
            if (ExtractPoint(AfterVerb, N) is { } point)
                return Cmd(CommandKind.Click) with { Point = point, DisplayIndex = display, PointIsDisplayRelative = display is not null, Button = button, Clicks = clicks, Modifiers = modifiers };

            var win = ExtractWindow(0, N, Act);
            var control = ExtractControl(0, N, ClickNoise);
            if (control is null)
                return Unknown("Click what? Try: click c12, click link Next Page in chrome, or click at 100,200.");
            return Cmd(CommandKind.Click) with { Window = win, Control = control, Button = button, Clicks = clicks, Modifiers = modifiers };
        }

        /// <summary>
        /// After press/hit/push: the words are keys when the first one is a key or modifier, unless they describe a
        /// control ("press the Home button": ends with a type word, or names an alias or control id).
        /// </summary>
        private bool StartsWithKey()
        {
            var words = Range(AfterVerb, N).Where(i => Open(i) && (t[i].IsQuoted || !KeyNoise.Contains(t[i].Word))).ToList();
            if (words.Count == 0 || t[words[0]].IsQuoted) return false;
            if (words.Any(i => OpenWord(i) && (IsControlAlias(i) || IsControlKeyword(i)))) return false;
            if (words.Count > 1 && IsLowerTypeWord(words[^1])) return false;
            var first = t[words[0]].Word;
            return KeyChord.IsKeyOrModifier(first) || (first.Contains('+') && KeyChord.IsModifier(first.Split('+')[0]));
        }

        /// <summary>
        /// Key chords with optional repeats and sequences: "tab 3 times", "enter twice", "down x5",
        /// "ctrl+c then ctrl+v", "ctrl+c, ctrl+v", "ctrl shift t".
        /// </summary>
        private RemoteCommand ParseKeySequence()
        {
            var idx = Range(AfterVerb, N).Where(i => Open(i) && (t[i].IsQuoted || !KeyNoise.Contains(t[i].Word))).ToList();
            if (idx.Count == 0) return Unknown("Press which keys? Try: press ctrl+s, keys alt+f4, or press enter.");
            var chords = new List<string>();
            var pending = new List<string>();
            var repeat = 1;
            void Flush()
            {
                if (pending.Count == 0) return;
                chords.Add(KeyChord.Normalize(string.Join("+", pending)));
                pending.Clear();
            }
            try
            {
                for (var k = 0; k < idx.Count; k++)
                {
                    var tok = t[idx[k]];
                    var w = tok.IsQuoted ? tok.Quoted! : tok.Word;
                    if (!tok.IsQuoted)
                    {
                        if (w is "then" or ",") { Flush(); continue; }
                        if (w == "and") { if (pending.All(KeyChord.IsModifier) && pending.Count > 0) continue; Flush(); continue; }
                        if (w == "twice") { repeat = 2; continue; }
                        if (w == "thrice") { repeat = 3; continue; }
                        if (w is "once" or "times" or "time") continue;
                        var rx = RepeatRegex().Match(w);
                        if (rx.Success) { repeat = int.Parse(rx.Groups[1].Value, CultureInfo.InvariantCulture); continue; }
                        if (IsInt(w, out var n) && k + 1 < idx.Count && t[idx[k + 1]].Word is "times" or "time") { repeat = n; k++; continue; }
                        if (NumberWords.TryGetValue(w, out n) && k + 1 < idx.Count && t[idx[k + 1]].Word is "times" or "time") { repeat = n; k++; continue; }
                    }
                    if (w.Length > 1 && w.Contains('+')) { Flush(); chords.Add(KeyChord.Normalize(w)); }
                    else if (KeyChord.IsModifier(w)) pending.Add(w);
                    else { pending.Add(w); Flush(); }
                    if (!tok.IsQuoted && tok.Text.Length > 1 && tok.Text.EndsWith(',')) Flush();
                }
                Flush();
            }
            catch (ArgumentException ex)
            {
                return Unknown($"{ex.Message} Try: press ctrl+s, press tab 3 times, or press ctrl+c then ctrl+v.");
            }
            if (chords.Count == 0) return Unknown("Press which keys? Try: press ctrl+s, keys alt+f4, or press enter.");
            if (repeat < 1) return Unknown("A key repeat count must be at least 1. Try: press tab 3 times.");
            idx.ForEach(Consume);
            return Cmd(CommandKind.PressKeys) with { Keys = chords[0], KeySequence = chords, Repeat = repeat };
        }

        private RemoteCommand ParseKeys() => ParseKeySequence();

        /// <summary>"guard off", "guard on", "guard 500 ms", "guard 1 s", "guard" (show the setting). A bare number is milliseconds.</summary>
        private RemoteCommand ParseGuard()
        {
            foreach (var i in Range(AfterVerb, N))
            {
                if (!OpenWord(i)) continue;
                var w = t[i].Word;
                if (w is "off" or "disable" or "disabled" or "no" or "none" or "0") return Cmd(CommandKind.SetGuard) with { Value = "off", Delay = TimeSpan.Zero };
                if (w is "on" or "enable" or "enabled" or "default" or "yes") return Cmd(CommandKind.SetGuard) with { Value = "on" };
                var m = DurationRegex().Match(w);
                if (!m.Success) continue;
                var value = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var unit = m.Groups[2].Success ? m.Groups[2].Value : OpenWord(i + 1) && UnitRegex().IsMatch(t[i + 1].Word) ? t[i + 1].Word : "ms";
                var delay = ToDuration(value, unit);
                return Cmd(CommandKind.SetGuard) with { Value = delay == TimeSpan.Zero ? "off" : "set", Delay = delay };
            }
            return Range(AfterVerb, N).Any(i => Open(i) && !IsFiller(i))
                ? Unknown("Try: guard off, guard on, or guard 500 ms.")
                : Cmd(CommandKind.SetGuard) with { Value = "status" };
        }

        /// <summary>"drag c3 to c4", "drag c3 to 100,200", "drag from 10,10 to 50,50", "drag the Report item to the Archive folder in w3".</summary>
        private RemoteCommand ParseDrag()
        {
            var win = ExtractWindow(0, N, Act);
            var toIndex = Range(AfterVerb, N).LastOrDefault(i => IsWord(i, Set("to", "onto", "into", "over")), -1);
            if (toIndex < 0) return Unknown("Drag from where to where? Try: drag c3 to c4, or drag from 10,10 to 50,50.");
            Consume(toIndex);
            ConsumeAll(Set("from"), AfterVerb);
            var display = ExtractDisplay(AfterVerb, N);
            var fromPoint = ExtractPoint(AfterVerb, toIndex);
            var fromControl = fromPoint is null ? ExtractControl(AfterVerb, toIndex, ClickNoise) : null;
            var toPoint = ExtractPoint(toIndex + 1, N);
            var toControl = toPoint is null ? ExtractControl(toIndex + 1, N, ClickNoise) : null;
            if (fromPoint is null && fromControl is null) return Unknown("Drag what? Try: drag c3 to c4.");
            if (toPoint is null && toControl is null) return Unknown("Drag it where? Try: drag c3 to c4, or drag c3 to 100,200.");
            return Cmd(CommandKind.Drag) with
            {
                Window = win, Point = fromPoint, Control = fromControl, TargetPoint = toPoint, TargetControl = toControl,
                DisplayIndex = display, PointIsDisplayRelative = display is not null,
            };
        }

        private RemoteCommand ParseInvoke(string word)
        {
            var value = word switch { "activate" => "invoke", "tick" => "check", "untick" => "uncheck", _ => word };
            var win = ExtractWindow(0, N, Act);
            var control = ExtractControl(0, N, []);
            if (control is null) return Unknown($"{char.ToUpperInvariant(value[0])}{value[1..]} which control? Try: {value} c12.");
            return Cmd(CommandKind.Invoke) with { Window = win, Control = control, Value = value };
        }

        private RemoteCommand ParseFocus()
        {
            // "switch to", "bring up", "go over to": only right after the verb, so "Welcome to Chrome" keeps its "to".
            for (var i = AfterVerb; i < N && OpenWord(i) && (Set("to", "up", "over").Contains(t[i].Word) || Articles.Contains(t[i].Word)); i++)
                if (!Articles.Contains(t[i].Word)) Consume(i);
            ConsumeAll(Set("front", "foreground", "top", "forward", "focus"), AfterVerb);
            windowNameToEnd = true;
            var win = ExtractWindow(0, N, WindowMode.Explicit);
            if (win is null)
            {
                var rest = Range(AfterVerb, N).Where(i => Open(i) && !IsFiller(i)).ToList();
                if (rest.Count > 0)
                {
                    rest.ForEach(Consume);
                    win = new WindowSelector(NameText: Join(rest));
                }
            }
            return win is null
                ? Unknown("Focus which window? Try: focus w3, or switch to chrome.")
                : Cmd(CommandKind.FocusWindow) with { Window = win };
        }

        private RemoteCommand ParseClear()
        {
            var win = ExtractWindow(0, N, Act);
            var control = ExtractControl(0, N, SetNoise);
            return control is null
                ? Unknown("Clear which control? Try: clear c12.")
                : Cmd(CommandKind.SetValue) with { Window = win, Control = control, Value = "" };
        }

        private bool HasContent(int a, int b) =>
            Range(a, b).Any(i => Open(i) && (t[i].IsQuoted || (!Filler.Contains(t[i].Word) && !SetNoise.Contains(t[i].Word))));

        private RemoteCommand ParseSetFamily()
        {
            var vi = VerbIndex;
            int? marker = null;
            bool Usable(int i) => i < N - 1 && HasContent(vi + 1, i);
            var strong = Range(vi + 1, N).Where(i => IsWord(i, Set("to", "=", ":")) && Usable(i)).ToList();
            // A marker right after a control description wins ("the Ship to field to Boston", "c12 to go to sleep");
            // then the first = or :; then the last "to" that leaves a control before it.
            marker = strong.Cast<int?>().FirstOrDefault(i => i > 0 && (IsLowerTypeWord(i!.Value - 1) || IsControlAlias(i.Value - 1) || t[i.Value - 1].IsQuoted))
                     ?? strong.Cast<int?>().FirstOrDefault(i => t[i!.Value].Word is "=" or ":")
                     ?? strong.Cast<int?>().LastOrDefault();
            if (marker is null)
            {
                foreach (var cls in new[] { Set("with", "as", "say", "says"), Set("value") })
                {
                    marker = Range(vi + 1, N).Cast<int?>().FirstOrDefault(i => IsWord(i!.Value, cls) && Usable(i.Value));
                    if (marker is not null) break;
                }
            }

            string value;
            int regionEnd;
            if (marker is { } mk)
            {
                value = ValueText(mk + 1, N);
                for (var i = mk; i < N; i++) Consume(i);
                regionEnd = mk;
            }
            else if (N > 0 && t[N - 1].IsQuoted && HasContent(vi + 1, N - 1))
            {
                value = t[N - 1].Quoted!;
                Consume(N - 1);
                regionEnd = N - 1;
            }
            else
            {
                return Unknown("I could not tell which value to set. Try: set c12 to \"text\", or set the Email box in chrome to bob@example.com.");
            }

            var win = ExtractWindow(0, regionEnd, Act);
            var control = ExtractControl(0, regionEnd, SetNoise);
            return control is null
                ? Unknown("Set which control? Try: set c12 to \"text\", or set the Email text box in w3 to bob@example.com.")
                : Cmd(CommandKind.SetValue) with { Window = win, Control = control, Value = value };
        }

        private RemoteCommand ParseTypeFamily(string w)
        {
            var vi = VerbIndex;
            var explicitWindow = ExtractWindow(0, N, WindowMode.NoBareHandles);
            var start = vi + 1;
            while (start < N && OpenWord(start) && (t[start].Word == ":" || (w == "enter" && t[start].Word == "text"))) Consume(start++);

            for (var i = N - 1; i > start; i--)
            {
                if (!IsWord(i, Set("into", "in", "inside", "onto", "to"))) continue;
                var after = Range(i + 1, N).Where(Open).ToList();
                if (after.Count == 0) continue;
                var strong = t[i].Word is "into" or "onto" or "inside"
                             || after.Any(x => t[x].IsQuoted || IsControlAlias(x) || IsControlKeyword(x) || IsLowerTypeWord(x));
                if (!strong && w != "put") continue;
                if (!Range(start, i).Any(Open)) continue;
                var value = ValueText(start, i);
                for (var k = start; k <= i; k++) Consume(k);
                var win = explicitWindow ?? ExtractWindow(i + 1, N, Read);
                var control = ExtractControl(i + 1, N, SetNoise);
                if (control is null) break;
                return Cmd(CommandKind.SetValue) with { Window = win, Control = control, Value = value };
            }

            if (w == "put") return Unknown("Put it where? Try: put \"hello\" in c12.");
            var end = start;
            while (end < N && Open(end)) end++;
            if (end == start) return Unknown("Type what? Try: type \"hello\", or type \"hello\" into the Search field in chrome.");
            var text = ValueText(start, end);
            for (var k = start; k < end; k++) Consume(k);
            return Cmd(CommandKind.TypeText) with { Value = text, Window = explicitWindow };
        }

        private RemoteCommand ParseMove()
        {
            ConsumeAll(Set("mouse", "cursor", "pointer", "over"), AfterVerb);
            var display = ExtractDisplay(AfterVerb, N);
            if (ExtractPoint(AfterVerb, N) is { } point)
                return Cmd(CommandKind.MoveMouse) with { Point = point, DisplayIndex = display, PointIsDisplayRelative = display is not null };
            var win = ExtractWindow(0, N, Act);
            var control = ExtractControl(0, N, MoveNoise);
            return control is null
                ? Unknown("Move the mouse where? Try: move mouse to 100,200, or hover c12.")
                : Cmd(CommandKind.MoveMouse) with { Window = win, Control = control };
        }

        private RemoteCommand ParseScroll(string w)
        {
            var direction = w switch { "pagedown" => "down", "pageup" => "up", _ => null };
            foreach (var i in Range(AfterVerb, N))
            {
                if (!IsWord(i, Directions)) continue;
                direction ??= t[i].Word;
                Consume(i);
                break;
            }
            var display = ExtractDisplay(AfterVerb, N);
            var point = ExtractPoint(AfterVerb, N);
            int? amount = null;
            foreach (var i in Range(AfterVerb, N))
            {
                if (!OpenWord(i) || !IsInt(t[i].Word, out var n) || n <= 0 || IsWord(i - 1, WindowPrepositions)) continue;
                amount = n;
                Consume(i);
                break;
            }
            ConsumeAll(ScrollNoise, AfterVerb);
            var win = ExtractWindow(0, N, Read);
            var control = ExtractControl(0, N, ScrollNoise);
            var notches = amount ?? 3;
            var (dx, dy) = (direction ?? "down") switch
            {
                "up" => (0, notches),
                "left" => (-notches, 0),
                "right" => (notches, 0),
                _ => (0, -notches),
            };
            return Cmd(CommandKind.Scroll) with
            {
                Window = win, Control = control, Point = point, DisplayIndex = display,
                PointIsDisplayRelative = point is not null && display is not null, ScrollX = dx, ScrollY = dy,
            };
        }

        private RemoteCommand ParseWait()
        {
            var vi = VerbIndex;
            var connector = OpenWord(vi + 1) && t[vi + 1].Word is "for" or "until" or "till";
            if (!connector) return Cmd(CommandKind.Wait) with { Delay = PlainDelay(vi + 1) };
            if (OpenWord(vi + 2) && DurationRegex().IsMatch(t[vi + 2].Word))
            {
                // "wait for 2 seconds" is a plain pause.
                Consume(vi + 1);
                return Cmd(CommandKind.Wait) with { Delay = PlainDelay(vi + 2) };
            }
            Consume(vi + 1);
            return ParseWaitFor();
        }

        private TimeSpan PlainDelay(int from)
        {
            foreach (var i in Range(from, N))
                if (ReadDuration(i) is { } d) return d;
            return TimeSpan.FromSeconds(1);
        }

        private RemoteCommand ParseWaitFor()
        {
            // Timeout: "up to 10 seconds", "for 10 seconds", "timeout 10", "max 2 minutes", "within 5 s".
            TimeSpan timeout = TimeSpan.FromSeconds(30);
            foreach (var i in Range(AfterVerb, N))
            {
                if (!OpenWord(i)) continue;
                var w = t[i].Word;
                var at = w == "up" && IsWord(i + 1, Set("to")) ? i + 2 : WaitTimeoutMarkers.Contains(w) ? i + 1 : w is "at" && IsWord(i + 1, Set("most")) ? i + 2 : -1;
                if (at < 0) continue;
                if (ReadDuration(at) is not { } d) continue;
                for (var k = i; k < at; k++) Consume(k);
                timeout = d;
                break;
            }

            // Condition and value.
            WaitTarget? condition = null;
            string? value = null;
            // "shows up" means appears.
            foreach (var i in Range(AfterVerb, N))
            {
                if (IsWord(i, Set("shows", "show", "turns", "pops")) && IsWord(i + 1, Set("up")))
                {
                    Consume(i);
                    Consume(i + 1);
                    condition = WaitTarget.ControlAppears;
                    break;
                }
            }
            foreach (var i in Range(AfterVerb, condition is null ? N : 0))
            {
                if (!OpenWord(i)) continue;
                var w = t[i].Word;
                if (WaitValueMarkers.Contains(w) || (w == "is" && OpenWord(i + 1) && !IsCondition(t[i + 1].Word) && !Filler.Contains(t[i + 1].Word) && Range(i + 1, N).Any(Open) && LooksLikeControlTarget()))
                {
                    if (w == "shows" && !Range(i + 1, N).Any(Open)) continue;
                    var end = i + 1;
                    while (end < N && Open(end)) end++;
                    if (end == i + 1) continue;
                    value = ValueText(i + 1, end);
                    for (var k = i; k < end; k++) Consume(k);
                    condition = WaitTarget.ControlValueEquals;
                    // "to be enabled", "is gone", "be visible": a single condition word is the condition, not a value.
                    if (end - i == 2 && OpenWordIgnoringUse(i + 1) is { } single && IsCondition(single) && !t[i + 1].IsQuoted)
                    {
                        condition = ConditionFor(single);
                        value = null;
                    }
                    break;
                }
            }
            if (condition is null)
            {
                foreach (var i in Range(AfterVerb, N))
                {
                    if (!OpenWord(i) || !IsCondition(t[i].Word)) continue;
                    condition = ConditionFor(t[i].Word);
                    Consume(i);
                    break;
                }
            }
            ConsumeAll(WaitNoise, AfterVerb);

            var controlish = Range(0, N).Any(i => OpenWord(i) && (IsControlAlias(i) || IsControlKeyword(i)
                                                                   || (IsLowerTypeWord(i) && TypeWords[t[i].Word] != "window")))
                             || condition is WaitTarget.ControlValueEquals or WaitTarget.ControlValueChanges or WaitTarget.ControlEnabled;
            if (controlish)
            {
                var win = ExtractWindow(0, N, Read);
                var control = ExtractControl(0, N, WaitNoise);
                if (control is null) return Unknown("Wait for which control? Try: wait until c12 says Done, or wait for the Save button in chrome.");
                return Cmd(CommandKind.WaitFor) with
                {
                    Window = win, Control = control, Value = value, Delay = timeout,
                    Wait = new WaitCondition(condition ?? WaitTarget.ControlAppears),
                };
            }

            windowNameToEnd = true;
            var window = ExtractWindow(0, N, WindowMode.Explicit);
            if (window is null)
            {
                var rest = Range(AfterVerb, N).Where(i => Open(i) && !IsFiller(i) && !(OpenWord(i) && EdgeNoise.Contains(t[i].Word))).ToList();
                if (rest.Count > 0)
                {
                    rest.ForEach(Consume);
                    window = new WindowSelector(NameText: Join(rest));
                }
            }
            if (window is null) return Unknown("Wait for what? Try: wait for window \"Save As\", wait until w5 is gone, or wait until c12 says Done.");
            return Cmd(CommandKind.WaitFor) with
            {
                Window = window, Delay = timeout,
                Wait = new WaitCondition(condition == WaitTarget.ControlGone ? WaitTarget.WindowGone : WaitTarget.WindowAppears),
            };
        }

        private string? OpenWordIgnoringUse(int i) => i >= 0 && i < N && !t[i].IsQuoted ? t[i].Word : null;

        private static WaitTarget ConditionFor(string w) =>
            WaitGoneWords.Contains(w) ? WaitTarget.ControlGone
            : WaitChangeWords.Contains(w) ? WaitTarget.ControlValueChanges
            : WaitEnabledWords.Contains(w) ? WaitTarget.ControlEnabled
            : WaitTarget.ControlAppears;

        private static bool IsCondition(string w) =>
            WaitGoneWords.Contains(w) || WaitAppearWords.Contains(w) || WaitChangeWords.Contains(w) || WaitEnabledWords.Contains(w);

        private RemoteCommand ParseList()
        {
            if (AnyOpen(ControlNouns) || AnyOpenLowerTypeWord()) return ParseControlListing();
            if (AnyOpen(WindowListNouns)) return Cmd(CommandKind.ListWindows);
            if (AnyOpen(ScreenWords)) return Cmd(CommandKind.ListDisplays);
            return Cmd(CommandKind.ListWindows);
        }

        private RemoteCommand ParseControlListing()
        {
            var verbose = AnyOpen("all", "every", "everything");
            windowNameToEnd = true;
            ConsumeAll(Set("controls", "elements", "widgets", "everything", "every"));
            ConsumeAll(ListNoise);
            var win = ExtractWindow(0, N, WindowMode.Explicit);

            string? name = null;
            foreach (var i in Range(0, N))
            {
                if (!IsWord(i, NameMarkers)) continue;
                Consume(i);
                var words = new List<int>();
                var j = i + 1;
                while (j < N && Open(j) && (t[j].IsQuoted || !(Stops.Contains(t[j].Word) || WindowPrepositions.Contains(t[j].Word) || IsWindowAlias(j)))) words.Add(j++);
                words.ForEach(Consume);
                if (words.Count > 0) name = Join(words);
                break;
            }

            string? type = null;
            foreach (var i in Range(0, N))
            {
                if (!OpenWord(i) || !IsLowerTypeWord(i)) continue;
                type = TypeWords[t[i].Word];
                Consume(i);
                break;
            }
            ConsumeAll(Set("control", "element"));
            win ??= ExtractWindow(0, N, Read);

            var rest = Range(0, N).Where(i => Open(i) && !IsFiller(i) && !(OpenWord(i) && EdgeNoise.Contains(t[i].Word))).ToList();
            if (rest.Count > 0)
            {
                if (type is not null && name is null) { name = Join(rest); rest.ForEach(Consume); }
                else if (win is null && name is null && type is null) { win = new WindowSelector(NameText: Join(rest)); rest.ForEach(Consume); }
            }

            var find = type is not null || name is not null;
            return Cmd(find ? CommandKind.FindControl : CommandKind.ListControls) with
            {
                Window = win,
                Control = find ? new ControlSelector(ControlType: type, NameText: name) : null,
                Verbose = verbose,
            };
        }

        private RemoteCommand ParseFind()
        {
            ConsumeAll(Set("for"), AfterVerb);
            var controlish = Range(0, N).Any(i => Open(i) && (t[i].IsQuoted || IsControlAlias(i) || IsControlKeyword(i) || IsLowerTypeWord(i)));
            var windowish = Range(0, N).Any(i => IsWord(i, WindowNouns) || (OpenWord(i) && IsWindowAlias(i)));
            if (controlish && !(windowish && !Range(0, N).Any(i => OpenWord(i) && (IsControlAlias(i) || IsControlKeyword(i) || IsLowerTypeWord(i)))))
            {
                var win = ExtractWindow(0, N, Read);
                var control = ExtractControl(0, N, FindNoise, multiwordAfterKeyword: true);
                if (control is null) return Unknown("Find which control? Try: find link Next Page in chrome.");
                return Cmd(CommandKind.FindControl) with { Window = win, Control = control };
            }
            windowNameToEnd = true;
            var window = ExtractWindow(0, N, Read);
            if (window is null)
            {
                var rest = Range(AfterVerb, N).Where(i => Open(i) && !IsFiller(i) && !(OpenWord(i) && FindNoise.Contains(t[i].Word))).ToList();
                if (rest.Count > 0)
                {
                    rest.ForEach(Consume);
                    window = new WindowSelector(NameText: Join(rest));
                }
            }
            return window is null
                ? Unknown("Find what? Try: find window chrome, or find link Next Page in chrome.")
                : Cmd(CommandKind.FindWindow) with { Window = window };
        }

        private RemoteCommand ParseInspect()
        {
            ConsumeAll(InspectNoise);
            var win = ExtractWindow(0, N, Act);
            var control = ExtractControl(0, N, InspectNoise);
            return control is null
                ? Unknown("Inspect which control? Try: inspect c12.")
                : Cmd(CommandKind.Inspect) with { Window = win, Control = control };
        }
    }
}

/// <summary>Normalizes key chords to the lowercase, <c>+</c>-joined form <see cref="IDesktopAutomation.PressKeys"/> takes.</summary>
public static class KeyChord
{
    private static readonly string[] ModifierOrder = ["primary", "ctrl", "alt", "shift", "win"];

    private static readonly Dictionary<string, string> Synonyms = new(StringComparer.Ordinal)
    {
        ["control"] = "ctrl", ["ctl"] = "ctrl", ["option"] = "alt", ["opt"] = "alt", ["mod"] = "primary", ["cmdorctrl"] = "primary",
        ["windows"] = "win", ["command"] = "win", ["cmd"] = "win", ["super"] = "win", ["meta"] = "win", ["winkey"] = "win",
        ["return"] = "enter", ["ret"] = "enter", ["esc"] = "escape", ["del"] = "delete", ["ins"] = "insert",
        ["pgdn"] = "pagedown", ["pgdown"] = "pagedown", ["pagedn"] = "pagedown", ["pgup"] = "pageup",
        ["bksp"] = "backspace", ["back"] = "backspace", ["spacebar"] = "space",
        ["caps"] = "capslock", ["prtsc"] = "printscreen", ["prtscn"] = "printscreen", ["prtscr"] = "printscreen",
        ["printscrn"] = "printscreen", ["print"] = "printscreen", ["break"] = "pause",
        ["uparrow"] = "up", ["arrowup"] = "up", ["downarrow"] = "down", ["arrowdown"] = "down",
        ["leftarrow"] = "left", ["arrowleft"] = "left", ["rightarrow"] = "right", ["arrowright"] = "right",
        ["menu"] = "apps", ["context"] = "apps", ["contextmenu"] = "apps", ["application"] = "apps",
        [","] = "comma", ["."] = "period", ["dot"] = "period", ["/"] = "slash", ["\\"] = "backslash", [";"] = "semicolon",
        ["'"] = "quote", ["apostrophe"] = "quote", ["`"] = "backquote", ["grave"] = "backquote", ["tilde"] = "backquote",
        ["["] = "leftbracket", ["]"] = "rightbracket", ["openbracket"] = "leftbracket", ["closebracket"] = "rightbracket",
        ["="] = "equals", ["equal"] = "equals", ["*"] = "multiply", ["hyphen"] = "minus", ["dash"] = "minus",
        ["scroll"] = "scrolllock",
    };

    private static readonly HashSet<string> Keys = BuildKeys();

    private static HashSet<string> BuildKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal)
        {
            "enter", "tab", "escape", "space", "backspace", "delete", "insert", "home", "end", "pageup", "pagedown",
            "up", "down", "left", "right", "printscreen", "pause", "capslock", "numlock", "scrolllock", "apps",
            "plus", "minus", "comma", "period", "slash", "backslash", "semicolon", "quote", "backquote",
            "leftbracket", "rightbracket", "equals", "multiply", "divide", "add", "subtract", "decimal",
        };
        for (var c = 'a'; c <= 'z'; c++) keys.Add(c.ToString());
        for (var d = 0; d <= 9; d++) { keys.Add(d.ToString(CultureInfo.InvariantCulture)); keys.Add($"numpad{d}"); }
        for (var f = 1; f <= 24; f++) keys.Add($"f{f}");
        return keys;
    }

    private static readonly Dictionary<string, string> TwoWordKeys = new(StringComparer.Ordinal)
    {
        ["page down"] = "pagedown", ["page up"] = "pageup", ["print screen"] = "printscreen", ["caps lock"] = "capslock",
        ["num lock"] = "numlock", ["scroll lock"] = "scrolllock", ["context menu"] = "apps", ["back space"] = "backspace",
    };

    /// <summary>True when <paramref name="word"/> is a modifier name or synonym ("ctrl", "control", "primary", "mod", "cmd").</summary>
    public static bool IsModifier(string word)
    {
        var w = word.Trim().ToLowerInvariant();
        return ModifierOrder.Contains(Synonyms.TryGetValue(w, out var mapped) ? mapped : w);
    }

    /// <summary>True when <paramref name="word"/> is a key, a modifier, or a whole chord such as "ctrl+s".</summary>
    public static bool IsKeyOrModifier(string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return false;
        if (IsModifier(word)) return true;
        try { Normalize(word); return true; }
        catch (ArgumentException) { return false; }
    }

    /// <summary>
    /// Normalizes "Control+Shift+T", "ctrl shift t", "cmd-r", "Return" to "ctrl+shift+t", "win+r", "enter":
    /// modifiers first in the order primary, ctrl, alt, shift, win, then at most one key. "primary" (also "mod",
    /// "cmdorctrl") is the platform's command modifier: Control on Windows, Command on macOS.
    /// </summary>
    /// <exception cref="ArgumentException">The chord is empty, names an unknown key, or names more than one non-modifier key.</exception>
    public static string Normalize(string chord)
    {
        if (string.IsNullOrWhiteSpace(chord)) throw new ArgumentException("The key chord is empty.", nameof(chord));
        var s = chord.Trim().ToLowerInvariant();
        if (s == "+") s = "plus";
        else if (s == "-") s = "minus";
        else if (s.EndsWith("++", StringComparison.Ordinal)) s = s[..^1] + "plus";
        else if (s.EndsWith("+-", StringComparison.Ordinal) || s.EndsWith(" -", StringComparison.Ordinal)) s = s[..^1] + "minus";

        var raw = s.Split(['+', ' ', '\t', '-'], StringSplitOptions.RemoveEmptyEntries).ToList();
        var tokens = new List<string>();
        for (var i = 0; i < raw.Count; i++)
        {
            var tok = raw[i];
            if (tok is "arrow" or "key" or "keys") continue;
            if (i + 1 < raw.Count && TwoWordKeys.TryGetValue($"{tok} {raw[i + 1]}", out var two)) { tokens.Add(two); i++; continue; }
            if (tok is "numpad" or "num" && i + 1 < raw.Count && raw[i + 1].Length == 1 && char.IsDigit(raw[i + 1][0])) { tokens.Add("numpad" + raw[i + 1]); i++; continue; }
            if (tok.StartsWith("num", StringComparison.Ordinal) && tok.Length == 4 && char.IsDigit(tok[3])) { tokens.Add("numpad" + tok[3]); continue; }
            tokens.Add(tok);
        }
        if (tokens.Count == 0) throw new ArgumentException($"The key chord '{chord}' names no keys.", nameof(chord));

        var modifiers = new HashSet<string>(StringComparer.Ordinal);
        string? key = null;
        foreach (var tok in tokens)
        {
            var name = Synonyms.TryGetValue(tok, out var mapped) ? mapped : tok;
            if (ModifierOrder.Contains(name)) { modifiers.Add(name); continue; }
            if (!Keys.Contains(name)) throw new ArgumentException($"Unknown key '{tok}' in '{chord}'.", nameof(chord));
            if (key is not null && key != name) throw new ArgumentException($"'{chord}' names more than one key ({key} and {name}); press them one at a time.", nameof(chord));
            key = name;
        }
        var parts = ModifierOrder.Where(modifiers.Contains).ToList();
        if (key is not null) parts.Add(key);
        return string.Join("+", parts);
    }
}
