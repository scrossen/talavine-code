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
/// Runs the Claude Code CLI as a native coding agent: one streaming <c>claude -p</c> session
/// per conversation (resumed across turns), its own tools, in the repository, with
/// permission prompts bypassed because the user already authorized the work. The CLI's
/// stream-json events become <see cref="TurnEvent"/>s, and the turn is persisted to the
/// conversation and session stores in SwarmCore's neutral transcript form so another lane
/// can continue from the receipts after a hand-off. When the host configures the MCP
/// bridge, the CLI also gets SwarmCode's vault tools. A session the CLI no longer knows is
/// forgotten and the turn retried once fresh; a cancelled turn keeps its receipts.
/// </summary>
public sealed class ClaudeCodeNativeLaneRunner(
    IConversationStore conversations,
    IAgentSessionStore sessions,
    IOptions<CodingOptions> coding,
    ILogger<ClaudeCodeNativeLaneRunner> logger, SwarmCore.Secrets.ISecretVault? vault = null,
    Planning.IConversationPlanStore? plans = null) : INativeLaneRunner
{
    public const string ProviderId = "ClaudeCodeNative";
    public const string SessionStateKey = "claudeCode.sessionId";

    public bool Supports(CodingLane lane) => string.Equals(lane.Provider, ProviderId, StringComparison.OrdinalIgnoreCase);
    public string ModelCatalogProvider => SubscriptionCliCatalog.ClaudeCodeProvider;

    /// <summary>Claude Code's own <c>--permission-mode plan</c>, narrowed further by <see cref="PlanDisallowedTools"/>.</summary>
    public bool SupportsPlanMode => true;

    private sealed class Attempt
    {
        public readonly List<ChatMessage> Transcript = [];
        public readonly Dictionary<string, (string Name, DateTime Started)> ToolNames = new(StringComparer.Ordinal);
        public readonly StringBuilder Stderr = new();
        public string? FinalText;
        public string? ResultError;
        public LlmFailureCategory? FailureCategory;
        public string? NewSessionId;
        public int ExitCode = -1;
        public int PersistedCount = 1;
        public string? Model;
    }

    public async IAsyncEnumerable<TurnEvent> RunAsync(NativeLaneTurn turn, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var turnId = Guid.NewGuid().ToString("N");
        var started = DateTime.UtcNow;
        var options = coding.Value;

        var executable = ResolveExecutable();
        if (executable is null)
        {
            yield return new TurnErrored(turnId, "Claude Code CLI executable was not found on this machine; install it and sign in to use the native lane.")
            { FailureCategory = LlmFailureCategory.ModelNotFound };
            yield break;
        }

        var sessionId = await NativeLaneSupport.ReadStateAsync(conversations, logger, turn.ConversationId, SessionStateKey, ct).ConfigureAwait(false);
        var sharedHistory = await sessions.LoadAsync(turn.ConversationId, ct).ConfigureAwait(false);
        await sessions.AppendAsync(turn.ConversationId, ChatMessage.Text(ChatRole.User, turn.UserMessage), ct).ConfigureAwait(false);
        var mcp = options.NativeAgents.McpServer;
        var presentation = turn.IsContinuation
            ? await conversations.AppendContinuationTurnAsync(turn.ConversationId, turn.UserMessage, ct).ConfigureAwait(false)
            : await conversations.AppendUserTurnAsync(turn.ConversationId, turn.UserMessage, ct: ct).ConfigureAwait(false);
        // Same opening event as an engine turn: hosts join the persisted presentation row to their objective.
        yield return new TurnStarted(turnId, turn.ReportedProfile(options).Id, turn.ReportedProfile(options).Name) { ConversationTurnId = presentation.Id };

        // Confined runs mount no MCP servers at all (no vault bridge, no external servers; the confined
        // arguments carry --strict-mcp-config), so nothing is written and nothing is announced.
        var containment = turn.EffectiveContainment(options.NativeAgents);
        var confined = containment == NativeLaneContainment.WorkspaceConfined;
        string? mcpConfig = null;
        var externalMcp = confined ? null : options.NativeAgents.ExternalMcpServers;
        // A plan turn mounts only SwarmCode's bridge, which the host restricts to tools that change nothing for that turn; the
        // person's external servers stay off (their tools run outside the CLI's plan-mode checks and SwarmCode's).
        try
        {
            mcpConfig = WriteMcpConfigFor(containment, mcp, turn.RepositoryRoot, turn.PlanMode ? null : options.NativeAgents.ExternalMcpServers, turn.ConversationId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogWarning(ex, "Could not write the MCP bridge config; running Claude Code without the vault bridge."); }
        using var mcpConfigLease = new NativeLaneSupport.TempConfigLease(mcpConfig);
        if (!confined && !turn.PlanMode && NativeLaneSupport.DescribeExternalServers(mcp, externalMcp, turn.RepositoryRoot) is { } mountedServers)
            yield return new CodingLaneNotice(turnId, turn.Lane.Name, mountedServers);
        if (NativeLaneSupport.BridgeUnavailableNotice(options.NativeAgents, confined, turn.RepositoryRoot) is { } bridgeNotice)
            yield return new CodingLaneNotice(turnId, turn.Lane.Name, bridgeNotice);
        var restrictedSupported = confined && NativeCliCapabilities.Supports(executable, RestrictedFlag);
        // A Windows command shim cannot carry the inline settings JSON (its quotes would become cmd syntax): an older
        // CLI without --restricted gets the same settings from a per-launch file instead.
        using var settingsLease = new NativeLaneSupport.TempConfigLease(
            confined && !restrictedSupported && WindowsBatchLaunch.IsBatchFile(executable)
                ? NativeLaneSupport.WriteTempConfig("claude-settings", ConfinedSettings) : null);

        var resumeId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId;
        var retried = false;
        Attempt attempt;
        while (true)
        {
            attempt = new Attempt();
            attempt.Transcript.Add(ChatMessage.Text(ChatRole.User, turn.UserMessage));
            var prompt = NativeLaneSupport.BuildPrompt(turn, isFirstTurn: resumeId is null, confined ? null : mcp, "CLAUDE.md", sharedHistory,
                options.MaxNativeHandoffCharacters, turn.PlanMode ? null : externalMcp);
            var psi = BuildStartInfo(executable, turn, resumeId, confined ? null : mcpConfig, containment,
                restrictedSupported, options.NativeAgents.AllowClaudeAiConnectors, settingsLease.Path);
            var channel = Channel.CreateUnbounded<TurnEvent>(new UnboundedChannelOptions { SingleReader = true });
            using var attemptLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var pump = Task.Run(() => PumpAsync(psi, prompt, turn.ConversationId, attempt, turnId, turn.Lane.Model, channel.Writer, attemptLifetime.Token), CancellationToken.None);

            // yield return cannot sit inside a try/catch, so cancellation is handled around the read only.
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
                    // Claude Code keeps its own task list (TodoWrite); mirror it so the person sees the plan on any lane.
                    if (plans is not null && evt is ToolCallStarted toolStarted
                        && Planning.PlanTools.FromNativeTodoCall(toolStarted.ToolName, toolStarted.Input) is { } todo)
                        await MirrorPlanAsync(plans, turn.ConversationId, todo, ct).ConfigureAwait(false);
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
                        presentation.Id, attempt.Transcript, null, DateTime.UtcNow - started, CancellationToken.None, attempt.PersistedCount).ConfigureAwait(false);
                }
            }

            try { await pump.ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await NativeLaneSupport.PersistAsync(conversations, sessions, logger, turn.ProfileId ?? options.ProfileId, turn.ConversationId,
                    presentation.Id, attempt.Transcript, null, DateTime.UtcNow - started, CancellationToken.None, attempt.PersistedCount).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                attempt.ResultError ??= "Claude Code could not run: " + ex.Message;
                attempt.FailureCategory ??= LlmErrorPolicy.Classify(ex);
            }

            // A resume id the CLI no longer knows (pruned session, reinstall) fails without a result event.
            if (attempt.FinalText is null && resumeId is not null && attempt.NewSessionId is null && !retried && !attempt.Transcript.Any(m => m.Parts.OfType<ToolUsePart>().Any()))
            {
                logger.LogInformation("Claude Code did not resume session {Session} for {Conversation}; starting a fresh session.", resumeId, turn.ConversationId);
                await NativeLaneSupport.WriteStateAsync(conversations, logger, turn.ConversationId, SessionStateKey, "", ct).ConfigureAwait(false);
                resumeId = null;
                retried = true;
                continue;
            }
            break;
        }

        var duration = DateTime.UtcNow - started;
        if (attempt.FinalText is null)
        {
            var detail = attempt.Stderr.Length > 0 ? NativeLaneSupport.Truncate(attempt.Stderr.ToString().Trim(), 800) : null;
            var message = attempt.ResultError ?? (attempt.ExitCode == 0 ? "Claude Code exited without a result." : $"Claude Code exited with code {attempt.ExitCode}.");
            if (detail is not null && !message.Contains(detail, StringComparison.Ordinal)) message += " " + detail;
            attempt.FailureCategory ??= ClassifyText(detail) ?? ClassifyText(attempt.ResultError);
            await NativeLaneSupport.PersistAsync(conversations, sessions, logger, turn.ProfileId ?? options.ProfileId, turn.ConversationId, presentation.Id, attempt.Transcript, null, duration, ct, attempt.PersistedCount).ConfigureAwait(false);
            yield return new TurnErrored(turnId, message, detail) { FailureCategory = attempt.FailureCategory };
            yield break;
        }

        await NativeLaneSupport.PersistAsync(conversations, sessions, logger, turn.ProfileId ?? options.ProfileId, turn.ConversationId, presentation.Id, attempt.Transcript, attempt.FinalText, duration, ct, attempt.PersistedCount).ConfigureAwait(false);
        yield return new TurnCompleted(turnId, attempt.FinalText, duration, ReplacesStreamedText: false);
    }

    private async Task MirrorPlanAsync(Planning.IConversationPlanStore store, string conversationId, IReadOnlyList<Planning.PlanItem> items, CancellationToken ct)
    {
        try { await store.SetAsync(conversationId, items, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            logger.LogWarning("Could not mirror the Claude Code task list for {Conversation} ({Error}).", conversationId, ex.GetType().Name);
        }
    }

    private async Task PumpAsync(ProcessStartInfo psi, string prompt, string conversationId, Attempt attempt, string turnId, string model, ChannelWriter<TurnEvent> writer, CancellationToken ct)
    {
        IChildProcess? process = null;
        Process? diagnosticsProcess = null;
        ChildProcessLifetimeContainment? containment = null;
        try
        {
            foreach (var notice in await NativeLaneSupport.PrepareHttpEnvironmentAsync(psi, coding.Value.NativeAgents,
                psi.ArgumentList.Contains("--strict-mcp-config"), vault, ct).ConfigureAwait(false))
                writer.TryWrite(new CodingLaneNotice(turnId, "Claude Code", notice));
            process = ChildProcessLauncher.Start(NativeLaneSupport.ForLaunch(psi)) ?? throw new InvalidOperationException("The Claude Code process did not start.");
            diagnosticsProcess = process.AttachDiagnosticsProcess();
            containment = diagnosticsProcess is null ? null : ChildProcessLifetimeContainment.Attach(diagnosticsProcess, NativeLaneSupport.WorkloadKey(psi.WorkingDirectory, conversationId), logger);
            process.ErrorLineReceived += line =>
            {
                if (line is null) return;
                if (attempt.Stderr.Length < 64 * 1024) attempt.Stderr.AppendLine(line);
                if (NativeLaneSupport.IsMcpDiagnostic(line))
                    writer.TryWrite(new CodingLaneNotice(turnId, "Claude Code", NativeLaneSupport.DiagnosticNotice("Claude Code: ", line)));
            };
            process.BeginErrorReadLine();
            try
            {
                await process.StandardInput.WriteAsync(prompt).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            catch (IOException ex) { logger.LogDebug(ex, "Claude Code closed stdin early."); }

            using var killOnCancel = ct.Register(() => { try { process.Kill(entireProcessTree: true); } catch { /* best effort */ } });
            while (await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonObject? evt;
                try { evt = JsonNode.Parse(line) as JsonObject; }
                catch (JsonException) { evt = null; }
                if (evt is null) { logger.LogDebug("Claude Code emitted a non-JSON line: {Line}", NativeLaneSupport.Truncate(line, 200)); continue; }

                var pendingEvents = Channel.CreateUnbounded<TurnEvent>();
                switch ((string?)evt["type"])
                {
                    case "system":
                        attempt.Model = (string?)evt["model"] ?? attempt.Model;
                        if ((string?)evt["session_id"] is { Length: > 0 } s && attempt.NewSessionId is null)
                        {
                            attempt.NewSessionId = s;
                            // Remember it at once so a cancelled turn still resumes the same session next time.
                            await NativeLaneSupport.WriteStateAsync(conversations, logger, conversationId, SessionStateKey, s, CancellationToken.None).ConfigureAwait(false);
                        }
                        break;
                    case "assistant":
                        HandleAssistant(evt, turnId, pendingEvents.Writer, attempt.Transcript, attempt.ToolNames);
                        break;
                    case "user":
                        HandleToolResults(evt, turnId, pendingEvents.Writer, attempt.Transcript, attempt.ToolNames);
                        break;
                    case "result":
                        if ((string?)evt["session_id"] is { Length: > 0 } rs && attempt.NewSessionId is null)
                        {
                            attempt.NewSessionId = rs;
                            await NativeLaneSupport.WriteStateAsync(conversations, logger, conversationId, SessionStateKey, rs, CancellationToken.None).ConfigureAwait(false);
                        }
                        if (evt["usage"] is JsonObject usage)
                            pendingEvents.Writer.TryWrite(NativeLaneSupport.Usage(turnId, ProviderId, attempt.Model ?? "configured:" + model, usage,
                                (decimal?)evt["total_cost_usd"]));
                        var isError = (bool?)evt["is_error"] ?? false;
                        var subtype = (string?)evt["subtype"] ?? "";
                        var text = (string?)evt["result"];
                        if (!isError && subtype == "success")
                        {
                            attempt.FinalText = text ?? NativeLaneSupport.LastAssistantText(attempt.Transcript) ?? "";
                        }
                        else
                        {
                            attempt.ResultError = string.IsNullOrWhiteSpace(text) ? $"Claude Code ended the turn with {subtype}." : text;
                            attempt.FailureCategory = subtype.Contains("max_turns", StringComparison.OrdinalIgnoreCase) ? LlmFailureCategory.Timeout : null;
                        }
                        break;
                }
                attempt.PersistedCount = await NativeLaneSupport.FlushAsync(sessions, conversationId, attempt.Transcript,
                    attempt.PersistedCount, ct).ConfigureAwait(false);
                while (pendingEvents.Reader.TryRead(out var progress)) await writer.WriteAsync(progress, ct).ConfigureAwait(false);
            }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            attempt.ExitCode = process.ExitCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try { process?.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw;
        }
        finally
        {
            containment?.Dispose();
            diagnosticsProcess?.Dispose();
            process?.Dispose();
            writer.TryComplete();
        }
    }

    internal static void HandleAssistant(JsonObject evt, string turnId, ChannelWriter<TurnEvent> writer, List<ChatMessage> transcript, Dictionary<string, (string Name, DateTime Started)> toolNames)
    {
        if (evt["message"] is not JsonObject message || message["content"] is not JsonArray content) return;
        var parts = new List<ChatContentPart>();
        foreach (var node in content)
        {
            if (node is not JsonObject block) continue;
            switch ((string?)block["type"])
            {
                case "text":
                    var text = (string?)block["text"] ?? "";
                    if (text.Length == 0) break;
                    parts.Add(new TextPart(text));
                    writer.TryWrite(new TextChunk(turnId, text));
                    break;
                case "thinking":
                    var thinking = (string?)block["thinking"] ?? "";
                    if (thinking.Length > 0) writer.TryWrite(new ReasoningChunk(turnId, thinking));
                    break;
                case "tool_use":
                    var id = (string?)block["id"] ?? Guid.NewGuid().ToString("N");
                    var name = (string?)block["name"] ?? "tool";
                    var input = block["input"]?.DeepClone() as JsonObject ?? new JsonObject();
                    toolNames[id] = (name, DateTime.UtcNow);
                    parts.Add(new ToolUsePart(id, name, input));
                    writer.TryWrite(new ToolCallStarted(turnId, id, name, input.DeepClone() as JsonObject ?? new JsonObject()) { SideEffect = NativeToolSideEffects.Claude(name) });
                    break;
            }
        }
        if (parts.Count > 0) transcript.Add(new ChatMessage(ChatRole.Assistant, parts));
    }

    internal static void HandleToolResults(JsonObject evt, string turnId, ChannelWriter<TurnEvent> writer, List<ChatMessage> transcript, Dictionary<string, (string Name, DateTime Started)> toolNames)
    {
        if (evt["message"] is not JsonObject message || message["content"] is not JsonArray content) return;
        var parts = new List<ChatContentPart>();
        foreach (var node in content)
        {
            if (node is not JsonObject block || (string?)block["type"] != "tool_result") continue;
            var id = (string?)block["tool_use_id"] ?? "";
            var isError = (bool?)block["is_error"] ?? false;
            var result = NativeLaneSupport.FlattenContent(block["content"]);
            var (name, startedAt) = toolNames.TryGetValue(id, out var known) ? known : ("tool", DateTime.UtcNow);
            parts.Add(new ToolResultPart(id, result, isError));
            writer.TryWrite(new ToolCallCompleted(turnId, id, name, result, isError, DateTime.UtcNow - startedAt) { SideEffect = NativeToolSideEffects.Claude(name) });
        }
        if (parts.Count > 0) transcript.Add(new ChatMessage(ChatRole.Tool, parts));
    }

    internal static string? ResolveExecutable()
    {
        try { return NativeCliCapabilities.ResolveExecutable(SubscriptionCliDiscovery.ResolveExecutableCandidates(SubscriptionCliCatalog.ClaudeCodeProvider)); }
        catch { return null; }
    }

    /// <summary>Kept for callers that only need the prompt shape; the runner passes the configured bridge.</summary>
    internal static string BuildPrompt(NativeLaneTurn turn, bool isFirstTurn, McpServerLaunch? mcp = null)
        => NativeLaneSupport.BuildPrompt(turn, isFirstTurn, mcp, "CLAUDE.md");

    /// <summary>Claude Code 2.1.248+: drops command-running tools and WebFetch, ignores settings files, confines file tools.</summary>
    public const string RestrictedFlag = "--restricted";

    /// <summary>The only tools a confined Claude Code lane has: reading, searching and editing files. No Bash, PowerShell, web or MCP.</summary>
    public const string ConfinedTools = "Read,Edit,MultiEdit,Write,Glob,Grep";
    public const string ConfinedDisallowedTools = "Bash,PowerShell,WebFetch,WebSearch,mcp__*";
    /// <summary>File-changing tools removed from a plan-mode turn in addition to the permission mode.</summary>
    public const string PlanDisallowedTools = "Edit,MultiEdit,Write,NotebookEdit";

    /// <summary>
    /// The per-launch <c>--mcp-config</c> file: the vault bridge plus external servers when unconfined; none at
    /// all when confined (the confined arguments carry <c>--strict-mcp-config</c>).
    /// </summary>
    internal static string? WriteMcpConfigFor(NativeLaneContainment containment, McpServerLaunch? bridge, string repositoryRoot,
        IReadOnlyList<McpServerLaunch>? external, string? conversationId = null)
        => containment == NativeLaneContainment.WorkspaceConfined
            ? null
            : NativeLaneSupport.WriteClaudeMcpConfig(bridge, repositoryRoot, external, conversationId);

    /// <summary>Settings for a confined launch on a CLI without <see cref="RestrictedFlag"/>: no hooks from any settings source.</summary>
    internal const string ConfinedSettings = "{\"disableAllHooks\":true}";

    /// <param name="settingsPath">A file holding <see cref="ConfinedSettings"/>, for a launcher that cannot carry the inline JSON (a Windows shim).</param>
    internal static ProcessStartInfo BuildStartInfo(string executable, NativeLaneTurn turn, string? sessionId, string? mcpConfigPath = null,
        NativeLaneContainment containment = NativeLaneContainment.Bypass, bool restrictedSupported = false,
        bool allowClaudeAiConnectors = false, string? settingsPath = null)
    {
        var args = new List<string>
        {
            "-p",
            "--output-format", "stream-json",
            "--verbose",
        };
        if (containment == NativeLaneContainment.WorkspaceConfined)
        {
            // No tool that runs commands or code exists in the session (so no git push, no shell), no user
            // or project settings/hooks/allow rules, no MCP servers; file edits are auto-accepted and, with
            // --restricted, confined to the workspace. --restricted is preferred when the CLI offers it.
            if (restrictedSupported) args.Add(RestrictedFlag);
            else
            {
                args.Add("--setting-sources"); args.Add("project");
                args.Add("--settings"); args.Add(settingsPath ?? ConfinedSettings);
            }
            args.Add("--strict-mcp-config");
            args.Add("--permission-mode"); args.Add(turn.PlanMode ? "plan" : "acceptEdits");
            args.Add("--tools"); args.Add(ConfinedTools);
            args.Add("--disallowedTools"); args.Add(turn.PlanMode ? ConfinedDisallowedTools + "," + PlanDisallowedTools : ConfinedDisallowedTools);
            args.Add("--add-dir"); args.Add(turn.RepositoryRoot);
        }
        else if (turn.PlanMode)
        {
            // Plan mode: Claude Code's own read-only planning mode, and (whatever the person's settings allow) no tool
            // that edits files or runs commands. Only this launch's MCP config loads (--strict-mcp-config): at most
            // SwarmCode's bridge, which the host restricts to tools that change nothing for a plan turn.
            args.Add("--strict-mcp-config");
            args.Add("--permission-mode"); args.Add("plan");
            args.Add("--disallowedTools"); args.Add(string.IsNullOrWhiteSpace(mcpConfigPath)
                ? "Bash,PowerShell,mcp__*," + PlanDisallowedTools
                : "Bash,PowerShell," + PlanDisallowedTools);
        }
        else
        {
            args.Add("--permission-mode"); args.Add("bypassPermissions");
        }
        if (!string.IsNullOrWhiteSpace(sessionId)) { args.Add("--resume"); args.Add(sessionId); }
        if (turn.ExecutionLimits is { MaxIterations: > 0 } limits)
        { args.Add("--max-turns"); args.Add(limits.MaxIterations.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        var model = NormalizeModel(turn.Lane.Model);
        foreach (var folder in turn.Attachments.Select(a => Path.GetDirectoryName(a.LocalPath)!).Distinct()) { args.Add("--add-dir"); args.Add(folder); }
        if (model is not null) { args.Add("--model"); args.Add(model); }
        if (!string.IsNullOrWhiteSpace(mcpConfigPath)) { args.Add("--mcp-config"); args.Add(mcpConfigPath); }

        // A Windows npm shim is started through the hardened batch bridge at launch (NativeLaneSupport.ForLaunch).
        var psi = NativeLaneSupport.TurnStartInfo(executable, args, turn.RepositoryRoot);
        NativeLaneSupport.ScrubApiKeys(psi.EnvironmentVariables);
        psi.Environment["CLAUDE_CODE_ENTRYPOINT"] = "swarmcode";
        // Claude Code loads the connectors linked on the user's claude.ai account by default. A run would then
        // read the person's live mail or drive instead of the data the host connected for the task, as a
        // SwarmCode run did with Gmail. They load only when the person allowed them and the run is unconfined.
        if (containment == NativeLaneContainment.WorkspaceConfined || !allowClaudeAiConnectors)
            psi.Environment[ClaudeAiConnectorsVariable] = "false";
        else
            psi.Environment.Remove(ClaudeAiConnectorsVariable);
        return psi;
    }

    /// <summary>Claude Code's switch for the connectors linked on the user's claude.ai account.</summary>
    public const string ClaudeAiConnectorsVariable = "ENABLE_CLAUDEAI_MCP_SERVERS";

    /// <summary>"claude-code:default" means the CLI's own default; "claude-code:opus" pins a model alias.</summary>
    internal static string? NormalizeModel(string model)
    {
        var value = model;
        var colon = value.IndexOf(':');
        if (colon >= 0) value = value[(colon + 1)..];
        value = value.Trim();
        return value.Length == 0 || value.Equals("default", StringComparison.OrdinalIgnoreCase) ? null : value;
    }

    /// <summary>Anchored on the CLI's own wording so a repository path such as Login.razor in stderr does not cool the lane down.</summary>
    internal static LlmFailureCategory? ClassifyText(string? detail) => NativeLaneSupport.ClassifyCliFailure(detail);
}
