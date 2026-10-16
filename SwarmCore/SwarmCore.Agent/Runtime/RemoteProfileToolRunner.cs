using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.ToolUseEngine;

namespace SwarmCore.Agent.Runtime;

public sealed record RemoteProfileInvocation(Guid ProfileId, Guid GrantId, Guid OperationId, string UserRequest, bool ReadOnly)
{
    public RemoteToolReach Reach { get; init; } = RemoteToolReach.ConnectedProfile;

    /// <summary>Conversation key prefix and authority name recorded on the turn context.</summary>
    public string Authority { get; init; } = "explicit-profile-connection";
}
public sealed record RemoteProfileOperationReceipt(string ToolId, Guid OperationId, ToolSideEffect SideEffect, string State, string? Error = null)
{
    /// <summary>The request the connected client stated for this action (its words, not verified as the person's).</summary>
    public string? Request { get; init; }

    /// <summary>The tool input the client sent, for the host's receipt. Hosts sanitize it before storing it.</summary>
    public JsonObject? Input { get; init; }
}

/// <summary>Shared execution over the same guard, authority context and durable scheduler as local assistant tools.
/// Hosts supply profile identity, live connection validation and their action ledger.</summary>
public sealed class RemoteProfileToolRunner(IServiceProvider services, IToolSchedulerFactory schedulers, ICurrentTurnContextAccessor turns)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<object> RunAsync(ToolDescriptor descriptor, JsonObject input, RemoteProfileInvocation invocation,
        Func<CancellationToken, Task> validateAuthority, Func<RemoteProfileOperationReceipt, CancellationToken, Task> recordReceipt, CancellationToken ct)
    {
        if (invocation.ProfileId == Guid.Empty || invocation.GrantId == Guid.Empty || invocation.OperationId == Guid.Empty
            || string.IsNullOrWhiteSpace(invocation.UserRequest)) throw new InvalidOperationException("Profile operation authority is required.");
        var typed = RemoteProfileToolPolicy.ReadInput(descriptor, input, invocation.Reach);
        var sideEffect = descriptor.EffectiveSideEffect(typed);
        if (sideEffect == ToolSideEffect.Exec || (invocation.ReadOnly && sideEffect != ToolSideEffect.ReadOnly))
            throw new InvalidOperationException("The read operation cannot change data.");
        var conversation = "remote-profile:" + invocation.GrantId.ToString("N");
        var context = new CurrentTurnContext(conversation, invocation.OperationId, invocation.ProfileId, UserMessage: invocation.UserRequest,
            ToolAccessState: new(), CommunicationScopeKind: AgentCommunicationScopeKind.TrustedProfileWide,
            CommunicationScopeAuthority: invocation.Authority, CommunicationScopeUserInitiated: true)
            { AdmittedToolIds = new HashSet<string>(StringComparer.Ordinal) { descriptor.ToolId } };
        var session = new AgentSession
        {
            ConversationId = conversation, TurnId = invocation.OperationId.ToString("D"), TurnContext = context,
            UserRequestAuthorized = true, Transcript = [ChatMessage.Text(ChatRole.User, invocation.UserRequest)], Tools = [descriptor],
            Profile = new() { Id = invocation.ProfileId, Name = "Connected profile", SystemPrompt = "", EnabledToolIds = [descriptor.ToolId] }
        };
        await validateAuthority(ct);
        var scheduler = schedulers.Create(session);
        // Writes retain their durable operation identity. Reads must inspect current source
        // state again, even on transport retry, so deletion or disabling cannot replay old data.
        var scheduleIdentity = ToolScheduleIdentity.For(session);
        if (sideEffect == ToolSideEffect.ReadOnly) scheduleIdentity = scheduleIdentity with { Turn = Guid.NewGuid().ToString("D") };
        var calls = await scheduler.RunAsync(scheduleIdentity, [new ToolScheduleRequest("operation", descriptor.ToolId, input,
            descriptor.Scheduling?.Invoke(input) ?? new ToolSchedulingCapability(), async token =>
            {
                try { await validateAuthority(token); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { return new("The profile connection was revoked or is unavailable.", true, false); }
                var prior = turns.Current;
                turns.Current = context;
                try
                {
                    if (sideEffect != ToolSideEffect.ReadOnly)
                    {
                        foreach (var guard in services.GetServices<IToolInvocationGuard>())
                        {
                            var decision = await guard.InspectAsync(new(descriptor.ToolId, sideEffect, typed, conversation, session.Transcript), token);
                            if (!decision.Allowed || decision.RequiresApproval)
                                return new("The existing action checks require clarification. " + decision.BlockReason, true, false);
                        }
                    }
                    using var approval = ToolApprovalContextAccessor.Push(new("profile-operation:" + invocation.OperationId.ToString("D"),
                        true, invocation.Authority, "operation", context.ToolAccessState, invocation.UserRequest));
                    await Record("planned", null, token);
                    var output = await descriptor.Invoke(typed, token);
                    var serialized = JsonSerializer.Serialize(output, Json);
                    using var result = JsonDocument.Parse(serialized);
                    var failed = result.RootElement.ValueKind == JsonValueKind.Object &&
                        ((result.RootElement.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
                        || (result.RootElement.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True));
                    await Record(failed ? "failed" : "applied", null, token);
                    return new(serialized, failed, true, LocalProcessCompleted: true);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { await Record("failed", ex.GetType().Name, CancellationToken.None); throw; }
                finally { turns.Current = prior; }
            })], ct);
        var call = calls.Single();
        return new { operationId = invocation.OperationId, tool = descriptor.ToolId, state = call.State.ToString(),
            result = call.Result?.Content, isError = call.Result?.IsError ?? true, wasDispatched = call.Result?.WasDispatched ?? false,
            instructions = call.State == ScheduledToolState.Uncertain ? "Outcome is uncertain. Inspect the actual target; do not repeat this action with a fresh operation ID." : null };

        Task Record(string state, string? error, CancellationToken token) => recordReceipt(new(descriptor.ToolId, invocation.OperationId, sideEffect, state, error), token);
    }
}
