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
/// Runs the Codex CLI as a native coding agent: <c>codex exec --json</c> in the repository with
/// approvals off (and, by default, no OS sandbox, matching Claude Code with permissions
/// bypassed), one thread per conversation resumed across turns. Codex's JSONL items become
/// <see cref="TurnEvent"/>s and the turn is persisted in SwarmCore's neutral transcript so
/// another lane can continue from the receipts. The MCP bridge, when configured, gives Codex
/// SwarmCode's vault tools. A thread Codex no longer knows is forgotten and the turn retried
/// once fresh; a cancelled turn keeps its receipts.
/// </summary>
public sealed class CodexNativeLaneRunner(
    IConversationStore conversations,
    IAgentSessionStore sessions,
    IOptions<CodingOptions> coding,
    ILogger<CodexNativeLaneRunner> logger, SwarmCore.Secrets.ISecretVault? vault = null) : INativeLaneRunner
{
    public const string ProviderId = "CodexNative";
    public const string ThreadStateKey = "codex.threadId";

    /// <summary>Tool names reported for Codex items; the orchestrator's mutation detection knows them.</summary>
    public const string CommandToolName = "codex.command";
    public const string FileChangeToolName = "codex.file_change";
    public const string McpToolName = "codex.mcp_tool";
    public const string WebSearchToolName = "codex.web_search";

    internal Func<string?> ExecutableResolver { get; init; } = ResolveExecutable;
    internal Func<ProcessStartInfo, IChildProcess> ProcessStarter { get; init; } = ChildProcessLauncher.Start;

    public bool Supports(CodingLane lane) => string.Equals(lane.Provider, ProviderId, StringComparison.OrdinalIgnoreCase);
    public string ModelCatalogProvider => SubscriptionCliCatalog.CodexCliProvider;

    public const string ConfinementUnavailableReason =
        "Codex was not started: confined execution requires a successful CLI help probe advertising both --ignore-user-config and --ignore-rules. Install a CLI with those boundaries or use an admitted non-native lane.";

    public bool SupportsContainment(NativeLaneContainment containment)
        => containment == NativeLaneContainment.Bypass || ExecutableResolver() is { } executable && HasRequiredConfinementCapabilities(executable);

    internal static bool HasRequiredConfinementCapabilities(string executable)
        => NativeCliCapabilities.Supports(executable, IgnoreUserConfigFlag, "exec", "--help")
            && NativeCliCapabilities.Supports(executable, IgnoreRulesFlag, "exec", "--help");

    public async IAsyncEnumerable<TurnEvent> RunAsync(NativeLaneTurn turn, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var turnId = Guid.NewGuid().ToString("N");
        var started = DateTime.UtcNow;
        var options = coding.Value;
        // Admission is for this turn. Later settings changes cannot widen its launch authority.
        turn = turn with { Containment = turn.EffectiveContainment(options.NativeAgents) };

        var executable = ExecutableResolver();
        if (executable is null)
        {
            yield return new TurnErrored(turnId, "Codex CLI executable was not found on this machine; install it and sign in to use the native lane.")
            { FailureCategory = LlmFailureCategory.ModelNotFound };
            yield break;
        }

        var confinedExecution = turn.EffectiveContainment(options.NativeAgents) == NativeLaneContainment.WorkspaceConfined;
        if (confinedExecution && !HasRequiredConfinementCapabilities(executable))
        {
            yield return new TurnErrored(turnId, ConfinementUnavailableReason) { FailureCategory = LlmFailureCategory.ModelNotFound };
            yield break;
        }

        var threadId = await NativeLaneSupport.ReadStateAsync(conversations, logger, turn.ConversationId, ThreadStateKey, ct).ConfigureAwait(false);
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

        var resumeId = string.IsNullOrWhiteSpace(threadId) ? null : threadId;
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
            var confined = turn.EffectiveContainment(options.NativeAgents) == NativeLaneContainment.WorkspaceConfined;
            // External MCP servers are mounted only unconfined (confined runs pass mcp_servers={}).
            var prompt = NativeLaneSupport.BuildPrompt(turn, isFirstTurn: resumeId is null, mcp, "AGENTS.md", sharedHistory, options.MaxNativeHandoffCharacters,
                confined ? null : options.NativeAgents.ExternalMcpServers);
            var psi = BuildStartInfo(executable, turn, resumeId, options.NativeAgents,
                ignoreUserConfigSupported: confinedExecution,
                ignoreRulesSupported: confinedExecution);
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
                        confined, vault, pumpCt).ConfigureAwait(false))
                        channel.Writer.TryWrite(new CodingLaneNotice(turnId, turn.Lane.Name, notice));
                    process = ProcessStarter(NativeLaneSupport.ForLaunch(psi)) ?? throw new InvalidOperationException("The Codex process did not start.");
                    diagnosticsProcess = process.AttachDiagnosticsProcess();
                    containment = diagnosticsProcess is null ? null : ChildProcessLifetimeContainment.Attach(diagnosticsProcess,
                        NativeLaneSupport.WorkloadKey(turn.RepositoryRoot, turn.ConversationId), logger);
                    process.ErrorLineReceived += line =>
                    {
                        if (line is null) return;
                        if (capturedStderr.Length < 64 * 1024) capturedStderr.AppendLine(line);
                        // Codex reports MCP start-up failures (and config errors naming MCP) only on stderr: make them visible.
                        if (NativeLaneSupport.IsMcpDiagnostic(line))
                            channel.Writer.TryWrite(new CodingLaneNotice(turnId, turn.Lane.Name, NativeLaneSupport.DiagnosticNotice("Codex: ", line)));
                    };
                    if (!confined && NativeLaneSupport.DescribeCodexReach(options.NativeAgents.McpServer, options.NativeAgents.ExternalMcpServers,
                            turn.RepositoryRoot, options.NativeAgents.AllowCodexAppConnectors) is { } mounted)
                        channel.Writer.TryWrite(new CodingLaneNotice(turnId, turn.Lane.Name, mounted));
                    process.BeginErrorReadLine();
                    try
                    {
                        await process.StandardInput.WriteAsync(prompt).ConfigureAwait(false);
                        process.StandardInput.Close();
                    }
                    catch (IOException ex) { logger.LogDebug(ex, "Codex closed stdin early."); }

                    using var killOnCancel = pumpCt.Register(() => { try { process.Kill(entireProcessTree: true); } catch { /* best effort */ } });
                    while (await process.StandardOutput.ReadLineAsync(pumpCt).ConfigureAwait(false) is { } line)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        JsonObject? evt;
                        try { evt = ParseEvent(line); }
                        catch (JsonException) { evt = null; }
                        if (evt is null) { logger.LogDebug("Codex emitted a non-JSON line: {Line}", NativeLaneSupport.Truncate(line, 200)); continue; }
                        var before = capturedState.ThreadId;
                        var pendingEvents = Channel.CreateUnbounded<TurnEvent>();
                        HandleEvent(evt, turnId, pendingEvents.Writer, capturedTranscript, capturedState);
                        persistedCount = await NativeLaneSupport.FlushAsync(sessions, turn.ConversationId, capturedTranscript,
                            persistedCount, pumpCt).ConfigureAwait(false);
                        while (pendingEvents.Reader.TryRead(out var progress)) await channel.Writer.WriteAsync(progress, pumpCt).ConfigureAwait(false);
                        if (capturedState.ThreadId is { } id && id != before)
                            await NativeLaneSupport.WriteStateAsync(conversations, logger, turn.ConversationId, ThreadStateKey, id, CancellationToken.None).ConfigureAwait(false);
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
                state.Error ??= "Codex could not run: " + ex.Message;
                state.FailureCategory ??= LlmErrorPolicy.Classify(ex);
            }

            var completed = state.TurnCompleted && state.Error is null;
            if (!completed && resumeId is not null && state.ThreadId is null && !retried && !transcript.Any(m => m.Parts.OfType<ToolUsePart>().Any()))
            {
                logger.LogInformation("Codex did not resume thread {Thread} for {Conversation}; starting a fresh thread.", resumeId, turn.ConversationId);
                await NativeLaneSupport.WriteStateAsync(conversations, logger, turn.ConversationId, ThreadStateKey, "", ct).ConfigureAwait(false);
                resumeId = null;
                retried = true;
                continue;
            }
            break;
        }

        var duration = DateTime.UtcNow - started;
        var finalText = state.TurnCompleted && state.Error is null ? state.LastMessage ?? NativeLaneSupport.LastAssistantText(transcript) ?? "" : null;
        if (finalText is null)
        {
            var detail = stderr.Length > 0 ? NativeLaneSupport.Truncate(stderr.ToString().Trim(), 800) : null;
            var message = state.Error ?? (exitCode == 0 ? "Codex exited without completing the turn." : $"Codex exited with code {exitCode}.");
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
        public string Model = "codex";
        public string? ThreadId;
        public string? LastMessage;
        public string? Error;
        public LlmFailureCategory? FailureCategory;
        public bool TurnCompleted;
        public readonly Dictionary<string, DateTime> Started = new(StringComparer.Ordinal);
    }

    /// <summary>Materializes CLI event JSON with consistent duplicate-property semantics.</summary>
    internal static JsonObject? ParseEvent(string line)
    {
        // JsonNode lazily materializes objects and throws ArgumentException on duplicate
        // properties only when a field is read. Normalize the CLI's event JSON eagerly,
        // using JsonElement.GetProperty's last-property-wins semantics at every depth.
        using var document = JsonDocument.Parse(line);
        return Materialize(document.RootElement) as JsonObject;

        static JsonNode? Materialize(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var result = new JsonObject();
                foreach (var property in element.EnumerateObject())
                    result[property.Name] = Materialize(property.Value);
                return result;
            }
            if (element.ValueKind == JsonValueKind.Array)
            {
                var result = new JsonArray();
                foreach (var item in element.EnumerateArray()) result.Add(Materialize(item));
                return result;
            }
            return JsonNode.Parse(element.GetRawText());
        }
    }

    /// <summary>Maps one <c>codex exec --json</c> event to turn events and transcript parts.</summary>
    internal static void HandleEvent(JsonObject evt, string turnId, ChannelWriter<TurnEvent> writer, List<ChatMessage> transcript, ParseState state)
    {
        switch ((string?)evt["type"])
        {
            case "thread.started":
                if ((string?)evt["thread_id"] is { Length: > 0 } id) state.ThreadId = id;
                break;
            case "turn.completed":
                state.TurnCompleted = true;
                if (evt["usage"] is JsonObject usage)
                    writer.TryWrite(NativeLaneSupport.Usage(turnId, ProviderId, state.Model, usage));
                break;
            case "turn.failed":
                state.Error = (string?)evt["error"]?["message"] ?? "Codex reported that the turn failed.";
                state.FailureCategory = NativeLaneSupport.ClassifyCliFailure(state.Error);
                break;
            case "error":
                state.Error = (string?)evt["message"] ?? "Codex reported an error.";
                state.FailureCategory = NativeLaneSupport.ClassifyCliFailure(state.Error);
                break;
            case "item.started":
                if (evt["item"] is JsonObject startedItem) HandleItemStarted(startedItem, turnId, writer, transcript, state);
                break;
            case "item.completed":
                if (evt["item"] is JsonObject completedItem) HandleItemCompleted(completedItem, turnId, writer, transcript, state);
                break;
        }
    }

    private static void HandleItemStarted(JsonObject item, string turnId, ChannelWriter<TurnEvent> writer, List<ChatMessage> transcript, ParseState state)
    {
        var id = (string?)item["id"] ?? Guid.NewGuid().ToString("N");
        var (toolName, input) = DescribeTool(item);
        if (toolName is null) return;
        state.Started[id] = DateTime.UtcNow;
        transcript.Add(new ChatMessage(ChatRole.Assistant, [new ToolUsePart(id, toolName, input)]));
        writer.TryWrite(new ToolCallStarted(turnId, id, toolName, input.DeepClone() as JsonObject ?? new JsonObject()) { SideEffect = NativeToolSideEffects.Codex(toolName, input as JsonObject) });
    }

    private static void HandleItemCompleted(JsonObject item, string turnId, ChannelWriter<TurnEvent> writer, List<ChatMessage> transcript, ParseState state)
    {
        var id = (string?)item["id"] ?? Guid.NewGuid().ToString("N");
        switch ((string?)item["type"])
        {
            case "agent_message":
                var text = (string?)item["text"] ?? "";
                if (text.Length == 0) break;
                state.LastMessage = text;
                transcript.Add(ChatMessage.Text(ChatRole.Assistant, text));
                writer.TryWrite(new TextChunk(turnId, text.EndsWith('\n') ? text : text + "\n"));
                break;
            case "reasoning":
                var reasoning = (string?)item["text"] ?? "";
                if (reasoning.Length > 0) writer.TryWrite(new ReasoningChunk(turnId, reasoning));
                break;
            case "error":
                state.Error = (string?)item["message"] ?? "Codex reported an error.";
                state.FailureCategory = NativeLaneSupport.ClassifyCliFailure(state.Error);
                break;
            default:
                var (toolName, input) = DescribeTool(item);
                if (toolName is null) break;
                if (!state.Started.TryGetValue(id, out var startedAt))
                {
                    // Some items (file changes, searches) only report completion: synthesize the start.
                    startedAt = DateTime.UtcNow;
                    transcript.Add(new ChatMessage(ChatRole.Assistant, [new ToolUsePart(id, toolName, input)]));
                    writer.TryWrite(new ToolCallStarted(turnId, id, toolName, input.DeepClone() as JsonObject ?? new JsonObject()) { SideEffect = NativeToolSideEffects.Codex(toolName, input as JsonObject) });
                }
                var (result, isError) = DescribeResult(item);
                transcript.Add(new ChatMessage(ChatRole.Tool, [new ToolResultPart(id, result, isError)]));
                writer.TryWrite(new ToolCallCompleted(turnId, id, toolName, result, isError, DateTime.UtcNow - startedAt) { SideEffect = NativeToolSideEffects.Codex(toolName, input as JsonObject) });
                break;
        }
    }

    private static (string? Name, JsonObject Input) DescribeTool(JsonObject item) => (string?)item["type"] switch
    {
        "command_execution" => (CommandToolName, new JsonObject { ["command"] = (string?)item["command"] }),
        "file_change" => (FileChangeToolName, new JsonObject { ["changes"] = item["changes"]?.DeepClone() }),
        "mcp_tool_call" => (McpToolName, new JsonObject { ["server"] = (string?)item["server"], ["tool"] = (string?)item["tool"], ["arguments"] = item["arguments"]?.DeepClone() }),
        "web_search" => (WebSearchToolName, new JsonObject { ["query"] = (string?)item["query"] }),
        _ => (null, new JsonObject()),
    };

    private static (string Result, bool IsError) DescribeResult(JsonObject item)
    {
        var status = (string?)item["status"] ?? "";
        var failed = status.Equals("failed", StringComparison.OrdinalIgnoreCase);
        switch ((string?)item["type"])
        {
            case "command_execution":
                var exit = (int?)item["exit_code"];
                var output = (string?)item["aggregated_output"] ?? "";
                return (new JsonObject { ["exitCode"] = exit, ["output"] = output }.ToJsonString(), failed || exit is not null and not 0);
            case "mcp_tool_call":
                return (NativeLaneSupport.SanitizeReceipt(item["result"])?.ToJsonString()
                    ?? NativeLaneSupport.SanitizeReceipt(item["error"])?.ToJsonString() ?? status, failed || item["error"] is not null);
            default:
                return (item.ToJsonString(), failed);
        }
    }

    internal static string? ResolveExecutable()
    {
        try
        {
            // Resolve each shim before probing: its code-mode companion belongs beside the versioned target.
            return NativeCliCapabilities.ResolveExecutable(SubscriptionCliDiscovery.ResolveExecutableCandidates(SubscriptionCliCatalog.CodexCliProvider)
                .Where(path => !SubscriptionCliDiscovery.IsProtectedDesktopPackageCli(path))
                .Select(path => CodexCliLaunchPath.Resolve(path) ?? path));
        }
        catch { return null; }
    }

    public const string IgnoreUserConfigFlag = "--ignore-user-config";
    public const string IgnoreRulesFlag = "--ignore-rules";

    /// <summary>
    /// Turns off Codex's built-in app connectors (the "apps" feature, stable and on by default in codex-cli 0.130;
    /// <c>codex features list</c>), which neither <c>mcp_servers={}</c> nor <c>--ignore-user-config</c> removes.
    /// Same as <c>--disable apps</c>; the <c>-c</c> form is accepted by every CLI version that reads config overrides.
    /// </summary>
    public const string AppConnectorsOff = "features.apps=false";

    /// <summary>
    /// Windows sandbox for confined runs. With the user's config ignored Codex would otherwise fall back to a
    /// read-only sandbox on Windows; "unelevated" (restricted token) needs no per-machine admin setup and
    /// permits writes inside the workspace only (verified live with codex-cli 0.130).
    /// </summary>
    public const string WindowsSandboxOverride = "windows.sandbox='unelevated'";

    /// <summary>
    /// TOML string values are literal strings ('...'): the same values as basic strings, and free of double quotes,
    /// which a Windows command shim cannot carry (a shim starts through the batch bridge at launch, NativeLaneSupport.ForLaunch).
    /// </summary>
    internal static ProcessStartInfo BuildStartInfo(string executable, NativeLaneTurn turn, string? threadId, NativeAgentOptions native,
        bool ignoreUserConfigSupported = false, bool ignoreRulesSupported = false, bool? windows = null)
    {
        var args = new List<string> { "exec", "--json", "--skip-git-repo-check", "-C", turn.RepositoryRoot };
        var confined = turn.EffectiveContainment(native) == NativeLaneContainment.WorkspaceConfined;
        if (confined)
        {
            if (!ignoreUserConfigSupported || !ignoreRulesSupported)
                throw new InvalidOperationException(ConfinementUnavailableReason);
            // Codex's OS sandbox: writes only inside the workspace, no network (so no git push or fetch),
            // and no approvals to escape it; the user's config, MCP servers and exec rules are not loaded.
            args.Add("-s"); args.Add("workspace-write");
            args.Add("-c"); args.Add("approval_policy='never'");
            args.Add("-c"); args.Add("sandbox_workspace_write.network_access=false");
            args.Add("-c"); args.Add("mcp_servers={}");
            // mcp_servers={} does not cover the built-in app connectors; they are a feature of their own.
            args.Add("-c"); args.Add(AppConnectorsOff);
            // The temp folders are not workspace: without these, workspace-write also allows writing there.
            args.Add("-c"); args.Add("sandbox_workspace_write.exclude_tmpdir_env_var=true");
            args.Add("-c"); args.Add("sandbox_workspace_write.exclude_slash_tmp=true");
            if (windows ?? OperatingSystem.IsWindows()) { args.Add("-c"); args.Add(WindowsSandboxOverride); }
            args.Add(IgnoreUserConfigFlag);
            args.Add(IgnoreRulesFlag);
        }
        else
        {
            args.Add("--dangerously-bypass-approvals-and-sandbox");
            // The user's own Codex app connectors reach live services; only when the user allows them.
            if (!native.AllowCodexAppConnectors) { args.Add("-c"); args.Add(AppConnectorsOff); }
        }
        var (model, effort) = NormalizeModel(turn.Lane.Model);
        if (model is not null) { args.Add("-m"); args.Add(model); }
        if (effort is not null) { args.Add("-c"); args.Add($"model_reasoning_effort={NativeLaneSupport.Toml(effort)}"); }
        if (!confined) args.AddRange(NativeLaneSupport.CodexMcpOverrides(native.McpServer, turn.RepositoryRoot, native.ExternalMcpServers, turn.ConversationId));
        if (!string.IsNullOrWhiteSpace(threadId)) { args.Add("resume"); args.Add(threadId); }
        foreach (var image in turn.Attachments.Where(a => a.IsImage)) { args.Add("--image"); args.Add(image.LocalPath); }
        args.Add("-"); // prompt on stdin

        var psi = NativeLaneSupport.TurnStartInfo(executable, args, turn.RepositoryRoot);
        NativeLaneSupport.ScrubApiKeys(psi.EnvironmentVariables);
        return psi;
    }

    /// <summary>"codex-cli:default" means Codex's own default; "codex-cli:gpt-5.6-terra@high" pins a model and a reasoning effort.</summary>
    internal static (string? Model, string? Effort) NormalizeModel(string model)
    {
        var value = model;
        var colon = value.IndexOf(':');
        if (colon >= 0) value = value[(colon + 1)..];
        value = value.Trim();
        if (value.Length == 0 || value.Equals("default", StringComparison.OrdinalIgnoreCase)) return (null, null);
        var at = value.LastIndexOf('@');
        if (at > 0 && at < value.Length - 1)
        {
            var effort = value[(at + 1)..].Trim().ToLowerInvariant();
            if (SwarmCore.LlmRouting.ProviderPacks.ProviderPackCatalog.GetRequired("CodexCli")
                .SubscriptionCli!.ReasoningEfforts.Contains(effort, StringComparer.Ordinal)) return (value[..at], effort);
        }
        return (value, null);
    }
}
