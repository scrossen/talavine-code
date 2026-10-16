using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Abstractions.Processes;
using SwarmCore.Agent.Runtime;
using SwarmCore.Agent.ToolUseEngine;
using SwarmCore.Coding.Routing;
using SwarmCore.LlmRouting.Services;

namespace SwarmCore.Coding.Native;

/// <summary>
/// Runs the Gemini CLI as a native coding agent: <c>gemini -o stream-json --approval-mode yolo</c>
/// in the repository, one CLI session per conversation resumed across turns. Gemini's stream-json
/// events become <see cref="TurnEvent"/>s and the turn is persisted in SwarmCore's neutral
/// transcript so another lane can continue from the receipts. The MCP bridge, when configured, is
/// handed to Gemini through a generated system-settings file. A session Gemini no longer knows is
/// forgotten and the turn retried once fresh; a cancelled turn keeps its receipts.
/// </summary>
public sealed class GeminiCliNativeLaneRunner(
    IConversationStore conversations,
    IAgentSessionStore sessions,
    IOptions<CodingOptions> coding,
    ILogger<GeminiCliNativeLaneRunner> logger, SwarmCore.Secrets.ISecretVault? vault = null) : INativeLaneRunner
{
    public const string ProviderId = "GeminiCliNative";
    public const string SessionStateKey = "gemini.sessionId";

    /// <summary>Gemini's own tool names that change the workspace; the orchestrator's mutation detection knows them.</summary>
    public static readonly string[] MutatingToolNames = ["write_file", "replace", "run_shell_command"];

    /// <summary>
    /// Gemini CLI offers sandbox and policy controls, but this host has no verified settings/tool envelope
    /// equivalent to its confined Claude and Codex lanes. Until that boundary is verified on both platforms,
    /// Gemini is offered only where native agents may run unconfined (SwarmCode).
    /// </summary>
    public bool SupportsContainment(NativeLaneContainment containment) => containment == NativeLaneContainment.Bypass;

    public bool Supports(CodingLane lane) => string.Equals(lane.Provider, ProviderId, StringComparison.OrdinalIgnoreCase);
    public string ModelCatalogProvider => SubscriptionCliCatalog.GeminiCliProvider;

    public async IAsyncEnumerable<TurnEvent> RunAsync(NativeLaneTurn turn, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var turnId = Guid.NewGuid().ToString("N");
        var started = DateTime.UtcNow;
        var options = coding.Value;
        if (turn.EffectiveContainment(options.NativeAgents) != NativeLaneContainment.Bypass)
        {
            yield return new TurnErrored(turnId, "Gemini CLI confinement has not been verified for this host, so it does not run for this request.")
            { FailureCategory = LlmFailureCategory.ModelNotFound };
            yield break;
        }

        var executable = ResolveExecutable();
        if (executable is null)
        {
            yield return new TurnErrored(turnId, "Gemini CLI executable was not found on this machine; install @google/gemini-cli and sign in to use the native lane.")
            { FailureCategory = LlmFailureCategory.ModelNotFound };
            yield break;
        }

        var storedSession = await NativeLaneSupport.ReadStateAsync(conversations, logger, turn.ConversationId, SessionStateKey, ct).ConfigureAwait(false);
        var sharedHistory = await sessions.LoadAsync(turn.ConversationId, ct).ConfigureAwait(false);
        await sessions.AppendAsync(turn.ConversationId, ChatMessage.Text(ChatRole.User, turn.UserMessage), ct).ConfigureAwait(false);
        var mcp = options.NativeAgents.McpServer;
        var presentation = turn.IsContinuation
            ? await conversations.AppendContinuationTurnAsync(turn.ConversationId, turn.UserMessage, ct).ConfigureAwait(false)
            : await conversations.AppendUserTurnAsync(turn.ConversationId, turn.UserMessage, ct: ct).ConfigureAwait(false);
        // Same opening event as an engine turn: hosts join the persisted presentation row to their objective.
        yield return new TurnStarted(turnId, turn.ReportedProfile(options).Id, turn.ReportedProfile(options).Name) { ConversationTurnId = presentation.Id };
        if (NativeLaneSupport.BridgeUnavailableNotice(options.NativeAgents,
                turn.EffectiveContainment(options.NativeAgents) == NativeLaneContainment.WorkspaceConfined, turn.RepositoryRoot) is { } bridgeNotice)
            yield return new CodingLaneNotice(turnId, turn.Lane.Name, bridgeNotice);
        if (SystemSettingsPathInherited
            && NativeLaneSupport.MountedServers(mcp, options.NativeAgents.ExternalMcpServers, turn.RepositoryRoot, turn.ConversationId).Count > 0)
            yield return new CodingLaneNotice(turnId, turn.Lane.Name,
                $"{SystemSettingsVariable} is already set in this environment, so Gemini CLI uses those settings and the vault bridge and MCP servers are not mounted for it.");

        var resumeId = string.IsNullOrWhiteSpace(storedSession) ? null : storedSession;
        var retried = false;
        ParseState state;
        List<ChatMessage> transcript;
        StringBuilder stderr;
        var exitCode = -1;
        var persistedCount = 1;
        while (true)
        {
            state = new ParseState { Model = "configured:" + turn.Lane.Model };
            transcript = [ChatMessage.Text(ChatRole.User, turn.UserMessage)];
            stderr = new StringBuilder();
            exitCode = -1;
            persistedCount = 1;
            // A fresh session gets an id we choose, so it can be resumed even if the CLI never reports one.
            var newSessionId = resumeId is null ? Guid.NewGuid().ToString() : null;
            if (newSessionId is not null)
                await NativeLaneSupport.WriteStateAsync(conversations, logger, turn.ConversationId, SessionStateKey, newSessionId, ct).ConfigureAwait(false);
            var prompt = NativeLaneSupport.BuildPrompt(turn, isFirstTurn: resumeId is null, mcp, "GEMINI.md", sharedHistory, options.MaxNativeHandoffCharacters, options.NativeAgents.ExternalMcpServers);
            var psi = BuildStartInfo(executable, turn, resumeId, newSessionId, options.NativeAgents);
            // The per-launch settings file can hold environment values: delete it when this attempt ends.
            using var settingsLease = new NativeLaneSupport.TempConfigLease(SystemSettingsPathInherited ? null : psi.EnvironmentVariables[SystemSettingsVariable]);
            var channel = Channel.CreateUnbounded<TurnEvent>(new UnboundedChannelOptions { SingleReader = true });
            var capturedTranscript = transcript;
            var capturedState = state;
            var capturedStderr = stderr;
            using var attemptLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var pumpCt = attemptLifetime.Token;
            var pump = Task.Run(async () =>
            {
                IChildProcess? process = null;
                Process? diagnosticsProcess = null;
                ChildProcessLifetimeContainment? containment = null;
                try
                {
                    foreach (var notice in await NativeLaneSupport.PrepareHttpEnvironmentAsync(psi, options.NativeAgents,
                        false, vault, pumpCt).ConfigureAwait(false))
                        channel.Writer.TryWrite(new CodingLaneNotice(turnId, turn.Lane.Name, notice));
                    process = ChildProcessLauncher.Start(NativeLaneSupport.ForLaunch(psi)) ?? throw new InvalidOperationException("The Gemini process did not start.");
                    diagnosticsProcess = process.AttachDiagnosticsProcess();
                    containment = diagnosticsProcess is null ? null : ChildProcessLifetimeContainment.Attach(diagnosticsProcess,
                        NativeLaneSupport.WorkloadKey(turn.RepositoryRoot, turn.ConversationId), logger);
                    process.ErrorLineReceived += line =>
                    {
                        if (line is null) return;
                        if (capturedStderr.Length < 64 * 1024) capturedStderr.AppendLine(line);
                        if (NativeLaneSupport.IsMcpDiagnostic(line))
                            channel.Writer.TryWrite(new CodingLaneNotice(turnId, turn.Lane.Name, NativeLaneSupport.DiagnosticNotice("Gemini: ", line)));
                    };
                    process.BeginErrorReadLine();
                    try
                    {
                        await process.StandardInput.WriteAsync(prompt).ConfigureAwait(false);
                        process.StandardInput.Close();
                    }
                    catch (IOException ex) { logger.LogDebug(ex, "Gemini closed stdin early."); }

                    using var killOnCancel = pumpCt.Register(() => { try { process.Kill(entireProcessTree: true); } catch { /* best effort */ } });
                    while (await process.StandardOutput.ReadLineAsync(pumpCt).ConfigureAwait(false) is { } line)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        JsonObject? evt;
                        try { evt = JsonNode.Parse(line) as JsonObject; }
                        catch (JsonException) { evt = null; }
                        if (evt is null) { logger.LogDebug("Gemini emitted a non-JSON line: {Line}", NativeLaneSupport.Truncate(line, 200)); continue; }
                        var pendingEvents = Channel.CreateUnbounded<TurnEvent>();
                        HandleEvent(evt, turnId, pendingEvents.Writer, capturedTranscript, capturedState);
                        persistedCount = await NativeLaneSupport.FlushAsync(sessions, turn.ConversationId, capturedTranscript,
                            persistedCount, pumpCt).ConfigureAwait(false);
                        while (pendingEvents.Reader.TryRead(out var progress)) await channel.Writer.WriteAsync(progress, pumpCt).ConfigureAwait(false);
                    }
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                    return process.ExitCode;
                }
                catch (OperationCanceledException) when (pumpCt.IsCancellationRequested)
                {
                    try { process?.Kill(entireProcessTree: true); } catch { /* best effort */ }
                    throw;
                }
                finally
                {
                    containment?.Dispose();
                    diagnosticsProcess?.Dispose();
                    process?.Dispose();
                    channel.Writer.TryComplete();
                }
            }, CancellationToken.None);

            var readerCompleted = false;
            try
            {
                while (true)
                {
                    TurnEvent? evt = null;
                    try
                    {
                        if (!await channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false)) break;
                        if (!channel.Reader.TryRead(out evt)) continue;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    yield return evt!;
                }
                readerCompleted = true;
            }
            finally
            {
                if (!readerCompleted)
                {
                    // Detaching the consumer also stops and joins the owned CLI before repairing the durable receipts.
                    attemptLifetime.Cancel();
                    try { await pump.ConfigureAwait(false); } catch { /* terminal receipt is saved below */ }
                    await NativeLaneSupport.PersistAsync(conversations, sessions, logger, turn.ProfileId ?? options.ProfileId, turn.ConversationId,
                        presentation.Id, transcript, null, DateTime.UtcNow - started, CancellationToken.None, persistedCount).ConfigureAwait(false);
                }
            }

            try { exitCode = await pump.ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await NativeLaneSupport.PersistAsync(conversations, sessions, logger, turn.ProfileId ?? options.ProfileId, turn.ConversationId,
                    presentation.Id, transcript, null, DateTime.UtcNow - started, CancellationToken.None, persistedCount).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                state.Error ??= "Gemini could not run: " + ex.Message;
                state.FailureCategory ??= LlmErrorPolicy.Classify(ex);
            }

            var completed = state.TurnCompleted && state.Error is null;
            if (!completed && resumeId is not null && !retried && !state.SawInit && !transcript.Any(m => m.Parts.OfType<ToolUsePart>().Any()))
            {
                logger.LogInformation("Gemini did not resume session {Session} for {Conversation}; starting a fresh session.", resumeId, turn.ConversationId);
                await NativeLaneSupport.WriteStateAsync(conversations, logger, turn.ConversationId, SessionStateKey, "", ct).ConfigureAwait(false);
                resumeId = null;
                retried = true;
                continue;
            }
            break;
        }

        var duration = DateTime.UtcNow - started;
        var finalText = state.TurnCompleted && state.Error is null ? state.Assistant.ToString().Trim() is { Length: > 0 } t ? t : NativeLaneSupport.LastAssistantText(transcript) ?? "" : null;
        if (finalText is null)
        {
            var detail = stderr.Length > 0 ? NativeLaneSupport.Truncate(stderr.ToString().Trim(), 800) : null;
            var message = state.Error ?? (exitCode == 0 ? "Gemini exited without completing the turn." : $"Gemini exited with code {exitCode}.");
            if (detail is not null && !message.Contains(detail, StringComparison.Ordinal)) message += " " + detail;
            state.FailureCategory ??= NativeLaneSupport.ClassifyCliFailure(message);
            await NativeLaneSupport.PersistAsync(conversations, sessions, logger, turn.ProfileId ?? options.ProfileId, turn.ConversationId, presentation.Id, transcript, null, duration, ct, persistedCount).ConfigureAwait(false);
            yield return new TurnErrored(turnId, message, detail) { FailureCategory = state.FailureCategory };
            yield break;
        }

        await NativeLaneSupport.PersistAsync(conversations, sessions, logger, turn.ProfileId ?? options.ProfileId, turn.ConversationId, presentation.Id, transcript, finalText, duration, ct, persistedCount).ConfigureAwait(false);
        yield return new TurnCompleted(turnId, finalText, duration, ReplacesStreamedText: false);
    }

    internal sealed class ParseState
    {
        public string Model = "gemini";
        public bool SawInit;
        public string? Error;
        public LlmFailureCategory? FailureCategory;
        public bool TurnCompleted;
        /// <summary>Assistant text since the last tool call; flushed to the transcript when a tool starts or the turn ends.</summary>
        public readonly StringBuilder Assistant = new();
        public readonly Dictionary<string, (string Name, DateTime StartedAt)> Started = new(StringComparer.Ordinal);
    }

    /// <summary>Maps one <c>gemini -o stream-json</c> event to turn events and transcript parts.</summary>
    internal static void HandleEvent(JsonObject evt, string turnId, ChannelWriter<TurnEvent> writer, List<ChatMessage> transcript, ParseState state)
    {
        switch ((string?)evt["type"])
        {
            case "init":
                state.SawInit = true;
                if ((string?)evt["model"] is { Length: > 0 } model) state.Model = model;
                break;
            case "message":
                if (!string.Equals((string?)evt["role"], "assistant", StringComparison.OrdinalIgnoreCase)) break;
                var text = (string?)evt["content"] ?? "";
                if (text.Length == 0) break;
                state.Assistant.Append(text);
                writer.TryWrite(new TextChunk(turnId, text));
                break;
            case "tool_use":
                FlushAssistant(transcript, state);
                var id = (string?)evt["tool_id"] ?? Guid.NewGuid().ToString("N");
                var name = (string?)evt["tool_name"] ?? "gemini.tool";
                var input = evt["parameters"] as JsonObject ?? new JsonObject();
                state.Started[id] = (name, DateTime.UtcNow);
                transcript.Add(new ChatMessage(ChatRole.Assistant, [new ToolUsePart(id, name, input)]));
                writer.TryWrite(new ToolCallStarted(turnId, id, name, input.DeepClone() as JsonObject ?? new JsonObject()) { SideEffect = NativeToolSideEffects.Gemini(name) });
                break;
            case "tool_result":
                var resultId = (string?)evt["tool_id"] ?? "";
                if (!state.Started.Remove(resultId, out var call)) break;
                var isError = string.Equals((string?)evt["status"], "error", StringComparison.OrdinalIgnoreCase);
                var output = isError
                    ? (string?)evt["error"]?["message"] ?? (string?)evt["output"] ?? "Tool error"
                    : (string?)evt["output"] ?? "";
                transcript.Add(new ChatMessage(ChatRole.Tool, [new ToolResultPart(resultId, output, isError)]));
                writer.TryWrite(new ToolCallCompleted(turnId, resultId, call.Name, output, isError, DateTime.UtcNow - call.StartedAt) { SideEffect = NativeToolSideEffects.Gemini(call.Name) });
                break;
            case "error":
                // Warnings (loop detection, recoverable API errors) are not turn failures; the result event decides.
                if (!string.Equals((string?)evt["severity"], "warning", StringComparison.OrdinalIgnoreCase))
                {
                    state.Error = (string?)evt["message"] ?? "Gemini reported an error.";
                    state.FailureCategory = NativeLaneSupport.ClassifyCliFailure(state.Error);
                }
                break;
            case "result":
                FlushAssistant(transcript, state);
                if (string.Equals((string?)evt["status"], "success", StringComparison.OrdinalIgnoreCase))
                {
                    state.TurnCompleted = true;
                    state.Error = null;
                    if (evt["stats"] is JsonObject stats)
                        writer.TryWrite(NativeLaneSupport.Usage(turnId, ProviderId, state.Model, new JsonObject
                        {
                            ["input_tokens"] = stats["input_tokens"]?.DeepClone(),
                            ["output_tokens"] = stats["output_tokens"]?.DeepClone(),
                            ["cached_input_tokens"] = stats["cached"]?.DeepClone(),
                        }));
                }
                else
                {
                    state.Error = (string?)evt["error"]?["message"] ?? "Gemini reported that the turn failed.";
                    state.FailureCategory = NativeLaneSupport.ClassifyCliFailure(state.Error);
                }
                break;
        }
    }

    /// <summary>Moves buffered assistant text into the transcript; the buffer keeps the last segment for the final answer.</summary>
    private static void FlushAssistant(List<ChatMessage> transcript, ParseState state)
    {
        var text = state.Assistant.ToString().Trim();
        state.Assistant.Clear();
        if (text.Length == 0) return;
        transcript.Add(ChatMessage.Text(ChatRole.Assistant, text));
    }

    internal static string? ResolveExecutable()
    {
        try
        {
            return NativeCliCapabilities.ResolveExecutable(SubscriptionCliDiscovery.ResolveExecutableCandidates(SubscriptionCliCatalog.GeminiCliProvider));
        }
        catch { return null; }
    }

    internal static ProcessStartInfo BuildStartInfo(string executable, NativeLaneTurn turn, string? resumeId, string? newSessionId, NativeAgentOptions native)
    {
        var args = new List<string> { "-o", "stream-json", "--approval-mode", "yolo", "--skip-trust" };
        foreach (var folder in turn.Attachments.Select(a => Path.GetDirectoryName(a.LocalPath)!).Distinct()) { args.Add("--include-directories"); args.Add(folder); }
        var model = NormalizeModel(turn.Lane.Model);
        if (model is not null) { args.Add("-m"); args.Add(model); }
        if (!string.IsNullOrWhiteSpace(resumeId)) { args.Add("--resume"); args.Add(resumeId); }
        else if (!string.IsNullOrWhiteSpace(newSessionId)) { args.Add("--session-id"); args.Add(newSessionId); }
        // The prompt arrives on stdin; -p only switches the CLI to headless mode and is appended to stdin.
        args.Add("-p"); args.Add("Follow the instructions above.");

        var psi = NativeLaneSupport.TurnStartInfo(executable, args, turn.RepositoryRoot);
        NativeLaneSupport.ScrubApiKeys(psi.EnvironmentVariables);
        // The person's own system settings path wins; nothing is written that this launch would not use.
        if (!SystemSettingsPathInherited
            && NativeLaneSupport.WriteGeminiSettings(native.McpServer, turn.RepositoryRoot, native.ExternalMcpServers, turn.ConversationId) is { } settings)
            psi.EnvironmentVariables[SystemSettingsVariable] = settings;
        return psi;
    }

    /// <summary>Where Gemini CLI reads system settings; the runner mounts the vault bridge and external servers through it.</summary>
    internal const string SystemSettingsVariable = "GEMINI_CLI_SYSTEM_SETTINGS_PATH";

    private static bool SystemSettingsPathInherited => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(SystemSettingsVariable));

    /// <summary>"gemini-cli:default" means Gemini's own default; "gemini-cli:pro" / "gemini-cli:flash" or a full model id pins a model.</summary>
    internal static string? NormalizeModel(string model)
    {
        var value = model;
        var colon = value.IndexOf(':');
        if (colon >= 0) value = value[(colon + 1)..];
        value = value.Trim();
        return value.Length == 0 || value.Equals("default", StringComparison.OrdinalIgnoreCase) ? null : value;
    }
}
