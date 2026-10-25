using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SwarmCore.Agent.Procedures;

namespace SwarmCore.RemoteControl.Agent;

/// <summary>
/// Turns a verified desktop trace into parameterized procedure steps for the shared engine
/// (<see cref="IAgentProcedurePlanner"/>; the engine still re-materializes and validates every candidate against the
/// actual receipts). A recorded argument equal to exactly one per-run input becomes a binding; everything else must be a
/// durable constant the host approves (<see cref="Explain"/>): action and kind words, control types, key chords,
/// window and control name text, wait conditions and secret slot names. Typed text and set values are never constants,
/// session ids (w3, c7) and coordinates are refused, and a constant that embeds a per-run input value is refused.
/// </summary>
public sealed partial class DesktopProcedureCompiler(IReadOnlyCollection<string> secretSlots) : IAgentProcedurePlanner
{
    private const int MaxLiteralLength = 160;
    private static readonly HashSet<string> Actions = new(StringComparer.Ordinal)
    {
        "click", "double-click", "doubleclick", "right-click", "rightclick", "invoke", "toggle", "check", "uncheck",
        "select", "expand", "collapse", "set", "focus",
    };
    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal) { "type", "press", "scroll" };

    [GeneratedRegex(@"\b[wWcC]\d+\b")]
    private static partial Regex AliasToken();
    [GeneratedRegex(@"^(up|down|left|right)( \d{1,3})?$", RegexOptions.IgnoreCase)]
    private static partial Regex ScrollText();

    public Task<AgentProcedureCandidate?> CreateCandidateAsync(AgentProcedureContext context,
        AgentProcedureRunEvidence evidence, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var receipts = evidence.Receipts.Where(r => !r.IsError).ToArray();
        var candidate = Compile(receipts, context.Inputs, context.ValueEquivalence, out _);
        return Task.FromResult(candidate);
    }

    /// <summary>The literal policy as the engine's <see cref="AgentProcedureContext.AllowLiteral"/>.</summary>
    public Func<string, string, JsonNode?, bool> AllowLiteral(JsonObject inputs) =>
        (tool, pointer, value) => Explain(tool, pointer, value, inputs) is null;

    /// <summary>Compiles successful receipts, or returns null with the first reason a step cannot be saved.</summary>
    public AgentProcedureCandidate? Compile(IReadOnlyList<AgentProcedureToolReceipt> receipts, JsonObject inputs,
        Func<JsonNode?, JsonNode?, bool>? equivalence, out string? refusal)
    {
        refusal = null;
        var inputLeaves = Leaves(inputs, "").Where(p => p.Value is JsonValue v && (v.TryGetValue<string>(out _) || IsNumber(v))).ToArray();
        bool Same(JsonNode? a, JsonNode? b) => equivalence?.Invoke(a, b) ?? JsonNode.DeepEquals(a, b);
        var steps = new List<AgentProcedureStep>();
        for (var index = 0; index < receipts.Count; index++)
        {
            var receipt = receipts[index];
            if (receipt.ToolId is DesktopControlTools.ActTool or DesktopControlTools.InputTool
                && receipt.Arguments["secret"] is null && (receipt.Arguments["value"] is not null || receipt.Arguments["text"] is not null)
                && MentionsPasswordControl(receipt.Result))
            {
                refusal = $"Step {index + 1} typed into a password field without a secret slot; repeat it with secret set to a slot name.";
                return null;
            }
            var template = receipt.Arguments.DeepClone().AsObject();
            var bindings = new List<AgentProcedureBinding>();
            foreach (var (pointer, value) in Leaves(receipt.Arguments, "").Where(p => p.Value is not JsonObject and not JsonArray).ToArray())
            {
                if (value is JsonValue scalar && (scalar.TryGetValue<string>(out _) || IsNumber(scalar)))
                {
                    var matches = inputLeaves.Where(p => Same(p.Value, value)).ToArray();
                    if (matches.Length == 1)
                    {
                        bindings.Add(new(pointer, AgentProcedureValueSource.Input, matches[0].Pointer));
                        SetNull(template, pointer);
                        continue;
                    }
                    if (matches.Length > 1)
                    {
                        refusal = $"Step {index + 1} argument {pointer} equals more than one input ({string.Join(", ", matches.Select(m => m.Pointer))}); give the inputs distinct values.";
                        return null;
                    }
                }
                if (Explain(receipt.ToolId, pointer, value, inputs) is { } why)
                {
                    refusal = $"Step {index + 1} ({receipt.ToolId}) argument {pointer}: {why}";
                    return null;
                }
            }
            steps.Add(new(receipt.ToolId, template, bindings));
        }
        return new(steps);
    }

    /// <summary>Null when <paramref name="value"/> may be stored as a constant at <paramref name="pointer"/> of <paramref name="tool"/>; otherwise why not.</summary>
    public string? Explain(string tool, string pointer, JsonNode? value, JsonObject inputs)
    {
        if (value is null) return null;
        if (value is not JsonValue scalar) return "only scalar constants can be stored.";
        if (scalar.TryGetValue<bool>(out _)) return pointer is "/includeAll" or "/interactiveOnly" ? null : "this flag cannot be stored.";
        if (IsNumber(scalar))
            return tool == DesktopControlTools.InputTool && pointer == "/clicks" || tool == DesktopControlTools.InspectTool && pointer == "/maxRows" ? null
                : pointer is "/x" or "/y" ? "screen coordinates cannot be saved; act on the control by its name instead."
                : "this number must come from an input.";
        if (!scalar.TryGetValue<string>(out var text)) return "unsupported constant.";
        if (text.Length > MaxLiteralLength) return "the constant is too long.";
        if (EmbeddedInput(text, inputs) is { } embedded)
            return $"the constant embeds the per-run input {embedded}; pass that value as its own argument so it becomes a binding.";
        string? Name(string what) => DesktopControlTools.IsWindowAlias(text.Trim()) || DesktopControlTools.IsControlAlias(text.Trim())
            ? $"'{text}' is a session id; name the {what} by its visible text instead."
            : long.TryParse(text, out _) ? $"'{text}' is a native handle; name the {what} by its visible text instead."
            : string.IsNullOrWhiteSpace(text) ? "an empty name cannot be stored." : null;
        string? Type() => DesktopControlTools.ControlTypes.Contains(DesktopControlTools.NormalizeType(text) ?? "") ? null : $"'{text}' is not a control type.";
        string? Slot() => secretSlots.Contains(text, StringComparer.Ordinal) ? null : $"'{text}' is not a secret slot this host provides.";
        string? Wait() => AliasToken().IsMatch(text) ? "the condition names a session id; use visible names."
            : RemoteCommandParser.Parse(DesktopControlTools.WaitText(text)).Kind == CommandKind.WaitFor ? null : "the condition is not a wait for sentence.";
        return (tool, pointer) switch
        {
            (DesktopControlTools.InspectTool, "/window") => Name("window"),
            (DesktopControlTools.InspectTool, "/nameContains") => null,
            (DesktopControlTools.InspectTool, "/controlType") => Type(),
            (DesktopControlTools.CaptureTool, "/target") => CaptureTarget(text),
            (DesktopControlTools.ActTool, "/action") => Actions.Contains(text.Trim().ToLowerInvariant()) ? null : $"'{text}' is not an action.",
            (DesktopControlTools.ActTool, "/control") => Name("control"),
            (DesktopControlTools.ActTool, "/window") => Name("window"),
            (DesktopControlTools.ActTool, "/controlType") => Type(),
            (DesktopControlTools.ActTool, "/secret") => Slot(),
            (DesktopControlTools.ActTool, "/value") => "a set value must come from an input or a secret slot.",
            (DesktopControlTools.InputTool, "/kind") => Kinds.Contains(text.Trim().ToLowerInvariant()) ? null
                : $"'{text}' input cannot be saved; only type, press and scroll replay structurally.",
            (DesktopControlTools.InputTool, "/keys") => Chord(text),
            (DesktopControlTools.InputTool, "/text") => ScrollText().IsMatch(text.Trim()) ? null : "typed text must come from an input or a secret slot.",
            (DesktopControlTools.InputTool, "/secret") => Slot(),
            (DesktopControlTools.InputTool, "/button") => text is "left" or "right" or "middle" ? null : "unknown button.",
            (DesktopControlTools.WaitForTool, "/text") => Wait(),
            (DesktopProcedureRunner.VerifyTool, "/condition") => Wait(),
            (DesktopProcedureRunner.PersonStepTool, "/until") => Wait(),
            (DesktopProcedureRunner.PersonStepTool, "/reason") => text.Length <= 40 && text.All(ch => ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')
                ? null : "reason must be a short lowercase slug such as one-time-code.",
            (DesktopControlTools.CommandTool, "/text") => CommandText(text),
            _ => "this argument cannot be stored as a constant.",
        };
    }

    private static string? Chord(string text)
    {
        try { KeyChord.Normalize(text); return null; }
        catch (ArgumentException ex) { return ex.Message; }
    }

    private static string? CaptureTarget(string text)
    {
        var t = text.Trim();
        if (t.Equals("all", StringComparison.OrdinalIgnoreCase) || t.StartsWith("screen", StringComparison.OrdinalIgnoreCase)) return null;
        var name = t.StartsWith("window ", StringComparison.OrdinalIgnoreCase) ? t[7..].Trim() : t;
        return DesktopControlTools.IsWindowAlias(name) || long.TryParse(name, out _) ? "name the window by its visible text instead of an id." : null;
    }

    private static string? CommandText(string text)
    {
        if (AliasToken().IsMatch(text)) return "the command names a session id; use visible names.";
        var parsed = RemoteCommandParser.Parse(text);
        return parsed.Kind switch
        {
            CommandKind.Unknown => "the command is not understood.",
            CommandKind.TypeText or CommandKind.SetValue => "typed text must come from an input; use desktop.input or desktop.act so the value becomes a binding.",
            CommandKind.Click or CommandKind.MoveMouse or CommandKind.Scroll when parsed.Point is not null => "screen coordinates cannot be saved.",
            CommandKind.Quit => "quit cannot be saved.",
            _ => null,
        };
    }

    private static string? EmbeddedInput(string text, JsonObject inputs)
    {
        foreach (var (pointer, value) in Leaves(inputs, ""))
            if (value is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length >= 3
                && !string.Equals(s, text, StringComparison.Ordinal)
                && text.Contains(s.Trim(), StringComparison.OrdinalIgnoreCase))
                return pointer;
        return null;
    }

    private static bool MentionsPasswordControl(JsonNode? node) => node switch
    {
        JsonObject o => o.Any(p => p.Key.Equals("isPassword", StringComparison.OrdinalIgnoreCase)
            && p.Value is JsonValue v && v.TryGetValue<bool>(out var b) && b || MentionsPasswordControl(p.Value)),
        JsonArray a => a.Any(MentionsPasswordControl),
        _ => false,
    };

    private static bool IsNumber(JsonValue v) => v.TryGetValue<int>(out _) || v.TryGetValue<long>(out _) || v.TryGetValue<double>(out _)
        || v.TryGetValue<decimal>(out _) || v.GetValueKind() == System.Text.Json.JsonValueKind.Number;

    internal static IEnumerable<(string Pointer, JsonNode? Value)> Leaves(JsonNode? node, string pointer)
    {
        yield return (pointer, node);
        if (node is JsonObject o)
            foreach (var p in o)
                foreach (var c in Leaves(p.Value, pointer + "/" + p.Key.Replace("~", "~0").Replace("/", "~1"))) yield return c;
        if (node is JsonArray a)
            for (var i = 0; i < a.Count; i++)
                foreach (var c in Leaves(a[i], pointer + "/" + i)) yield return c;
    }

    private static void SetNull(JsonObject root, string pointer)
    {
        var parts = pointer.TrimStart('/').Split('/').Select(p => p.Replace("~1", "/").Replace("~0", "~")).ToArray();
        JsonNode? node = root;
        for (var i = 0; i < parts.Length - 1; i++)
            node = node is JsonObject o ? o[parts[i]] : node is JsonArray a ? a[int.Parse(parts[i])] : null;
        if (node is JsonObject obj) obj[parts[^1]] = null;
        else if (node is JsonArray arr) arr[int.Parse(parts[^1])] = null;
    }
}
