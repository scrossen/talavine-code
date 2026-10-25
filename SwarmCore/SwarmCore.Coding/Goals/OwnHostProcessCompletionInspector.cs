using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Continual;
using SwarmCore.Coding.Native;
using SwarmCore.Missions;

namespace SwarmCore.Coding.Goals;

/// <summary>
/// Settles a dispatched own-host process call whose completion the host itself observed.
/// <para>
/// A goal receipt for <c>shell.exec</c> is written from the host tool bridge's own completion
/// envelope (<see cref="ShellExecTool.Output"/>: exit code, captured output, timed-out flag). The
/// bridge marks a non-zero exit as an error, and outside a mission fence the tool never claims
/// <c>LocalProcessCompleted</c>, so <see cref="MissionTransitions.NeedsInspection"/> treats every
/// failed command as an unverified physical operation. That hold protects operations whose
/// completion the host did not see (a lost transport, a timeout, a crash). For an own-host
/// process the host waited for exit before it produced the envelope, so the outcome of the
/// operation is exactly that envelope: as settled as a zero exit, which never holds a goal.
/// </para>
/// <para>
/// The proof rests only on host-owned facts: the receipt names this host's bridge server and the
/// shell tool's own identity, the stored result is the bridge envelope in the transport's own
/// encoding, and the envelope reports that the process was not killed on timeout. Model text,
/// error strings and exit codes of foreign or unidentified tools establish nothing. The inspector
/// never reads the command, never replays it and never claims the process had no effect.
/// </para>
/// </summary>
public sealed class OwnHostProcessCompletionInspector(ICapabilityCatalog? catalog = null) : ICodingGoalOutcomeInspector
{
    public const string InspectorIdentity = "swarmcore.coding.own-host-process-completion/v1";

    public string Identity => InspectorIdentity;

    public Task<CodingGoalOutcomeInspection?> InspectAsync(ContinualObjectiveSnapshot goal, ContinualToolReceipt target,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Inspect(target));
    }

    /// <summary>Null means this inspector cannot establish the outcome; the receipt stays unresolved.</summary>
    public CodingGoalOutcomeInspection? Inspect(ContinualToolReceipt target)
    {
        // Only a completed, dispatched call carries an envelope. The side effect must be the one the host
        // recorded for its own shell tool; an external send or an unclassified call is never settled here.
        if (target.State != ContinualToolReceiptState.Completed || !target.WasDispatched
            || target.SideEffect != ContinualSideEffectClass.Exec || string.IsNullOrEmpty(target.Result)) return null;
        if (!OwnHostToolReceipts.TryIdentify(target.ToolName, target.InputJson, ShellExecTool.ToolId, catalog, out var transport)) return null;
        if (!OwnHostToolReceipts.TryReadResultText(target.Result, transport, out var text)) return null;
        if (!TryReadEnvelope(text, out var exitCode, out var stdoutLength, out var stderrLength, out var timedOut)) return null;
        // A killed process was interrupted mid-work. -1 is the tool's marker for an exit it never observed; a real
        // process that happens to exit with -1 (possible on Windows) is deliberately left held rather than guessed.
        if (timedOut || exitCode == -1) return null;
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target.Result)));
        return new(ContinualReceiptResolutionOutcome.KnownOutcome,
            $"Own-host process completion: the host's {ShellExecTool.ToolId} tool returned its completion envelope for this exact call "
            + $"through {transport.Description} (exit code {exitCode}, timed out: false, recorded stdout {stdoutLength} characters, "
            + $"recorded stderr {stderrLength} characters; receipt result SHA-256 {digest}). The host waited for the local process to exit "
            + "before recording this result, so the operation's outcome is this envelope and the original receipt retains the recorded output. "
            + "No action was replayed and no claim is made about the command's effects beyond the recorded exit.");
    }

    /// <summary>The bridge serializes <see cref="ShellExecTool.Output"/> with exactly these four members; anything else is not its envelope.</summary>
    internal static bool TryReadEnvelope(string text, out int exitCode, out int stdoutLength, out int stderrLength, out bool timedOut)
    {
        exitCode = 0; stdoutLength = 0; stderrLength = 0; timedOut = false;
        JsonObject? envelope;
        try { envelope = JsonNode.Parse(text) as JsonObject; }
        catch (JsonException) { return false; }
        if (envelope is null || envelope.Count != 4) return false;
        if (envelope[nameof(ShellExecTool.Output.ExitCode)] is not JsonValue exit || !exit.TryGetValue(out exitCode)
            || envelope[nameof(ShellExecTool.Output.Stdout)] is not JsonValue stdout || !stdout.TryGetValue<string>(out var stdoutText)
            || envelope[nameof(ShellExecTool.Output.Stderr)] is not JsonValue stderr || !stderr.TryGetValue<string>(out var stderrText)
            || envelope[nameof(ShellExecTool.Output.TimedOut)] is not JsonValue timed || !timed.TryGetValue(out timedOut))
            return false;
        stdoutLength = stdoutText.Length;
        stderrLength = stderrText.Length;
        return true;
    }
}
