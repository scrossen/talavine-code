using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Routing;

namespace SwarmCode.Ui;

public enum TurnOutcome
{
    None,
    Completed,
    Errored,
    Exhausted,
    Abandoned,
}

public sealed record RendererOptions
{
    /// <summary>Print every event as one JSON line on stdout instead of pretty rendering.</summary>
    public bool Json { get; init; }

    /// <summary>Show reasoning chunks in dim gray.</summary>
    public bool ShowThinking { get; init; }

    /// <summary>
    /// Non-interactive (-p) rendering: stream nothing to stdout except the final answer;
    /// tool and lane diagnostics go to stderr.
    /// </summary>
    public bool Quiet { get; init; }
}

/// <summary>Renders <see cref="TurnEvent"/>s to the terminal with plain ANSI colors (honors NO_COLOR).</summary>
public sealed class ConsoleRenderer
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly RendererOptions _options;
    private readonly bool _color;
    private readonly TextWriter _out;
    private readonly TextWriter _diag;
    private readonly StringBuilder _text = new();
    private readonly Dictionary<string, string> _toolNames = new();
    private bool _streamedText;
    private bool _atLineStart = true;
    private bool _inReasoning;

    public ConsoleRenderer(RendererOptions options, TextWriter? output = null, TextWriter? diagnostics = null)
    {
        _options = options;
        _out = output ?? Console.Out;
        _diag = diagnostics ?? (options.Quiet && !options.Json ? Console.Error : Console.Out);
        _color = output is null && diagnostics is null && !options.Json
                 && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"))
                 && !(options.Quiet ? Console.IsErrorRedirected : Console.IsOutputRedirected);
    }

    public TurnOutcome Outcome { get; private set; }

    /// <summary>Tokens the providers reported for the current turn (null until a report arrives).</summary>
    public long? TurnTokens { get; private set; }

    /// <summary>Tokens and cost reported since this renderer was created (null until a report arrives).</summary>
    public long? SessionTokens { get; private set; }
    public decimal? SessionCostUsd { get; private set; }

    /// <summary>The current candidate answer. Quiet output waits until acceptance settles.</summary>
    public string? FinalText { get; private set; }

    /// <summary>
    /// A plan-mode turn ends with the proposed plan and no acceptance event (nothing changed, so nothing is verified).
    /// Called by the turn runner after such a stream: the delivered plan counts as completion, and quiet (-p) output
    /// prints it now because no acceptance event will.
    /// </summary>
    public void FinishWithoutAcceptance()
    {
        if (Outcome != TurnOutcome.None || FinalText is null) return;
        Outcome = TurnOutcome.Completed;
        if (_options.Json) return;
        if (_options.Quiet) _out.WriteLine(FinalText);
    }

    /// <summary>A goal's buffered answer becomes deliverable only after independent goal verification.</summary>
    public void FinishVerifiedGoal()
    {
        Outcome = TurnOutcome.Completed;
        if (_options.Quiet && !_options.Json && FinalText is not null) _out.WriteLine(FinalText);
    }

    public void Reset()
    {
        Outcome = TurnOutcome.None;
        FinalText = null;
        TurnTokens = null;
        _text.Clear();
        _toolNames.Clear();
        _streamedText = false;
        _inReasoning = false;
    }

    public void Render(TurnEvent evt)
    {
        if (evt is TurnUsageReported usage)
        {
            // Providers report deltas; native and API lanes alike. Kept for /cost and /status.
            TurnTokens = (TurnTokens ?? 0) + usage.InputTokens + usage.OutputTokens;
            SessionTokens = (SessionTokens ?? 0) + usage.InputTokens + usage.OutputTokens;
            if (usage.CostUsd is { } cost) SessionCostUsd = (SessionCostUsd ?? 0) + cost;
        }
        if (_options.Json)
        {
            RenderJson(evt);
            TrackOutcome(evt);
            return;
        }

        switch (evt)
        {
            case TextChunk chunk:
                _text.Append(chunk.Delta);
                if (_options.Quiet) break;
                EndReasoning();
                _out.Write(chunk.Delta);
                _streamedText = true;
                if (chunk.Delta.Length > 0) _atLineStart = chunk.Delta[^1] == '\n';
                break;

            case ReasoningChunk reasoning:
                if (!_options.ShowThinking) break;
                BeginDiagLine();
                _inReasoning = true;
                _diag.Write(Paint(reasoning.Delta, Dim));
                if (reasoning.Delta.Length > 0) _atLineStart = reasoning.Delta[^1] == '\n';
                break;

            case ToolCallStarted tool:
                EndReasoning();
                BeginDiagLine();
                _toolNames[tool.ToolCallId] = tool.ToolName;
                _diag.WriteLine(Paint($"▸ {tool.ToolName} {Truncate(tool.Input.ToJsonString(), 160)}", Dim));
                _atLineStart = true;
                break;

            case ToolCallCompleted done:
                BeginDiagLine();
                var ms = (long)done.Elapsed.TotalMilliseconds;
                if (done.IsError)
                {
                    _diag.WriteLine(Paint($"✗ {done.ToolName} ({ms} ms) {Truncate(OneLine(done.Result), 200)}", Red));
                }
                else
                {
                    _diag.WriteLine(Paint($"✓ {done.ToolName} ({ms} ms)", Dim));
                }
                _atLineStart = true;
                break;

            case CodingLaneSelected selected:
                BeginDiagLine();
                _diag.WriteLine(Paint(
                    $"[lane: {selected.Lane.Name} → {selected.Lane.Provider}/{selected.Lane.Model}]", Cyan));
                _atLineStart = true;
                break;

            case CodingLaneHandOff handOff:
                BeginDiagLine();
                _diag.WriteLine(Paint($"[hand-off: {handOff.From.Name} → {handOff.To.Name} — {handOff.Reason}]", Yellow));
                _atLineStart = true;
                break;

            case CodingLaneSkipped skipped:
                BeginDiagLine();
                _diag.WriteLine(Paint($"[skipping lane {skipped.Lane.Name}: {skipped.Reason}]", Dim));
                _atLineStart = true;
                break;

            case CodingLaneNotice notice:
                BeginDiagLine();
                _diag.WriteLine(Paint($"[{notice.Lane}: {notice.Message}]", Dim));
                _atLineStart = true;
                break;

            case CodingLanesExhausted exhausted:
                BeginDiagLine();
                _diag.WriteLine(Paint($"[lanes exhausted: {exhausted.Reason}]", Red));
                _atLineStart = true;
                break;

            case CodingAcceptanceEvaluated acceptance:
                EndReasoning();
                BeginDiagLine();
                var status = acceptance.Result.Status switch
                {
                    CodingAcceptanceStatus.Passed => "acceptance passed",
                    CodingAcceptanceStatus.Failed => "acceptance failed",
                    _ => "completion unverified"
                };
                _diag.WriteLine(Paint($"[{status}: {acceptance.Result.Summary}]", acceptance.Result.Status == CodingAcceptanceStatus.Passed ? Cyan : Yellow));
                foreach (var check in acceptance.Result.Checks.Where(c => !c.Passed))
                    _diag.WriteLine(Paint($"  {check.Name}: exit {check.ExitCode?.ToString() ?? "unavailable"}{(check.TimedOut ? " (timed out)" : "")}", Yellow));
                if (_options.Quiet && acceptance.Result.Status != CodingAcceptanceStatus.Failed && FinalText is not null)
                    _out.WriteLine(FinalText);
                _atLineStart = true;
                break;

            case CodingTurnLimitReached limit:
                EndReasoning();
                BeginDiagLine();
                _diag.WriteLine(Paint($"[job stopped: {limit.Reason} Completion is unverified.]", Yellow));
                _atLineStart = true;
                break;

            case WorkingContextCompactionCompleted { Reason: SwarmCore.Agent.ToolUseEngine.WorkingContextCompactionReason.UserRequested } compacted:
                BeginDiagLine();
                _diag.WriteLine(Paint(compacted.Applied
                    ? $"[context compacted: {compacted.OriginalCharacters:N0} → {compacted.CompactedCharacters:N0} characters]"
                    : "[nothing to compact yet: the conversation is still short enough to send in full]", Dim));
                _atLineStart = true;
                break;

            case WorkingContextCompactionStarted:
                BeginDiagLine();
                _diag.WriteLine(Paint("[compacting context…]", Dim));
                _atLineStart = true;
                break;

            case TurnCompleted completed:
                EndReasoning();
                FinalText = string.IsNullOrEmpty(completed.FinalText) ? _text.ToString() : completed.FinalText;
                if (_options.Quiet)
                {
                    // Keep candidate answers off stdout until host acceptance has settled.
                }
                else
                {
                    if (completed.ReplacesStreamedText && _streamedText && !string.IsNullOrEmpty(completed.FinalText))
                    {
                        BeginLine();
                        _out.WriteLine(Paint("[final answer replaces the streamed text]", Dim));
                        _out.WriteLine(completed.FinalText);
                    }
                    else if (!_streamedText && !string.IsNullOrEmpty(completed.FinalText))
                    {
                        _out.WriteLine(completed.FinalText);
                    }
                    else
                    {
                        BeginLine();
                    }
                    _out.WriteLine(Paint($"({completed.Duration.TotalSeconds:F1}s)", Dim));
                }
                _atLineStart = true;
                break;

            case TurnErrored errored:
                EndReasoning();
                BeginDiagLine();
                _diag.WriteLine(Paint($"error: {errored.Message}", Red));
                if (!string.IsNullOrWhiteSpace(errored.Details))
                    _diag.WriteLine(Paint(Truncate(errored.Details, 600), Dim));
                _atLineStart = true;
                break;

            case TurnAbandoned abandoned:
                BeginDiagLine();
                _diag.WriteLine(Paint($"turn abandoned: {abandoned.Reason}", Yellow));
                _atLineStart = true;
                break;
        }

        TrackOutcome(evt);
    }

    private void TrackOutcome(TurnEvent evt)
    {
        switch (evt)
        {
            case CodingLaneSelected:
                FinalText = null;
                _text.Clear();
                _streamedText = false;
                break;
            case TurnCompleted completed:
                FinalText ??= string.IsNullOrEmpty(completed.FinalText) ? _text.ToString() : completed.FinalText;
                break;
            case CodingAcceptanceEvaluated acceptance:
                Outcome = acceptance.Result.Status == CodingAcceptanceStatus.Failed ? TurnOutcome.Errored : TurnOutcome.Completed;
                break;
            case CodingTurnLimitReached:
                Outcome = TurnOutcome.Errored;
                break;
            case TurnErrored:
                // A later accepted lane can recover; a terminal error must still override an earlier candidate answer.
                Outcome = TurnOutcome.Errored;
                break;
            case CodingLanesExhausted:
                Outcome = TurnOutcome.Exhausted;
                break;
            case TurnAbandoned:
                Outcome = TurnOutcome.Abandoned;
                break;
        }
    }

    private void RenderJson(TurnEvent evt)
    {
        try
        {
            var node = JsonSerializer.SerializeToNode(evt, evt.GetType(), JsonOptions) as JsonObject ?? new JsonObject();
            var line = new JsonObject { ["type"] = evt.GetType().Name };
            foreach (var property in node) line[property.Key] = property.Value?.DeepClone();
            _out.WriteLine(line.ToJsonString(JsonOptions));
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException or InvalidOperationException)
        {
            _out.WriteLine(new JsonObject
            {
                ["type"] = evt.GetType().Name,
                ["turnId"] = evt.TurnId,
                ["serializationError"] = ex.Message,
            }.ToJsonString(JsonOptions));
        }
    }

    // Move to a fresh line before a diagnostic when streamed text left the cursor mid-line.
    private void BeginDiagLine()
    {
        if (_inReasoning) return;
        BeginLine();
    }

    private void BeginLine()
    {
        if (!_atLineStart && !_options.Quiet)
        {
            _out.WriteLine();
            _atLineStart = true;
        }
    }

    private void EndReasoning()
    {
        if (!_inReasoning) return;
        _inReasoning = false;
        if (!_atLineStart)
        {
            _diag.WriteLine();
            _atLineStart = true;
        }
    }

    private const string Dim = "\u001b[90m";
    private const string Red = "\u001b[31m";
    private const string Yellow = "\u001b[33m";
    private const string Cyan = "\u001b[36m";

    private string Paint(string text, string code) => _color ? code + text + "\u001b[0m" : text;

    private static string OneLine(string value) => value.ReplaceLineEndings(" ");

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";
}
