using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting.ToolUse;

/// <summary>
/// Result of trying to recover real tool calls from a subscription-CLI reply
/// that used prose or Anthropic-style XML markup instead of the JSON envelope.
/// </summary>
internal sealed record SalvagedReply(
    IReadOnlyList<ToolUsePart> Calls,
    string ResidualText,
    bool SawIncompleteCallSyntax,
    bool SawUndeclaredToolName);

/// <summary>
/// Repairs the two things subscription CLIs get wrong often enough to be the
/// dominant source of agent-turn failure:
///
/// 1. The model narrates a tool call instead of emitting one — either as
///    markdown (<c>**Tool Call: inbox.list_rules**</c>) or as the Anthropic
///    XML calling convention (<c>&lt;invoke name="x"&gt;&lt;parameter …&gt;</c>)
///    that the CLI's own harness normally consumes. Both arrive here as plain
///    text with zero tool calls, which ends the agent turn on a non-answer.
///    We parse the unambiguous forms back into real <see cref="ToolUsePart"/>s.
///
/// 2. The model echoes protocol scaffolding — most visibly a
///    <c>&lt;system-reminder&gt;</c> block restating the JSON contract — into
///    text that is rendered verbatim to the user. None of that is ours to show.
///
/// Everything here is deliberately strict: salvage only fires on syntax that
/// cannot plausibly be prose, and a recovered call must name a tool the request
/// actually declared.
/// </summary>
internal static class SubscriptionCliReplySalvage
{
    private const RegexOptions Flags =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    // Optional namespace prefix. Real Claude output uses `antml:invoke`, but
    // the model also invents STACKED prefixes when it is reconstructing the
    // convention from memory — observed live as
    // `<epsilon:tool_antml:invoke name="code.run_csharp">`. Allow any number of
    // segments so one extra prefix does not turn a recoverable call into raw
    // markup on the user's screen.
    private const string Ns = @"(?:[A-Za-z_][\w.\-]*:)*";

    private static readonly Regex ClosedReminderRegex = new(
        $@"<\s*{Ns}system[-_ ]?reminder\b[^>]*>.*?<\s*/\s*{Ns}system[-_ ]?reminder\s*>",
        Flags | RegexOptions.Singleline);

    private static readonly Regex StrayReminderTagRegex = new(
        $@"<\s*/?\s*{Ns}system[-_ ]?reminder\b[^>]*>",
        Flags);

    private static readonly Regex StrayFunctionCallsTagRegex = new(
        $@"<\s*/?\s*{Ns}function_calls\s*>",
        Flags);

    // Tag-name families the model reaches for when re-imagining the calling
    // convention: the real `invoke`/`parameter` pair, plus observed dialects
    // like `<call_tool name="…"><param name="…">`.
    private const string InvokeTag = @"(?:invoke|call_tool|tool_call|function_call|call)";
    private const string ParameterTag = @"(?:parameter|param|argument|arg)";

    private static readonly Regex ClosedInvokeRegex = new(
        $@"<\s*{Ns}{InvokeTag}\s+name\s*=\s*""(?<name>[^""]+)""\s*>(?<body>.*?)<\s*/\s*{Ns}{InvokeTag}\s*>",
        Flags | RegexOptions.Singleline);

    private static readonly Regex OpenInvokeRegex = new(
        $@"<\s*{Ns}{InvokeTag}\s+name\s*=[^>]*>",
        Flags);

    private static readonly Regex ClosedParameterRegex = new(
        $@"<\s*{Ns}{ParameterTag}\s+name\s*=\s*""(?<pname>[^""]+)""\s*>(?<pvalue>.*?)<\s*/\s*{Ns}{ParameterTag}\s*>",
        Flags | RegexOptions.Singleline);

    private static readonly Regex OpenParameterRegex = new(
        $@"<\s*{Ns}{ParameterTag}\s+name\s*=[^>]*>",
        Flags);

    // `**Tool Call: inbox.list_rules**`, `**Tool call:** inbox.list_rules`,
    // and the PLURAL header the model also emits (`**Tool Calls:** name`) —
    // `Tool_call: inbox.list_rules`. Anchored to a line start so an answer that
    // merely mentions a tool mid-sentence is never rewritten into a call.
    private static readonly Regex ProseToolCallRegex = new(
        @"^[ \t]*[>*_`#\-]*[ \t]*tool[ _\-]?calls?[ \t]*\**[ \t]*[:\-][ \t]*\**[ \t]*(?<name>[A-Za-z_][A-Za-z0-9_.\-]*)[ \t]*\**[ \t]*\r?$",
        Flags | RegexOptions.Multiline);

    // Whitespace / bold markers / an "arguments:" label / an opening code fence
    // may sit between the announced tool name and its JSON input. Nothing else.
    private static readonly Regex ArgumentsPreambleRegex = new(
        @"\A(?:[ \t\r\n]*\**[ \t]*(?:with\s+)?(?:arguments?|args|input|parameters?|params)\s*[:=]?)?[ \t\r\n]*\**[ \t\r\n]*(?:`{3,}[a-z]*[ \t]*\r?\n)?[ \t\r\n]*",
        Flags);

    // `code.run_csharp({"code":"…"})` — the model writes the call as a function
    // invocation, or as a (possibly numbered) list item in colon form
    // (`1. code.run_csharp: {"code":…}`), usually under a bare `**Tool Calls:**`
    // header. Anchored to a line start; the lookahead leaves the `{` for the
    // balanced-object reader.
    private static readonly Regex FunctionStyleCallRegex = new(
        @"^[ \t]*(?:\d+[.)][ \t]*)?[>*_`#\-]*[ \t]*(?<name>[A-Za-z_][\w\-]*(?:\.[A-Za-z_][\w\-]*)+)\s*(?:\(\s*|:[ \t]*|[ \t]+(?:request|call|invocation|input|args|arguments)\s*:?\s*)(?=\{)",
        Flags | RegexOptions.Multiline);

    // A call written with keyword/property assignments:
    // `_run(name="data.query-sql", input={…})`, or the further-truncated
    // `.name="shared.web-search" input={…}`. Both were observed live. The
    // quoted tool name is unambiguous; the lookahead leaves the `{` for the
    // balanced-object reader.
    private static readonly Regex KeywordArgumentCallRegex = new(
        @"(?<lead>^|[^\w.])(?:[\w.]*\(\s*name|\.name)\s*=\s*""(?<name>[A-Za-z_][\w.\-]*)""\s*,?\s*(?:input|arguments|args|parameters)\s*=\s*(?=\{)",
        Flags | RegexOptions.Multiline);

    // `.run_csharp` on its own line (or directly before a JSON object) — the
    // tool name arrived truncated to its final dotted segment. The fragment is
    // only usable when it resolves to exactly ONE declared tool by suffix.
    private static readonly Regex DotFragmentCallRegex = new(
        @"^[ \t]*\.(?<frag>[A-Za-z_]\w{2,})[ \t]*\(?[ \t]*(?=\{|\r?$)",
        Flags | RegexOptions.Multiline);

    // A `**Tool Calls:**` header with no name on the line. Only removed when a
    // call was actually salvaged below it — on its own it proves nothing.
    private static readonly Regex BareToolCallHeaderRegex = new(
        @"^[ \t]*[>*_`#\-]*[ \t]*tool[ _\-]?calls?[ \t]*[:\-]?[ \t]*[*_`]*[ \t]*\r?$\n?",
        Flags | RegexOptions.Multiline);

    // A degenerate call marker with no usable call in it — observed as
    // `{"tool_call":}` and `{"tool_call": "data.query-sql"}`. There is no
    // input to run, so it is debris, not a call.
    private static readonly Regex DegenerateToolCallKeyRegex = new(
        @"\{\s*""tool_call""\s*:",
        Flags);

    // The CLI's own harness rejecting a native tool call and echoing the error
    // as the reply, e.g. `<tool_use_error>Error: Tool "call_1" not found…`.
    // That is the host talking, never an answer.
    private static readonly Regex HarnessErrorEchoRegex = new(
        @"<\s*/?\s*tool_use_error\b",
        Flags);

    // A JSON call structure whose opening was cut off, so the reply starts in
    // the middle of the name string: `.run_csharp","input":{…}`. The surviving
    // fragment still identifies the tool when it matches exactly one declared
    // name by suffix.
    private static readonly Regex TruncatedNamedCallPrefixRegex = new(
        @"\A\s*\.?(?<frag>[A-Za-z_][\w\-]*)""\s*,\s*""(?:input|arguments)""\s*:\s*(?=\{)",
        Flags);

    // The entire reply is one or more bare call ids (`call_11`) and nothing
    // else — debris from a mangled envelope, never an answer.
    private static readonly Regex CallIdDebrisRegex = new(
        @"\A[\s>*_`#\-]*(?:call_\d+[\s,.;:*_`\-]*)+\z",
        Flags);

    // Transcript-echo lines the model copies from its own UI when narrating a
    // call (`Status: Completed`, `Diff: undefined`). Removed ONLY from a reply
    // that already yielded a salvaged call — in a plain answer, a line like
    // "Status: Completed" can be legitimate content (a task report).
    private static readonly Regex TranscriptEchoLineRegex = new(
        @"^[ \t]*[>*_`#\-]*[ \t]*(?:status[ \t]*:[ \t]*(?:completed|failed|in.?progress)|diff[ \t]*:[ \t]*(?:undefined|null))[ \t]*[*_`]*[ \t]*\r?$\n?",
        Flags | RegexOptions.Multiline);

    // Lines that are verbatim (or near-verbatim) protocol boilerplate. A model
    // that echoes the contract back at the user is quoting us, not answering.
    private static readonly string[] ScaffoldingLinePrefixes =
    [
        "you are serving one swarmcore",
        "this is a structured tool-calling turn",
        "this is a structured tool calling turn",
        "return only one json object",
        "return exactly one json object",
        "return this exact shape",
        "no markdown, no prose",
        "do not use markdown",
        "reply now with only the json object",
        "swarmcore turn:",
    ];

    /// <summary>
    /// Removes protocol scaffolding the model echoed back. Never applied to
    /// JSON parsing input — only to text that would reach a human.
    /// </summary>
    public static string StripScaffolding(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var working = ClosedReminderRegex.Replace(text, "\n");
        working = StrayReminderTagRegex.Replace(working, string.Empty);
        working = StrayFunctionCallsTagRegex.Replace(working, string.Empty);
        working = RemoveScaffoldingLines(working);
        return CollapseBlankRuns(working);
    }

    /// <summary>
    /// Pulls unambiguous tool-call syntax out of a reply that arrived as text.
    /// </summary>
    public static SalvagedReply Salvage(string? text, IReadOnlyList<ChatToolDefinition> tools)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new SalvagedReply([], string.Empty, false, false);
        }

        var calls = new List<ToolUsePart>();
        var undeclared = false;
        var incomplete = false;
        var index = 1;

        var working = ExtractTruncatedNamedCallPrefix(text, tools, calls, ref index, ref incomplete);
        working = ExtractXmlInvokes(working, tools, calls, ref index, ref undeclared);
        working = ExtractProseCalls(working, tools, calls, ref index, ref undeclared);
        working = ExtractJsonCallStructures(working, tools, calls, ref index, ref undeclared);
        working = ExtractKeywordArgumentCalls(working, tools, calls, ref index, ref incomplete);
        working = ExtractFunctionStyleCalls(working, tools, calls, ref index, ref incomplete);
        working = ExtractDotFragmentCalls(working, tools, calls, ref index, ref incomplete, ref undeclared);
        if (calls.Count > 0)
        {
            // A salvaged call may leave its bare `**Tool Calls:**` header and
            // echoed transcript-status lines behind.
            working = BareToolCallHeaderRegex.Replace(working, string.Empty);
            working = TranscriptEchoLineRegex.Replace(working, string.Empty);
        }

        // Anything left that still opens a call block is a truncated call: the
        // arguments cannot be trusted, so drop the markup and report it so the
        // caller reprompts instead of showing raw XML to the user.
        var openInvoke = OpenInvokeRegex.Match(working);
        if (openInvoke.Success)
        {
            incomplete = true;
            working = working[..openInvoke.Index];
        }

        var openParameter = OpenParameterRegex.Match(working);
        if (openParameter.Success)
        {
            incomplete = true;
            working = working[..openParameter.Index];
        }

        return new SalvagedReply(
            calls,
            StripScaffolding(working),
            incomplete,
            undeclared);
    }

    private static string ExtractXmlInvokes(
        string text,
        IReadOnlyList<ChatToolDefinition> tools,
        List<ToolUsePart> calls,
        ref int index,
        ref bool undeclared)
    {
        var matches = ClosedInvokeRegex.Matches(text);
        if (matches.Count == 0)
        {
            return text;
        }

        var builder = new StringBuilder();
        var cursor = 0;
        foreach (Match match in matches)
        {
            var declared = FindTool(tools, match.Groups["name"].Value);
            if (declared is null)
            {
                undeclared = true;
                // Still remove the markup: raw XML must not reach the user
                // whether or not we can turn it into a call.
                builder.Append(text, cursor, match.Index - cursor);
                cursor = match.Index + match.Length;
                continue;
            }

            var input = new JsonObject();
            foreach (Match parameter in ClosedParameterRegex.Matches(match.Groups["body"].Value))
            {
                var name = parameter.Groups["pname"].Value;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                input[name] = CoerceParameter(parameter.Groups["pvalue"].Value, declared.InputSchema, name);
            }

            calls.Add(new ToolUsePart($"cli_salvaged_{index++}", declared.Name, input));
            builder.Append(text, cursor, match.Index - cursor);
            cursor = match.Index + match.Length;
        }

        builder.Append(text, cursor, text.Length - cursor);
        return builder.ToString();
    }

    private static string ExtractProseCalls(
        string text,
        IReadOnlyList<ChatToolDefinition> tools,
        List<ToolUsePart> calls,
        ref int index,
        ref bool undeclared)
    {
        var matches = ProseToolCallRegex.Matches(text);
        if (matches.Count == 0)
        {
            return text;
        }

        var builder = new StringBuilder();
        var cursor = 0;
        foreach (Match match in matches)
        {
            if (match.Index < cursor)
            {
                continue;
            }

            var declared = FindTool(tools, match.Groups["name"].Value);
            if (declared is null)
            {
                undeclared = true;
                continue;
            }

            var consumedTo = match.Index + match.Length;
            var input = TryReadTrailingArguments(text, consumedTo, out var argumentsEnd)
                        ?? new JsonObject();
            if (argumentsEnd > consumedTo)
            {
                consumedTo = argumentsEnd;
            }

            calls.Add(new ToolUsePart($"cli_salvaged_{index++}", declared.Name, input));
            builder.Append(text, cursor, match.Index - cursor);
            cursor = consumedTo;
        }

        if (cursor == 0)
        {
            return text;
        }

        builder.Append(text, cursor, text.Length - cursor);
        return builder.ToString();
    }

    /// <summary>
    /// Recovers tool calls the model emitted as raw JSON structures in text —
    /// the envelope re-imagined from memory in an invented dialect. Observed
    /// live, each as the tail (or entirety) of a reply:
    ///   {"name":"code.run_csharp","input":{…}}            (bare call object)
    ///   [{"name":"…","input":{…},"id":"call_13"}]         (bare call array)
    ///   &lt;ftc&gt;{"tc":[{"id":…,"name":…,"input":{…}}]}&lt;/ftc&gt; (wrapped, invented key)
    /// Strict on purpose: a structure only converts when every element has a
    /// "name" naming a DECLARED tool and an object-valued "input"/"arguments".
    /// A call-shaped structure naming an undeclared tool is reported so the
    /// caller reprompts instead of rendering machine JSON to the user.
    /// </summary>
    private static string ExtractJsonCallStructures(
        string text,
        IReadOnlyList<ChatToolDefinition> tools,
        List<ToolUsePart> calls,
        ref int index,
        ref bool undeclared)
    {
        var builder = new StringBuilder();
        var cursor = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (i < cursor || (text[i] != '{' && text[i] != '['))
            {
                continue;
            }

            var span = ExtractBalancedValue(text, i);
            if (span is null)
            {
                continue;
            }

            var node = TryParseNode(span);
            if (node is null)
            {
                continue;
            }

            var callNodes = TryReadCallStructure(node);
            if (callNodes is null)
            {
                continue;
            }

            var declaredCalls = new List<ToolUsePart>(callNodes.Count);
            var allDeclared = true;
            foreach (var (name, input) in callNodes)
            {
                var declared = FindTool(tools, name);
                if (declared is null)
                {
                    allDeclared = false;
                    break;
                }

                declaredCalls.Add(new ToolUsePart(
                    $"cli_salvaged_{index + declaredCalls.Count}",
                    declared.Name,
                    input));
            }

            if (!allDeclared)
            {
                // Call-shaped JSON naming a tool that was never declared: not
                // ours to run, not fit to render. Reprompt.
                undeclared = true;
                builder.Append(text, cursor, i - cursor);
                return builder.ToString();
            }

            index += declaredCalls.Count;
            calls.AddRange(declaredCalls);
            builder.Append(text, cursor, i - cursor);
            cursor = i + span.Length;
        }

        if (cursor == 0)
        {
            return text;
        }

        builder.Append(text, cursor, text.Length - cursor);
        var residual = builder.ToString();
        // The structure may have sat inside an invented wrapper tag (<ftc>…</ftc>);
        // removing the JSON leaves the empty tag pair behind.
        residual = Regex.Replace(
            residual,
            @"<(\w{1,12})>\s*</\1>",
            string.Empty,
            RegexOptions.CultureInvariant);
        return residual;
    }

    /// <summary>
    /// Interprets a parsed JSON node as one-or-more tool calls, or null when
    /// the node is not unambiguously call-shaped. Accepted shapes: a call
    /// object ({"name":…,"input":{…}}), an array of call objects, and an
    /// object wrapping exactly one such array under any key.
    /// </summary>
    private static List<(string Name, JsonObject Input)>? TryReadCallStructure(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj when TryReadCallObject(obj) is { } single:
                return [single];
            case JsonObject obj when obj.Count == 1
                                     && obj.First().Value is JsonArray wrapped:
                return TryReadCallArray(wrapped);
            case JsonArray array:
                return TryReadCallArray(array);
            default:
                return null;
        }
    }

    private static List<(string Name, JsonObject Input)>? TryReadCallArray(JsonArray array)
    {
        if (array.Count == 0)
        {
            return null;
        }

        var result = new List<(string, JsonObject)>(array.Count);
        foreach (var element in array)
        {
            if (element is not JsonObject obj || TryReadCallObject(obj) is not { } call)
            {
                return null;
            }

            result.Add(call);
        }

        return result;
    }

    private static (string Name, JsonObject Input)? TryReadCallObject(JsonObject obj)
    {
        if (obj["name"] is not JsonValue nameValue
            || nameValue.GetValueKind() != System.Text.Json.JsonValueKind.String)
        {
            return null;
        }

        var input = obj["input"] as JsonObject ?? obj["arguments"] as JsonObject;
        if (input is null)
        {
            return null;
        }

        // Only call-plumbing keys may be present; any other key means this is
        // ordinary data that happens to have a "name" field, not a call.
        foreach (var property in obj)
        {
            if (property.Key is not ("name" or "input" or "arguments" or "id" or "type"))
            {
                return null;
            }
        }

        return (nameValue.GetValue<string>(), (JsonObject)input.DeepClone());
    }

    /// <summary>
    /// Recovers a call whose JSON opening was cut off, leaving the reply
    /// starting mid-name: <c>.run_csharp","input":{…}</c>. Only fires at the
    /// very start of the reply (a truncation artifact, never something a model
    /// writes deliberately) and only when the surviving fragment matches
    /// exactly one declared tool by suffix.
    /// </summary>
    private static string ExtractTruncatedNamedCallPrefix(
        string text,
        IReadOnlyList<ChatToolDefinition> tools,
        List<ToolUsePart> calls,
        ref int index,
        ref bool incomplete)
    {
        var match = TruncatedNamedCallPrefixRegex.Match(text);
        if (!match.Success)
        {
            return text;
        }

        var fragment = match.Groups["frag"].Value;
        var candidates = tools
            .Where(t => string.Equals(t.Name, fragment, StringComparison.OrdinalIgnoreCase)
                        || t.Name.EndsWith("." + fragment, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count != 1)
        {
            return text;
        }

        var span = ExtractBalancedObject(text, match.Length);
        if (span is null || TryParseNode(span) is not JsonObject input)
        {
            // The name resolved but the arguments did not: reprompt rather
            // than run a call whose input we had to guess at.
            incomplete = true;
            return string.Empty;
        }

        calls.Add(new ToolUsePart($"cli_salvaged_{index++}", candidates[0].Name, input));

        // Whatever trails the object is the tail of the mangled envelope
        // (`}}]`), never prose worth showing.
        var remainder = text[(match.Length + span.Length)..];
        return Regex.IsMatch(remainder, @"\A[\s\}\]\),]*\z", RegexOptions.CultureInvariant)
            ? string.Empty
            : remainder;
    }

    /// <summary>
    /// Recovers a call written with keyword/property assignments —
    /// <c>_run(name="data.query-sql", input={…})</c> or
    /// <c>.name="data.query-sql" input={…}</c>, observed live with the leading
    /// identifier truncated away. The quoted name must be a declared tool; a
    /// declared tool whose argument object is unparseable is reported incomplete
    /// so the caller reprompts rather than guessing.
    /// </summary>
    private static string ExtractKeywordArgumentCalls(
        string text,
        IReadOnlyList<ChatToolDefinition> tools,
        List<ToolUsePart> calls,
        ref int index,
        ref bool incomplete)
    {
        var matches = KeywordArgumentCallRegex.Matches(text);
        if (matches.Count == 0)
        {
            return text;
        }

        var builder = new StringBuilder();
        var cursor = 0;
        foreach (Match match in matches)
        {
            if (match.Index < cursor)
            {
                continue;
            }

            var declared = FindTool(tools, match.Groups["name"].Value);
            if (declared is null)
            {
                continue;
            }

            // The lead group may have consumed one separator character that is
            // ordinary text and must survive.
            var callStart = match.Index + match.Groups["lead"].Length;
            var jsonStart = match.Index + match.Length;
            var span = ExtractBalancedObject(text, jsonStart);
            var input = span is null ? null : TryParseNode(span) as JsonObject;
            if (input is null)
            {
                incomplete = true;
                builder.Append(text, cursor, callStart - cursor);
                return builder.ToString();
            }

            var consumedTo = jsonStart + span!.Length;
            var closing = Regex.Match(
                text[consumedTo..], @"\A\s*\)[ \t]*;?", RegexOptions.CultureInvariant);
            if (closing.Success)
            {
                consumedTo += closing.Length;
            }

            calls.Add(new ToolUsePart($"cli_salvaged_{index++}", declared.Name, input));
            builder.Append(text, cursor, callStart - cursor);
            cursor = consumedTo;
        }

        if (cursor == 0)
        {
            return text;
        }

        builder.Append(text, cursor, text.Length - cursor);
        return builder.ToString();
    }

    /// <summary>
    /// Recovers `code.run_csharp({"code":"…"})` — a call written as a function
    /// invocation. Only a name that exactly matches a declared tool is touched;
    /// anything else (a code sample, an API in prose) stays text, because a
    /// spurious salvage-or-reprompt on a legitimate final answer is worse than
    /// showing one odd line. A declared name whose argument object is truncated
    /// or unparseable is reported incomplete so the caller reprompts.
    /// </summary>
    private static string ExtractFunctionStyleCalls(
        string text,
        IReadOnlyList<ChatToolDefinition> tools,
        List<ToolUsePart> calls,
        ref int index,
        ref bool incomplete)
    {
        var matches = FunctionStyleCallRegex.Matches(text);
        if (matches.Count == 0)
        {
            return text;
        }

        var builder = new StringBuilder();
        var cursor = 0;
        foreach (Match match in matches)
        {
            if (match.Index < cursor)
            {
                continue;
            }

            var declared = FindTool(tools, match.Groups["name"].Value);
            if (declared is null)
            {
                continue;
            }

            var jsonStart = match.Index + match.Length;
            var span = ExtractBalancedObject(text, jsonStart);
            var input = span is null ? null : TryParseNode(span) as JsonObject;
            if (input is null)
            {
                // A declared tool with arguments we cannot trust: cut the debris
                // and reprompt rather than guessing or rendering it.
                incomplete = true;
                builder.Append(text, cursor, match.Index - cursor);
                return builder.ToString();
            }

            var consumedTo = jsonStart + span!.Length;
            var closing = Regex.Match(
                text[consumedTo..], @"\A\s*\)[ \t]*;?", RegexOptions.CultureInvariant);
            if (closing.Success)
            {
                consumedTo += closing.Length;
            }

            calls.Add(new ToolUsePart($"cli_salvaged_{index++}", declared.Name, input));
            builder.Append(text, cursor, match.Index - cursor);
            cursor = consumedTo;
        }

        if (cursor == 0)
        {
            return text;
        }

        builder.Append(text, cursor, text.Length - cursor);
        return builder.ToString();
    }

    /// <summary>
    /// Recovers `.run_csharp` + a JSON object — a call whose tool name arrived
    /// truncated to its final dotted segment. Strict on purpose: the fragment
    /// must resolve to exactly ONE declared tool by suffix, and the arguments
    /// must be a complete JSON object. A fragment matching nothing is left
    /// alone (it could be CSS or prose); a fragment matching a tool with
    /// untrustworthy arguments is reported incomplete; an AMBIGUOUS fragment is
    /// reported undeclared — both make the caller reprompt instead of guessing.
    /// </summary>
    private static string ExtractDotFragmentCalls(
        string text,
        IReadOnlyList<ChatToolDefinition> tools,
        List<ToolUsePart> calls,
        ref int index,
        ref bool incomplete,
        ref bool undeclared)
    {
        var matches = DotFragmentCallRegex.Matches(text);
        if (matches.Count == 0)
        {
            return text;
        }

        var builder = new StringBuilder();
        var cursor = 0;
        foreach (Match match in matches)
        {
            if (match.Index < cursor)
            {
                continue;
            }

            var fragment = match.Groups["frag"].Value;
            var candidates = tools
                .Where(t => string.Equals(t.Name, fragment, StringComparison.OrdinalIgnoreCase)
                            || t.Name.EndsWith("." + fragment, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (candidates.Count == 0)
            {
                continue;
            }

            var input = TryReadTrailingArguments(text, match.Index + match.Length, out var argumentsEnd);
            if (candidates.Count > 1)
            {
                undeclared = true;
                builder.Append(text, cursor, match.Index - cursor);
                return builder.ToString();
            }

            if (input is null)
            {
                incomplete = true;
                builder.Append(text, cursor, match.Index - cursor);
                return builder.ToString();
            }

            calls.Add(new ToolUsePart($"cli_salvaged_{index++}", candidates[0].Name, input));
            builder.Append(text, cursor, match.Index - cursor);
            // The paren form (`.run_csharp({…})`) leaves its closing paren.
            var closing = Regex.Match(
                text[argumentsEnd..], @"\A\s*\)[ \t]*;?", RegexOptions.CultureInvariant);
            cursor = argumentsEnd + (closing.Success ? closing.Length : 0);
        }

        if (cursor == 0)
        {
            return text;
        }

        builder.Append(text, cursor, text.Length - cursor);
        return builder.ToString();
    }

    // The entire reply is one lone angle-bracket placeholder — observed live:
    // `<request for tool call>`. A description of a call, not a call.
    private static readonly Regex PlaceholderReplyRegex = new(
        @"\A\s*<[^<>\r\n]{1,80}>\s*\z",
        Flags);

    // A reply OPENING as the envelope's text field: `{"text":"…`. When the
    // JSON never closes, the model was cut off mid-answer and the answer text
    // itself is recoverable.
    private static readonly Regex TruncatedEnvelopeTextOpenRegex = new(
        @"\A\{\s*""text""\s*:\s*""",
        Flags);

    // The tail of a text-only envelope: the closing quote of "text", an
    // optional EMPTY toolCalls array, and the closing brace at end of input.
    private static readonly Regex TextOnlyEnvelopeTailRegex = new(
        @"""\s*(?:,\s*""tool_?calls""\s*:\s*\[\s*\]\s*)?\}\z",
        Flags | RegexOptions.RightToLeft);

    /// <summary>
    /// Decodes the body of a JSON string literal (no surrounding quotes),
    /// leaving any stray unescaped quote as literal content.
    /// </summary>
    private static string DecodeJsonStringBody(string span)
    {
        var builder = new StringBuilder(span.Length);
        var escaped = false;
        for (var i = 0; i < span.Length; i++)
        {
            var c = span[i];
            if (!escaped)
            {
                if (c == '\\')
                {
                    escaped = true;
                }
                else
                {
                    builder.Append(c);
                }

                continue;
            }

            escaped = false;
            switch (c)
            {
                case 'n': builder.Append('\n'); break;
                case 't': builder.Append('\t'); break;
                case 'r': builder.Append('\r'); break;
                case 'b': builder.Append('\b'); break;
                case 'f': builder.Append('\f'); break;
                case 'u':
                    if (i + 4 < span.Length
                        && ushort.TryParse(
                            span.AsSpan(i + 1, 4),
                            NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture,
                            out var code))
                    {
                        builder.Append((char)code);
                        i += 4;
                    }
                    else
                    {
                        builder.Append('u');
                    }

                    break;
                default: builder.Append(c); break;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// True when the reply is nothing but mangled-envelope debris — bare call
    /// ids like <c>call_11</c>, or a lone <c>&lt;request for tool call&gt;</c>
    /// placeholder — which is never an answer and never renderable.
    /// </summary>
    public static bool IsToolSyntaxDebris(string? text)
        => !string.IsNullOrWhiteSpace(text)
           && (CallIdDebrisRegex.IsMatch(text)
               || PlaceholderReplyRegex.IsMatch(text)
               || HarnessErrorEchoRegex.IsMatch(text)
               || DegenerateToolCallKeyRegex.IsMatch(text));

    /// <summary>
    /// True when the WHOLE reply is just a tool name the request declared
    /// (<c>code.run_csharp</c>, optionally wrapped in markdown emphasis) with
    /// no arguments anywhere — observed live as an entire final answer. Naming
    /// a tool runs nothing; there is no input to salvage, so this must
    /// reprompt rather than render.
    /// </summary>
    public static bool IsBareToolNameReply(string? text, IReadOnlyList<ChatToolDefinition> tools)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var stripped = text.Trim().Trim('*', '_', '`', '#', '>', '-', ' ', '\t', '\r', '\n', ':', '.');
        return stripped.Length > 0
               && !stripped.Contains(' ', StringComparison.Ordinal)
               && FindTool(tools, stripped) is not null;
    }

    /// <summary>
    /// True when the reply echoes the request payload skeleton back at us —
    /// all three of <c>"systemPrompt"</c>, <c>"messages"</c>, and
    /// <c>"tools"</c>. That is our protocol scaffolding, never an answer.
    /// </summary>
    public static bool ContainsRequestSkeletonEcho(string? text)
        => !string.IsNullOrWhiteSpace(text)
           && text.Contains("\"systemPrompt\"", StringComparison.OrdinalIgnoreCase)
           && text.Contains("\"messages\"", StringComparison.OrdinalIgnoreCase)
           && text.Contains("\"tools\"", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Recovers the answer from an envelope whose JSON was cut off INSIDE the
    /// "text" string — <c>{"text":"…long answer</c> with no closing quote. The
    /// model finished its thought but not its JSON; the decoded string content
    /// IS the answer. Deliberately narrow: the reply must open as the text
    /// field and run to end-of-input while still inside the string (a closed
    /// string with malformed JSON after it stays on the reprompt path, because
    /// a truncated toolCalls array must not be misread as a final answer), and
    /// the decoded text must be substantial enough to be an answer.
    /// </summary>
    public static string? RecoverTruncatedEnvelopeText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.TrimStart().TrimEnd();
        var open = TruncatedEnvelopeTextOpenRegex.Match(trimmed);
        if (!open.Success)
        {
            return null;
        }

        // A COMPLETE text-only envelope that still will not parse, because the
        // model wrote unescaped quotes inside its own answer:
        //   {"text":"… a genuine "you taught X but AI now says Y" conflict …"}
        // The document shape is unambiguous — an opening text field and a tail
        // of nothing but an empty toolCalls array and the closing brace — so
        // the whole span between them is the answer. Requiring an EMPTY
        // toolCalls keeps a tool request from being mistaken for a final reply.
        var tail = TextOnlyEnvelopeTailRegex.Match(trimmed);
        if (tail.Success && tail.Index >= open.Length)
        {
            var span = trimmed[open.Length..tail.Index];
            var decoded = DecodeJsonStringBody(span).Trim();
            if (decoded.Length >= 40)
            {
                return decoded;
            }
        }

        var builder = new StringBuilder();
        var escaped = false;
        var closed = false;
        for (var i = open.Length; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (escaped)
            {
                escaped = false;
                switch (c)
                {
                    case 'n': builder.Append('\n'); break;
                    case 't': builder.Append('\t'); break;
                    case 'r': builder.Append('\r'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'u':
                        if (i + 4 < trimmed.Length
                            && ushort.TryParse(
                                trimmed.AsSpan(i + 1, 4),
                                NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture,
                                out var code))
                        {
                            builder.Append((char)code);
                            i += 4;
                        }
                        else
                        {
                            builder.Append('u');
                        }

                        break;
                    default: builder.Append(c); break;
                }
            }
            else if (c == '\\')
            {
                escaped = true;
            }
            else if (c == '"')
            {
                // Only a quote followed by valid JSON continuation (`,` or `}`
                // or end-of-input) actually terminates the string. Observed
                // live: the model escaped its JSON properly for ~2000 chars,
                // then quoted a word without escaping and degraded to raw
                // prose — that quote is CONTENT, not a terminator.
                var next = i + 1;
                while (next < trimmed.Length && char.IsWhiteSpace(trimmed[next]))
                {
                    next++;
                }

                if (next >= trimmed.Length)
                {
                    // Quote at end-of-input: the model finished its answer and
                    // stopped without closing the envelope. Recoverable.
                    break;
                }

                if (trimmed[next] is ',' or '}')
                {
                    closed = true;
                    break;
                }

                builder.Append(c);
            }
            else
            {
                builder.Append(c);
            }
        }

        if (closed)
        {
            return null;
        }

        var text = builder.ToString().Trim();
        return text.Length >= 40 ? text : null;
    }

    /// <summary>
    /// Reads the JSON object a prose tool-call announcement may be followed by.
    /// Only whitespace, bold markers, an "arguments:" label and an opening code
    /// fence may separate them, so unrelated JSON later in the reply is never
    /// captured.
    /// </summary>
    private static JsonObject? TryReadTrailingArguments(string text, int start, out int end)
    {
        end = start;
        if (start >= text.Length)
        {
            return null;
        }

        var tail = text[start..];
        var preamble = ArgumentsPreambleRegex.Match(tail);
        var offset = preamble.Success ? preamble.Length : 0;
        if (offset >= tail.Length || tail[offset] != '{')
        {
            return null;
        }

        var span = ExtractBalancedObject(tail, offset);
        if (span is null)
        {
            return null;
        }

        JsonObject? parsed;
        try
        {
            parsed = JsonNode.Parse(span) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        if (parsed is null)
        {
            return null;
        }

        var consumed = offset + span.Length;
        // Swallow a closing code fence so it doesn't survive as stray text.
        var closing = Regex.Match(tail[consumed..], @"\A[ \t\r\n]*`{3,}", RegexOptions.CultureInvariant);
        if (closing.Success)
        {
            consumed += closing.Length;
        }

        end = start + consumed;
        return parsed;
    }

    private static string? ExtractBalancedObject(string text, int start)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0) return text[start..(i + 1)];
                    break;
            }
        }

        return null;
    }

    /// <summary>
    /// Like <see cref="ExtractBalancedObject"/> but for any JSON value opener —
    /// an object or an array. Combined depth is sound because a well-formed
    /// JSON value nests both bracket kinds properly.
    /// </summary>
    private static string? ExtractBalancedValue(string text, int start)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"': inString = true; break;
                case '{' or '[': depth++; break;
                case '}' or ']':
                    depth--;
                    if (depth == 0) return text[start..(i + 1)];
                    break;
            }
        }

        return null;
    }

    private static ChatToolDefinition? FindTool(IReadOnlyList<ChatToolDefinition> tools, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var trimmed = name.Trim();
        return tools.FirstOrDefault(t => string.Equals(t.Name, trimmed, StringComparison.Ordinal))
               ?? tools.FirstOrDefault(t => string.Equals(t.Name, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// XML parameter bodies are untyped text. Prefer the tool's declared schema
    /// type; fall back to a conservative literal sniff so `{"a":1}` and `true`
    /// don't arrive as strings while free text (code, prose) stays a string.
    /// </summary>
    private static JsonNode? CoerceParameter(string rawValue, JsonObject? schema, string parameterName)
    {
        var value = rawValue.Trim('\r', '\n');
        var declaredType = schema?["properties"]?[parameterName]?["type"]?.GetValue<string>();

        switch (declaredType?.ToLowerInvariant())
        {
            case "string":
                return JsonValue.Create(value);
            case "integer":
            case "number":
                return TryParseNumber(value.Trim()) ?? JsonValue.Create(value);
            case "boolean":
                return bool.TryParse(value.Trim(), out var declaredBool)
                    ? JsonValue.Create(declaredBool)
                    : JsonValue.Create(value);
            case "object":
            case "array":
                return TryParseNode(value.Trim()) ?? JsonValue.Create(value);
        }

        var sniffed = value.Trim();
        if (sniffed.StartsWith('{') || sniffed.StartsWith('['))
        {
            return TryParseNode(sniffed) ?? JsonValue.Create(value);
        }

        if (string.Equals(sniffed, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sniffed, "false", StringComparison.OrdinalIgnoreCase))
        {
            return JsonValue.Create(bool.Parse(sniffed));
        }

        return TryParseNumber(sniffed) ?? JsonValue.Create(value);
    }

    private static JsonNode? TryParseNumber(string value)
    {
        if (value.Length == 0)
        {
            return null;
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var asLong))
        {
            return JsonValue.Create(asLong);
        }

        // Only a bare decimal literal counts; "1 apple" must stay a string.
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var asDouble)
            ? JsonValue.Create(asDouble)
            : null;
    }

    private static JsonNode? TryParseNode(string value)
    {
        try
        {
            return JsonNode.Parse(value);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string RemoveScaffoldingLines(string text)
    {
        if (!ScaffoldingLinePrefixes.Any(prefix => text.Contains(prefix[..Math.Min(12, prefix.Length)], StringComparison.OrdinalIgnoreCase)))
        {
            return text;
        }

        var lines = text.Split('\n');
        var kept = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            if (IsScaffoldingLine(line))
            {
                continue;
            }

            kept.Add(line);
        }

        return string.Join('\n', kept);
    }

    private static bool IsScaffoldingLine(string line)
    {
        var trimmed = line.Trim().TrimStart('>', '*', '_', '`', '#', '-', ' ');
        if (trimmed.Length == 0 || trimmed.Length > 200)
        {
            return false;
        }

        return ScaffoldingLinePrefixes.Any(prefix =>
            trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static string CollapseBlankRuns(string text)
    {
        var normalized = text.Replace("\r\n", "\n");
        while (normalized.Contains("\n\n\n", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("\n\n\n", "\n\n");
        }

        return normalized.Trim();
    }
}
