using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace SwarmCore.Coding.Extensions;

/// <summary>What one hook process did.</summary>
public sealed record CodingHookExecution(
    CodingHook Hook, int? ExitCode, bool TimedOut, string Stdout, string Stderr, bool OutputTruncated, string? StartError, TimeSpan Duration);

/// <summary>The combined result of the hooks that ran for one event.</summary>
public sealed record CodingHookOutcome(bool Blocked, string? Reason, IReadOnlyList<string> Context, IReadOnlyList<string> Warnings, IReadOnlyList<CodingHookExecution> Executions)
{
    public static CodingHookOutcome None { get; } = new(false, null, [], [], []);
    public bool Ran => Executions.Count > 0;
}

/// <summary>
/// Runs hook commands. Host-neutral: the command line runs through the platform shell exactly as configured
/// (<c>cmd.exe /d /s /c "&lt;command&gt;"</c> on Windows, <c>/bin/sh -c &lt;command&gt;</c> elsewhere); event data is passed
/// only as JSON on stdin and a few <c>SWARMCODE_*</c> environment variables, never spliced into the command line.
/// Each process has a timeout (the whole process tree is killed), and stdout/stderr are capped while being drained.
/// <para>
/// Result contract (Claude Code compatible): exit 0 = success; stdout may be JSON with <c>decision: "block"</c> and
/// <c>reason</c>, <c>additionalContext</c> (also under <c>hookSpecificOutput</c>) or <c>note</c>; for UserPromptSubmit
/// plain stdout is added as context. Exit 2 blocks, with stderr as the reason. Any other exit code, a timeout or a
/// start failure is a non-blocking warning. The first block stops the remaining hooks of that event.
/// </para>
/// </summary>
public sealed class CodingHookRunner(ILogger<CodingHookRunner>? logger = null)
{
    public const int MaxOutputCharacters = 16_000;
    public const int MaxReasonCharacters = 2_000;
    public const int MaxContextCharacters = 4_000;
    public const int MaxTotalContextCharacters = 8_000;
    public const int BlockExitCode = 2;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public async Task<CodingHookOutcome> RunAsync(IReadOnlyList<CodingHook> hooks, CodingHookEvent evt, string? toolId, JsonObject payload,
        string workingDirectory, CancellationToken ct = default)
    {
        var selected = hooks.Where(h => h.Event == evt && h.Matches(toolId)).ToList();
        if (selected.Count == 0) return CodingHookOutcome.None;
        payload["event"] = evt.ToString();
        if (!payload.ContainsKey("cwd")) payload["cwd"] = workingDirectory;
        var stdin = payload.ToJsonString(Json);
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SWARMCODE_HOOK_EVENT"] = evt.ToString(),
            ["SWARMCODE_PROJECT_DIR"] = workingDirectory,
        };
        if (toolId is not null) env["SWARMCODE_TOOL"] = toolId;

        var executions = new List<CodingHookExecution>();
        var context = new List<string>();
        var warnings = new List<string>();
        var contextBudget = MaxTotalContextCharacters;
        foreach (var hook in selected)
        {
            var execution = await RunOneAsync(hook, stdin, workingDirectory, env, ct).ConfigureAwait(false);
            executions.Add(execution);
            var decision = Interpret(execution);
            if (decision.Warning is { } warning)
            {
                warnings.Add(warning);
                logger?.LogWarning("{Warning}", warning);
            }
            if (decision.Context is { Length: > 0 } added && contextBudget > 0)
            {
                added = ExtensionFiles.Clip(added, Math.Min(MaxContextCharacters, contextBudget));
                context.Add(added);
                contextBudget -= added.Length;
            }
            if (decision.Block)
            {
                logger?.LogInformation("{Event} hook ({Source}) blocked {Tool}.", evt, hook.Source.Label(), toolId ?? "(no tool)");
                return new CodingHookOutcome(true, decision.Reason, context, warnings, executions);
            }
        }
        return new CodingHookOutcome(false, null, context, warnings, executions);
    }

    /// <summary>Runs one hook process with its timeout and output caps. Throws only when <paramref name="ct"/> is cancelled.</summary>
    public async Task<CodingHookExecution> RunOneAsync(CodingHook hook, string stdinJson, string workingDirectory,
        IReadOnlyDictionary<string, string>? environment = null, CancellationToken ct = default)
    {
        var watch = Stopwatch.StartNew();
        var psi = CreateStartInfo(hook.Command, workingDirectory);
        foreach (var (key, value) in environment ?? new Dictionary<string, string>()) psi.Environment[key] = value;

        SwarmCore.Abstractions.Processes.IChildProcess process;
        try
        {
            // The shared launcher: Mac Catalyst needs posix_spawn (Process.Start can report "Operation not supported").
            process = SwarmCore.Abstractions.Processes.ChildProcessLauncher.Start(psi);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning("Hook command could not start: {Error}", ex.Message);
            return new CodingHookExecution(hook, null, false, "", "", false, ex.Message, watch.Elapsed);
        }

        using (process)
        {
            var stdout = new BoundedReader(process.StandardOutput, MaxOutputCharacters);
            var stderr = new BoundedReader(process.StandardError, MaxOutputCharacters);
            var readOut = stdout.DrainAsync();
            var readErr = stderr.DrainAsync();
            // Written concurrently: a hook that never reads stdin must not block us on a full pipe.
            var writeIn = Task.Run(async () =>
            {
                try
                {
                    await process.StandardInput.WriteAsync(stdinJson).ConfigureAwait(false);
                    await process.StandardInput.FlushAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
                finally { try { process.StandardInput.Close(); } catch { /* already gone */ } }
            }, CancellationToken.None);

            var timedOut = false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(hook.Timeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                timedOut = !ct.IsCancellationRequested;
                Kill(process);
                if (!timedOut)
                {
                    await Settle(writeIn, readOut, readErr).ConfigureAwait(false);
                    throw;
                }
            }
            await Settle(writeIn, readOut, readErr).ConfigureAwait(false);
            int? exit = null;
            if (!timedOut) { try { exit = process.ExitCode; } catch (InvalidOperationException) { } }
            return new CodingHookExecution(hook, exit, timedOut, stdout.Text, stderr.Text, stdout.Truncated || stderr.Truncated, null, watch.Elapsed);
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string command, string workingDirectory)
    {
        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows())
        {
            var shell = Environment.GetEnvironmentVariable("ComSpec");
            psi = new ProcessStartInfo(string.IsNullOrWhiteSpace(shell) ? "cmd.exe" : shell)
            {
                // /s with the outer quotes: cmd strips exactly those and runs the configured text unchanged.
                Arguments = "/d /s /c \"" + command + "\"",
            };
        }
        else
        {
            psi = new ProcessStartInfo("/bin/sh");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
        }
        psi.WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : Environment.CurrentDirectory;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardInputEncoding = new UTF8Encoding(false);
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        return psi;
    }

    internal readonly record struct Decision(bool Block, string? Reason, string? Context, string? Warning);

    internal static Decision Interpret(CodingHookExecution e)
    {
        var name = e.Hook.Event + " hook (" + e.Hook.Source.Label() + ")";
        if (e.StartError is not null) return new(false, null, null, $"{name} could not start: {e.StartError}");
        if (e.TimedOut) return new(false, null, null, $"{name} timed out after {e.Hook.Timeout.TotalSeconds:0}s and was stopped; continuing.");
        if (e.ExitCode == BlockExitCode)
            return Block(e.Hook.Event, Reason(e.Stderr) ?? Reason(e.Stdout) ?? $"Blocked by a {name}.", null, name);
        if (e.ExitCode != 0)
            return new(false, null, null, $"{name} exited with code {e.ExitCode}; continuing." + (string.IsNullOrWhiteSpace(e.Stderr) ? "" : " " + ExtensionFiles.Clip(e.Stderr, 300)));

        var text = e.Stdout.Trim();
        if (text.StartsWith('{') && TryParse(text) is { } json)
        {
            var hookSpecific = json["hookSpecificOutput"] as JsonObject;
            var decision = Str(json, "decision");
            var permission = Str(hookSpecific, "permissionDecision");
            var context = Str(json, "additionalContext") ?? Str(hookSpecific, "additionalContext") ?? Str(json, "note");
            if (string.Equals(decision, "block", StringComparison.OrdinalIgnoreCase) || string.Equals(permission, "deny", StringComparison.OrdinalIgnoreCase))
                return Block(e.Hook.Event, Reason(Str(json, "reason") ?? Str(hookSpecific, "permissionDecisionReason")) ?? $"Blocked by a {name}.", context, name);
            return new(false, null, context, null);
        }
        // Plain stdout is context only for a prompt or session-start hook (Claude Code's rule); elsewhere it is the hook's own log.
        return new(false, null, AcceptsPlainContext(e.Hook.Event) && text.Length > 0 ? text : null, null);
    }

    /// <summary>Events whose hooks may block (exit 2 or <c>decision: block</c>). For every other event a block is only a warning.</summary>
    public static bool CanBlock(CodingHookEvent evt)
        => evt is CodingHookEvent.PreToolUse or CodingHookEvent.PostToolUse or CodingHookEvent.UserPromptSubmit or CodingHookEvent.Stop;

    /// <summary>Events whose hooks add plain stdout as context: UserPromptSubmit and SessionStart.</summary>
    public static bool AcceptsPlainContext(CodingHookEvent evt)
        => evt is CodingHookEvent.UserPromptSubmit or CodingHookEvent.SessionStart;

    private static Decision Block(CodingHookEvent evt, string reason, string? context, string name)
        => CanBlock(evt)
            ? new(true, reason, context, null)
            : new(false, null, context, $"{name} asked to block, which a {evt} hook cannot do; continuing. {reason}");

    private static string? Reason(string? text)
        => string.IsNullOrWhiteSpace(text) ? null : ExtensionFiles.Clip(text, MaxReasonCharacters);

    private static JsonObject? TryParse(string text)
    {
        try { return JsonNode.Parse(text) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonObject? o, string name)
    {
        if (o is null) return null;
        foreach (var (key, value) in o)
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase) && value is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                return s;
        return null;
    }

    private static void Kill(SwarmCore.Abstractions.Processes.IChildProcess process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
    }

    private static async Task Settle(params Task[] tasks)
    {
        // Readers finish when the pipes close; after a kill a grandchild could keep a pipe open, so bound the wait.
        var all = Task.WhenAll(tasks);
        await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
    }

    /// <summary>Drains a stream to the end while keeping at most <c>max</c> characters.</summary>
    private sealed class BoundedReader(StreamReader reader, int max)
    {
        private readonly StringBuilder _text = new();
        private volatile bool _truncated;

        public string Text { get { lock (_text) return _text.ToString(); } }
        public bool Truncated => _truncated;

        public async Task DrainAsync()
        {
            var buffer = new char[4096];
            try
            {
                int read;
                while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
                {
                    lock (_text)
                    {
                        var room = max - _text.Length;
                        if (room > 0) _text.Append(buffer, 0, Math.Min(room, read));
                        if (read > room) _truncated = true;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        }
    }
}
