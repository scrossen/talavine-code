using System.Text.Json.Nodes;
using SwarmCore.Agent.Communication;
using SwarmCore.Coding.Routing;

namespace SwarmCore.Coding.Native;

/// <summary>One native turn; new inbox input is admitted only when the observed operation batch is complete.
/// Steer supplies semantic direction without changing the admitted native sandbox or tool permissions.
/// Explicit Pause/Stop remains the host's hard control.</summary>
internal static class CodexGoalAttention
{
    public static async Task RunAsync(CodexAppServerConnection connection, NativeLaneTurn turn, string workerId,
        string prompt, string? resumeId, Func<JsonObject, Task> observe, Func<GoalDelivery, Task> accepted,
        CancellationToken ct)
    {
        await connection.RequestAsync("initialize", new()
        {
            ["clientInfo"] = new JsonObject { ["name"] = "swarmcode", ["version"] = "1" },
        }, ct).ConfigureAwait(false);
        await connection.NotifyAsync("initialized", ct).ConfigureAwait(false);
        var (model, effort) = CodexNativeLaneRunner.NormalizeModel(turn.Lane.Model);
        var parameters = new JsonObject
        {
            ["cwd"] = turn.RepositoryRoot, ["approvalPolicy"] = "never", ["sandbox"] = "danger-full-access",
        };
        if (model is not null) parameters["model"] = model;
        if (resumeId is not null) parameters["threadId"] = resumeId;
        var thread = await connection.RequestAsync(resumeId is null ? "thread/start" : "thread/resume", parameters, ct, TimeSpan.FromSeconds(NativeLaneSupport.ExternalStartupTimeoutSeconds + 30)).ConfigureAwait(false);
        var threadId = (string?)thread["thread"]?["id"] ?? throw new InvalidDataException("Codex did not identify the admitted thread.");
        if (resumeId is not null && threadId != resumeId) throw new InvalidDataException("Codex resumed a different thread.");
        await observe(new() { ["type"] = "thread.started", ["thread_id"] = threadId }).ConfigureAwait(false);

        GoalDelivery? initial = await TryBeginAsync(turn, workerId, ct).ConfigureAwait(false);
        var input = new JsonArray { Text(initial is null ? prompt : prompt + "\n\n" + initial.ContextText) };
        foreach (var image in turn.Attachments.Where(a => a.IsImage))
            input.Add(new JsonObject { ["type"] = "localImage", ["path"] = image.LocalPath });
        var start = new JsonObject
        {
            ["threadId"] = threadId, ["input"] = input, ["cwd"] = turn.RepositoryRoot,
            ["approvalPolicy"] = "never", ["sandboxPolicy"] = new JsonObject { ["type"] = "dangerFullAccess" },
        };
        if (model is not null) start["model"] = model;
        if (effort is not null) start["effort"] = effort;
        JsonObject started;
        try { started = await connection.RequestAsync("turn/start", start, ct).ConfigureAwait(false); }
        catch (Exception error)
        {
            if (initial is not null) await RecordFailureAsync(turn, initial, error).ConfigureAwait(false);
            // A lost start ACK must not discard physical-operation evidence already emitted by the native process.
            while (connection.Notifications.TryRead(out var pending))
            {
                if ((string?)pending["params"]?["threadId"] != threadId || pending["params"]?["item"] is not JsonObject item) continue;
                if ((string?)pending["method"] is "item/started" or "item/completed")
                    await observe(new() { ["type"] = (string?)pending["method"] == "item/started" ? "item.started" : "item.completed", ["item"] = NormalizeItem(item) }).ConfigureAwait(false);
            }
            throw;
        }
        var nativeTurnId = (string?)started["turn"]?["id"];
        if (string.IsNullOrWhiteSpace(nativeTurnId))
        {
            var error = new InvalidDataException("Codex did not identify the active turn.");
            if (initial is not null) await RecordFailureAsync(turn, initial, error).ConfigureAwait(false);
            throw error;
        }
        if (initial is not null) await AcceptedAsync(turn, initial, accepted).ConfigureAwait(false);

        var active = new HashSet<string>(StringComparer.Ordinal);
        var acceptedPending = new Queue<GoalDelivery>();
        Task<JsonObject>? next = null;
        var steerAdmitted = true;
        using var attentionTicks = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        var nextTick = attentionTicks.WaitForNextTickAsync(ct).AsTask();
        try
        {
            while (true)
            {
                // Give the wall-clock inbox tick priority even if delta notifications never stop.
                if (nextTick.IsCompleted)
                {
                    if (!await nextTick.ConfigureAwait(false)) break;
                    if (steerAdmitted) await DeliverAsync().ConfigureAwait(false);
                    nextTick = attentionTicks.WaitForNextTickAsync(ct).AsTask();
                }
                next ??= connection.Notifications.ReadAsync(ct).AsTask();
                // Native input is queued while work runs; its own loop drains after the admitted
                // batch. Already-admitted batch actions finish. This never cancels or replays tools.
                if (await Task.WhenAny(next, nextTick).ConfigureAwait(false) != next) continue;
                var frame = await next.ConfigureAwait(false); next = null;
                var body = frame["params"] as JsonObject ?? new();
                if ((string?)body["threadId"] is { } owner && owner != threadId) continue;
                var method = (string?)frame["method"];
                if (method is "item/started" or "item/completed" && body["item"] is JsonObject item)
                {
                    if ((string?)body["turnId"] != nativeTurnId) continue;
                    steerAdmitted = true;
                    var normalized = NormalizeItem(item);
                    var id = (string?)item["id"];
                    if (id is not null && IsOperation(item))
                    {
                        if (method == "item/started") active.Add(id); else active.Remove(id);
                    }
                    await observe(new() { ["type"] = method == "item/started" ? "item.started" : "item.completed", ["item"] = normalized }).ConfigureAwait(false);
                    if (active.Count == 0) await FlushAcceptedAsync().ConfigureAwait(false);
                    if (steerAdmitted) await DeliverAsync().ConfigureAwait(false);
                }
                else if (method == "turn/completed")
                {
                    var terminal = body["turn"] as JsonObject ?? new();
                    if ((string?)terminal["id"] != nativeTurnId) continue;
                    var completed = (string?)terminal["status"] == "completed";
                    await observe(completed ? new() { ["type"] = "turn.completed" }
                        : new() { ["type"] = "turn.failed", ["error"] = terminal["error"]?.DeepClone() ?? new JsonObject { ["message"] = "The native turn did not complete." } }).ConfigureAwait(false);
                    return;
                }
                else if (method == "error" && (bool?)body["willRetry"] != true)
                    await observe(new() { ["type"] = "error", ["message"] = (string?)body["error"]?["message"] ?? "Codex reported an error." }).ConfigureAwait(false);
            }
        }
        finally
        {
            // Preserve accepted input even if the native connection ends during a tool.
            // The owning neutral transcript adapter closes unknown pairs before adding it.
            await FlushAcceptedAsync().ConfigureAwait(false);
        }

        async Task FlushAcceptedAsync()
        {
            while (acceptedPending.TryDequeue(out var delivery)) await accepted(delivery).ConfigureAwait(false);
        }

        async Task DeliverAsync()
        {
            var delivery = await TryBeginAsync(turn, workerId, ct).ConfigureAwait(false);
            if (delivery is null) return;
            try
            {
                var receipt = await connection.RequestAsync("turn/steer", new()
                {
                    ["threadId"] = threadId, ["expectedTurnId"] = nativeTurnId,
                    ["input"] = new JsonArray { Text(delivery.ContextText) },
                }, ct).ConfigureAwait(false);
                if ((string?)receipt["turnId"] != nativeTurnId)
                    throw new InvalidDataException("Codex did not acknowledge the exact active turn.");
                await AcceptedAsync(turn, delivery, value => { acceptedPending.Enqueue(value); return Task.CompletedTask; }).ConfigureAwait(false);
                if (active.Count == 0) await FlushAcceptedAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                await RecordFailureAsync(turn, delivery, error).ConfigureAwait(false);
                steerAdmitted = false;
                if (error is OperationCanceledException && ct.IsCancellationRequested) throw;
                // Work continues. An ambiguous transport outcome is held durably, never replayed automatically.
            }
        }
    }

    private static JsonObject Text(string text) => new() { ["type"] = "text", ["text"] = text };

    private static async Task<GoalDelivery?> TryBeginAsync(NativeLaneTurn turn, string worker, CancellationToken ct)
    {
        try { return await GoalMailboxBoundary.BeginAsync(turn.GoalMailbox, turn.GoalId, turn.ConversationId, worker, ct: ct, expectedLeaseToken: turn.GoalMessageLeaseToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; } // A disconnected inbox must not cancel ordinary goal work.
    }

    private static async Task AcceptedAsync(NativeLaneTurn turn, GoalDelivery delivery, Func<GoalDelivery, Task> accepted)
    {
        await GoalMailboxBoundary.TryRecordAsync(turn.GoalMailbox!, delivery, GoalDeliveryState.Delivered,
            "Codex accepted this delivery into the bound native turn; worker acknowledgement is pending.", CancellationToken.None).ConfigureAwait(false);
        await accepted(delivery).ConfigureAwait(false);
    }

    private static Task RecordFailureAsync(NativeLaneTurn turn, GoalDelivery delivery, Exception error)
        => GoalMailboxBoundary.TryRecordAsync(turn.GoalMailbox!, delivery,
            IsPreAdmissionRejection(error) ? GoalDeliveryState.Received : GoalDeliveryState.Uncertain,
            IsPreAdmissionRejection(error) ? "Native transport explicitly rejected this input before acceptance; pending for a later boundary."
                : "Native input acceptance could not be established; inspect this delivery before retrying.", CancellationToken.None);

    private static bool IsPreAdmissionRejection(Exception error)
        => error is CodexAppServerRejectedException { Code: -32600 or -32601 or -32602 };

    internal static bool IsOperation(JsonObject item) => (string?)item["type"] is { } type
        && type is not ("agentMessage" or "userMessage" or "reasoning" or "plan" or "contextCompaction" or "enteredReviewMode" or "exitedReviewMode");

    internal static JsonObject NormalizeItem(JsonObject item)
    {
        var result = (JsonObject)item.DeepClone();
        result["type"] = (string?)item["type"] switch
        {
            "agentMessage" => "agent_message", "commandExecution" => "command_execution", "fileChange" => "file_change",
            "mcpToolCall" => "mcp_tool_call", "webSearch" => "web_search",
            _ when IsOperation(item) => "native_operation", var other => other,
        };
        if ((string?)result["type"] == "native_operation")
        {
            result["nativeType"] = item["type"]?.DeepClone();
            var intent = (JsonObject)item.DeepClone();
            foreach (var key in new[] { "result", "error", "aggregatedOutput", "status" }) intent.Remove(key);
            result["nativeInput"] = intent;
        }
        if (item["exitCode"] is { } exit) result["exit_code"] = exit.DeepClone();
        if (item["aggregatedOutput"] is { } output) result["aggregated_output"] = output.DeepClone();
        if ((string?)item["type"] == "commandExecution" && item["command"] is JsonArray command)
            result["command"] = string.Join(' ', command.Select(p => (string?)p));
        return result;
    }
}
