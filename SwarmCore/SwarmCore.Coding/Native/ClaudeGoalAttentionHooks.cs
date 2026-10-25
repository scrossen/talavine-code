using System.IO.Pipes;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using SwarmCore.Abstractions.Processes;
using SwarmCore.Agent.Communication;
using SwarmCore.Coding.Routing;

namespace SwarmCore.Coding.Native;

/// <summary>Run-scoped attention checkpoints. The helper has no profile, vault or tool authority.</summary>
public sealed class ClaudeGoalAttentionHooks : IAsyncDisposable
{
    private const string Prefix = "swarm-goal-hook-";
    private const int MaxFrameBytes = 128 * 1024;
    internal const int MaxOutputCharacters = 9900;
    internal const int MaxContextCharacters = 9500;
    private const int MaxInputCharacters = 256 * 1024;
    private const string Protocol = "swarmcode-goal-hook-v1";
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly string[] Events = ["PostToolBatch", "PostToolUse", "PostToolUseFailure", "Stop"];
    private readonly NativeLaneTurn _turn;
    private readonly string _workerId;
    private readonly Func<GoalDelivery, Task> _accepted;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string? _privateDirectory;
    private readonly Task _listener;
    private string? _sessionId;
    private bool _wrongSession;
    private readonly object _sessionGate = new();
    private int _disposed;

    internal string Endpoint { get; }
    public string SettingsPath { get; }
    internal Action<string>? CheckpointObserved { get; set; }

    private ClaudeGoalAttentionHooks(NativeLaneTurn turn, string workerId, string? sessionId,
        McpServerLaunch launch, Func<GoalDelivery, Task> accepted)
    {
        _turn = turn; _workerId = workerId; _sessionId = sessionId; _accepted = accepted;
        (Endpoint, _privateDirectory) = LocalPipeEndpoint.Create(Prefix);
        try { SettingsPath = NativeLaneSupport.WriteTempConfig("claude-goal-hooks", BuildSettings(launch, Endpoint)); }
        catch { if (_privateDirectory is not null) Directory.Delete(_privateDirectory, true); throw; }
        _listener = Task.Run(ListenAsync);
    }

    internal static ClaudeGoalAttentionHooks? Create(NativeLaneTurn turn, string workerId, string? sessionId,
        McpServerLaunch? launch, Func<GoalDelivery, Task> accepted)
    {
        if (turn.GoalMailbox is null || turn.GoalId is null || !CanLaunchHelper(launch)) return null;
        return new(turn, workerId, sessionId, launch!, accepted);
    }

    internal static bool CanLaunchHelper(McpServerLaunch? launch) => launch is { Command.Length: > 0 }
        && string.IsNullOrWhiteSpace(launch.Url) && launch.Arguments.Contains("--mcp-server", StringComparer.Ordinal);

    internal static string BuildSettings(McpServerLaunch launch, string endpoint)
    {
        var prefix = launch.Arguments.TakeWhile(a => a != "--mcp-server").ToList();
        prefix.Add("--goal-message-hook"); prefix.Add(endpoint);
        var hooks = new JsonObject();
        foreach (var name in Events)
            hooks[name] = new JsonArray(new JsonObject { ["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "command", ["command"] = launch.Command,
                ["args"] = new JsonArray(prefix.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()), ["timeout"] = 10,
            }) });
        return new JsonObject { ["hooks"] = hooks }.ToJsonString(Json);
    }

    /// <summary>Only the session observed on the owned CLI stream can admit a callback.</summary>
    internal void BindSession(string sessionId)
    {
        lock (_sessionGate)
        {
            if (_sessionId is null) _sessionId = sessionId;
            else if (_sessionId != sessionId) _wrongSession = true;
        }
    }

    // This protocol is scoped to the known major and settings schema. Older/unrecognized clients retain
    // the full initial-prompt path; version or system:init alone never establishes message delivery.
    internal static bool SupportsVersion(string text)
        => Version.TryParse(text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), out var version)
            && version.Major == 2 && version >= new Version(2, 1, 302);

    internal static async Task<bool> IsSupportedAsync(string executable, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var psi = NativeLaneSupport.TurnStartInfo(executable, ["--version"], Path.GetTempPath());
            NativeLaneSupport.ScrubApiKeys(psi.EnvironmentVariables);
            using var process = ChildProcessLauncher.Start(NativeLaneSupport.ForLaunch(psi));
            if (process is null) return false;
            using var stop = timeout.Token.Register(() => { try { process.Kill(true); } catch { } });
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await error.ConfigureAwait(false);
            return process.ExitCode == 0 && SupportsVersion(await output.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    internal static async Task<bool> IsHelperSupportedAsync(McpServerLaunch? launch, CancellationToken ct)
    {
        if (!CanLaunchHelper(launch)) return false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var arguments = launch!.Arguments.TakeWhile(a => a != "--mcp-server").Concat(["--goal-message-hook", "protocol"]);
            var psi = NativeLaneSupport.TurnStartInfo(launch.Command!, arguments.ToList(), Path.GetTempPath());
            NativeLaneSupport.ScrubApiKeys(psi.EnvironmentVariables);
            using var process = ChildProcessLauncher.Start(NativeLaneSupport.ForLaunch(psi));
            if (process is null) return false;
            using var stop = timeout.Token.Register(() => { try { process.Kill(true); } catch { } });
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await error.ConfigureAwait(false);
            return process.ExitCode == 0 && (await output.ConfigureAwait(false)).Trim() == Protocol;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    private async Task ListenAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(Endpoint, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_lifetime.Token).ConfigureAwait(false);
                await ServeAsync(pipe).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { break; }
            catch { if (!_lifetime.IsCancellationRequested) await Task.Delay(50, _lifetime.Token).ConfigureAwait(false); }
        }
    }

    private async Task ServeAsync(Stream pipe)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        GoalDelivery? delivery = null;
        var outputAttempted = false;
        var settled = false;
        try
        {
            var request = JsonNode.Parse(await LocalPipeEndpoint.ReadFrameAsync(pipe, MaxFrameBytes, timeout.Token).ConfigureAwait(false)) as JsonObject;
            var name = (string?)request?["hook_event_name"];
            bool bound;
            lock (_sessionGate) bound = !_wrongSession && _sessionId is not null && _sessionId == (string?)request?["session_id"];
            if (!bound || !Events.Contains(name, StringComparer.Ordinal) || request?["agent_id"] is not null
                || !SameDirectory((string?)request?["cwd"], _turn.RepositoryRoot))
            { await SendAsync(pipe, new JsonObject(), timeout.Token).ConfigureAwait(false); return; }
            CheckpointObserved?.Invoke(name!);
            delivery = await GoalMailboxBoundary.BeginAsync(_turn.GoalMailbox, _turn.GoalId,
                _turn.ConversationId, _workerId, MaxContextCharacters, timeout.Token, expectedLeaseToken: _turn.GoalMessageLeaseToken).ConfigureAwait(false);
            if (delivery is null) { await SendAsync(pipe, new JsonObject(), timeout.Token).ConfigureAwait(false); return; }
            var output = new JsonObject { ["hookSpecificOutput"] = new JsonObject
            { ["hookEventName"] = name, ["additionalContext"] = delivery.ContextText } }.ToJsonString(Json);
            if (output.Length > MaxOutputCharacters)
            {
                await RecordAsync(GoalDeliveryState.Received, "Complete message exceeds this hook's output budget; waiting for the next goal turn.").ConfigureAwait(false);
                settled = true;
                await SendAsync(pipe, new JsonObject(), timeout.Token).ConfigureAwait(false); return;
            }
            outputAttempted = true;
            await SendAsync(pipe, new JsonObject { ["deliveryId"] = delivery.Id, ["output"] = output }, timeout.Token).ConfigureAwait(false);
            var ack = JsonNode.Parse(await LocalPipeEndpoint.ReadFrameAsync(pipe, MaxFrameBytes, timeout.Token).ConfigureAwait(false)) as JsonObject;
            if ((string?)ack?["deliveryId"] != delivery.Id || (bool?)ack?["flushed"] != true)
                throw new InvalidDataException("Hook output was not acknowledged.");
            await RecordAsync(GoalDeliveryState.Delivered, "The bound Claude hook flushed the complete context to the CLI.").ConfigureAwait(false);
            settled = true;
            await _accepted(delivery).ConfigureAwait(false);
            await SendAsync(pipe, new JsonObject { ["recorded"] = true }, timeout.Token).ConfigureAwait(false);
        }
        catch { /* The helper cannot interrupt work; a claimed batch still gets an explicit conservative outcome. */ }
        finally
        {
            if (delivery is not null && !settled)
                await RecordAsync(outputAttempted ? GoalDeliveryState.Uncertain : GoalDeliveryState.Received,
                    outputAttempted ? "Hook context admission was not acknowledged; inspect before retrying." : "No hook context was sent.").ConfigureAwait(false);
        }
        Task RecordAsync(GoalDeliveryState state, string detail) => GoalMailboxBoundary.TryRecordAsync(_turn.GoalMailbox!, delivery!, state, detail);
    }

    private static bool SameDirectory(string? left, string right)
    {
        try { return left is not null && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal); }
        catch { return false; }
    }

    private static Task SendAsync(Stream pipe, JsonObject value, CancellationToken ct)
        => LocalPipeEndpoint.WriteFrameAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(value, Json), MaxFrameBytes, ct);

    /// <summary>Early-exit CLI mode: bounded stdin and current-user IPC only, no application bootstrap.</summary>
    public static async Task<int> RunHelperAsync(string endpoint, TextReader input, TextWriter output, CancellationToken ct = default)
    {
        if (endpoint == "protocol") { await output.WriteLineAsync(Protocol).ConfigureAwait(false); return 0; }
        if (!LocalPipeEndpoint.IsValid(endpoint, Prefix)) return 0;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var text = new StringBuilder();
            var buffer = new char[4096];
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (text.Length + read > MaxInputCharacters) return 0;
                text.Append(buffer, 0, read);
            }
            var value = JsonNode.Parse(text.ToString()) as JsonObject;
            if (value is null) return 0;
            // Never send tool arguments/results or any other hook payload back to the host.
            var request = new JsonObject();
            foreach (var key in new[] { "session_id", "cwd", "hook_event_name", "agent_id" })
                if (value[key] is { } field) request[key] = field.DeepClone();
            using var pipe = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(3000, timeout.Token).ConfigureAwait(false);
            await SendAsync(pipe, request, timeout.Token).ConfigureAwait(false);
            var response = JsonNode.Parse(await LocalPipeEndpoint.ReadFrameAsync(pipe, MaxFrameBytes, timeout.Token).ConfigureAwait(false)) as JsonObject;
            if ((string?)response?["output"] is not { Length: > 0 } json || json.Length > MaxOutputCharacters
                || (string?)response["deliveryId"] is not { Length: > 0 } deliveryId) return 0;
            await output.WriteLineAsync(json.AsMemory(), timeout.Token).ConfigureAwait(false);
            await output.FlushAsync(timeout.Token).ConfigureAwait(false);
            await SendAsync(pipe, new JsonObject { ["deliveryId"] = deliveryId, ["flushed"] = true }, timeout.Token).ConfigureAwait(false);
            await LocalPipeEndpoint.ReadFrameAsync(pipe, MaxFrameBytes, timeout.Token).ConfigureAwait(false);
        }
        catch { /* Optional attention must never block/kill a native turn or print an invalid hook response. */ }
        return 0;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try { await _listener.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _lifetime.Dispose();
        try { File.Delete(SettingsPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        if (_privateDirectory is not null) { try { Directory.Delete(_privateDirectory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
