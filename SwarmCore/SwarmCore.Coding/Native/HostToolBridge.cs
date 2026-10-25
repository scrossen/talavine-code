using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;
using SwarmCore.Agent.ToolUseEngine;

namespace SwarmCore.Coding.Native;

/// <summary>Which of a conversation's tools a native agent may reach through the host tool bridge for its current turn.</summary>
public enum HostToolProfile
{
    /// <summary>Every host tool the conversation's profile offers, minus what the CLI has natively.</summary>
    Full,

    /// <summary>A plan turn: only tools that change nothing (<see cref="Planning.CodingPlanMode.Admits"/>).</summary>
    Plan,
}

/// <summary>One host tool as the bridge process advertises it to the native CLI.</summary>
public sealed record HostToolManifest(string Name, string Description, JsonObject InputSchema, bool ReadOnly);

/// <summary>An image a tool returned (a screenshot), carried beside the text result.</summary>
public sealed record HostToolImage(string Base64, string MediaType);

/// <summary>A host tool's result: the text the model reads, any images, and whether it reports a failure.</summary>
public sealed record HostToolResult(string Text, IReadOnlyList<HostToolImage> Images, bool IsError)
{
    // Physical adapter evidence travels separately from text. Native receipt enrichment only
    // accepts the same-process journal; echoing these fields in tool output grants no evidence.
    public bool WasDispatched { get; init; } = true;
    public bool LocalProcessCompleted { get; init; }
    public ToolSideEffect? SideEffect { get; init; }
}

/// <summary>
/// Which host tools a native agent is offered and under what MCP names. The CLI already edits files, searches code and runs
/// git and shell commands itself, so mutating equivalents are left out. Structured file reads and searches stay available
/// to retain host-owned read-only receipts when parsing fails; the person's external MCP servers are mounted
/// on the CLI directly, so their projections are left out too. Everything else the conversation's profile offers (agents,
/// task list, background shells, schedules, GitHub, notebooks, web, apps, Chrome and desktop, credentials, memory) is served.
/// </summary>
public static class HostToolBridgeCatalog
{
    /// <summary>Native equivalents except structured reads. <c>shell.exec</c> stays: it expands vault references in commands.</summary>
    public static readonly IReadOnlySet<string> NativeEquivalentToolIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "code.edit_file", "fs.write_file",
        "git.status", "git.diff", "git.log", "git.commit", "harness.decline",
    };

    /// <summary>The MCP tool name for a host tool id: <c>agents.spawn</c> becomes <c>agents_spawn</c>, <c>personal-memory.list</c> <c>personal_memory_list</c>.</summary>
    public static string McpName(string toolId)
    {
        var name = new StringBuilder(toolId.Length);
        foreach (var c in toolId) name.Append(char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_');
        return name.Length <= 64 ? name.ToString() : name.ToString(0, 64);
    }

    /// <summary>True for a tool a plan-mode CLI may call: it changes nothing the person could need to undo.</summary>
    public static bool IsReadOnly(ToolDescriptor tool) => Planning.CodingPlanMode.Admits(tool);

    /// <summary>The tools served for a profile, keyed by unique MCP name (a later duplicate name is dropped).</summary>
    public static IReadOnlyDictionary<string, ToolDescriptor> Select(IEnumerable<ToolDescriptor> tools, HostToolProfile profile)
    {
        var selected = new Dictionary<string, ToolDescriptor>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            if (NativeEquivalentToolIds.Contains(tool.ToolId) || tool.ToolId.StartsWith("mcp.", StringComparison.OrdinalIgnoreCase)) continue;
            if (profile == HostToolProfile.Plan && !IsReadOnly(tool)) continue;
            selected.TryAdd(McpName(tool.ToolId), tool);
        }
        return selected;
    }

    public static HostToolManifest Manifest(string name, ToolDescriptor tool)
        // A descriptor's own schema is used when it is an object; anything else (or none) is generated from the input type.
        => new(name, tool.Description, tool.InputSchema?.DeepClone() as JsonObject ?? ToolSchemaGenerator.Generate(tool.InputType), IsReadOnly(tool));
}

/// <summary>
/// The host's side of a native agent's tool bridge, in host terms: which turn is running for a conversation, the tools
/// that conversation's profile offers, the pre-dispatch guard, and the ambient context a call runs in.
/// </summary>
public interface IHostToolBridgeHost
{
    /// <summary>The profile for the conversation's running turn, or null when it is not running a turn here.</summary>
    HostToolProfile? Admit(string conversationId);

    /// <summary>The tools the conversation's profile offers (the same set an API lane would get).</summary>
    Task<IReadOnlyList<ToolDescriptor>> ToolsAsync(string conversationId, CancellationToken ct);

    /// <summary>The same pre-dispatch check an API lane's call gets (permissions, hooks, repeated calls).</summary>
    Task<ToolGuardDecision> InspectAsync(ToolInvocationRequest request, CancellationToken ct);

    /// <summary>Makes the conversation's turn ambient for one call (turn context, memory context). Dispose to end it.</summary>
    IDisposable BeginCall(string conversationId);
}

/// <summary>Private native process admission; a conversation id alone cannot borrow a later turn's authority.</summary>
public interface IHostToolBridgeAttemptHost
{
    IDisposable? BeginAttempt(string conversationId, string? capability);
    bool IsAttemptCurrent(string conversationId);
    CancellationToken AttemptCancellation(string conversationId);
}

/// <summary>
/// A host built from a service provider: <see cref="IProfileResolver"/> for the tools, <see cref="IToolInvocationGuard"/>
/// for the guard, and <see cref="ICurrentTurnContextAccessor"/> for the ambient owner. The host supplies admission and
/// any extra ambient state (for example its personal-memory turn).
/// </summary>
public sealed class ServiceHostToolBridgeHost(
    IServiceProvider services,
    Func<string, HostToolProfile?> admit,
    Func<string, IDisposable?>? beginExtra = null) : IHostToolBridgeHost, IHostToolBridgeAttemptHost
{
    public HostToolProfile? Admit(string conversationId) => admit(conversationId);

    public IDisposable? BeginAttempt(string conversationId, string? capability)
    {
        var active = services.GetService<ActiveCodingWorkspaces>();
        var binding = active?.BindBridgeAttempt(conversationId, capability);
        if (binding is null) return null;
        var permissions = services.GetService<Permissions.ActiveCodingPermissions>()?.BindBridgePolicy(conversationId, active!.BridgePermissions(conversationId));
        return new Scope(() => { permissions?.Dispose(); binding.Dispose(); });
    }
    public bool IsAttemptCurrent(string conversationId) => services.GetService<ActiveCodingWorkspaces>()?.IsBridgeAttemptCurrent(conversationId) == true;
    public CancellationToken AttemptCancellation(string conversationId)
        => services.GetService<ActiveCodingWorkspaces>()?.BridgeAttemptCancellation(conversationId) ?? new(true);


    public async Task<IReadOnlyList<ToolDescriptor>> ToolsAsync(string conversationId, CancellationToken ct)
    {
        using var _ = HostToolBridgeScope.Begin();
        var tools = (await services.GetRequiredService<IProfileResolver>().ResolveAsync(conversationId, ct: ct).ConfigureAwait(false)).Tools;
        // An agent role's allow list (host state for the running turn) narrows what a native lane may call through the bridge.
        var active = services.GetService<ActiveCodingWorkspaces>();
        return active is null || active.AllowedTools(conversationId).Count == 0 ? tools
            : tools.Where(t => active.AdmitsTool(conversationId, t.ToolId)).ToList();
    }

    public Task<ToolGuardDecision> InspectAsync(ToolInvocationRequest request, CancellationToken ct)
        => services.GetService<IToolInvocationGuard>() is { } guard ? guard.InspectAsync(request, ct) : Task.FromResult(ToolGuardDecision.Allow());

    public IDisposable BeginCall(string conversationId)
    {
        var turns = services.GetService<ICurrentTurnContextAccessor>();
        var prior = turns?.Current;
        // The native lane runs only under the person's own request (SwarmCode policy), so the bridged call carries it too.
        // The conversation's workspace for this turn (an isolated agent's worktree, for example) roots sandboxed paths.
        if (turns is not null)
            turns.Current = new CurrentTurnContext(conversationId, null, null,
                CommunicationScopeKind: AgentCommunicationScopeKind.TrustedProfileWide, CommunicationScopeAuthority: "native-bridge",
                CommunicationScopeUserInitiated: true)
            {
                WorkspaceRoot = services.GetService<ActiveCodingWorkspaces>()?.Get(conversationId)?.FullRoot,
                GoalMessageGoalId = services.GetService<ActiveCodingWorkspaces>()?.GoalMessageGoalId(conversationId),
                GoalMessageLeaseToken = services.GetService<ActiveCodingWorkspaces>()?.GoalMessageLeaseToken(conversationId),
            };
        var extra = beginExtra?.Invoke(conversationId);
        return new Scope(() =>
        {
            extra?.Dispose();
            if (turns is not null) turns.Current = prior;
        });
    }

    private sealed class Scope(Action end) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) end(); }
    }
}

public static class HostToolBridgeServiceCollectionExtensions
{
    /// <summary>
    /// The host tool bridge for native lanes: one broker serving the conversation's own profile tools (minus what the CLI
    /// has natively) while it runs a coding turn, with the plan subset during a plan turn (both read from
    /// <see cref="ActiveCodingWorkspaces"/>, which the orchestrator fills for exactly the turn's duration), and the bridge
    /// endpoint handed to the configured MCP bridge command. <paramref name="beginExtra"/> adds host ambient state to a
    /// call (for example a personal-memory turn).
    /// </summary>
    public static IServiceCollection AddSwarmCoreHostToolBridge(this IServiceCollection services,
        Func<IServiceProvider, string, IDisposable?>? beginExtra = null)
    {
        services.TryAddSingleton(sp =>
        {
            var active = sp.GetRequiredService<ActiveCodingWorkspaces>();
            var host = new ServiceHostToolBridgeHost(sp,
                id => active.Get(id) is null ? null : active.IsPlanTurn(id) ? HostToolProfile.Plan : HostToolProfile.Full,
                beginExtra is null ? null : id => beginExtra(sp, id));
            return new HostToolBridgeBroker(host, logger: sp.GetService<ILogger<HostToolBridgeBroker>>());
        });
        if (!services.Any(d => d.ImplementationType == typeof(HostToolBridgeNativeOptions)))
            services.AddSingleton<Microsoft.Extensions.Options.IPostConfigureOptions<CodingOptions>, HostToolBridgeNativeOptions>();
        return services;
    }
}

/// <summary>Hands the native agents' MCP bridge this host's tool endpoint (only when a bridge command is configured).</summary>
internal sealed class HostToolBridgeNativeOptions(IServiceProvider services) : Microsoft.Extensions.Options.IPostConfigureOptions<CodingOptions>
{
    // The broker is resolved here, not injected: its host reads the coding options this hook configures.
    public void PostConfigure(string? name, CodingOptions options)
    {
        if (options.NativeAgents.McpServer is { IsConfigured: true } mcp && !string.IsNullOrWhiteSpace(mcp.Command))
            mcp.Environment[HostToolBridgeBroker.PipeEnvironmentVariable] = services.GetRequiredService<HostToolBridgeBroker>().GetEndpoint();
    }
}

/// <summary>
/// Ambient while the host resolves a profile for a native agent's bridge. Profile decorators that start resources the
/// native CLI owns itself (the person's external MCP servers, which the CLI mounts directly) skip that work.
/// </summary>
public static class HostToolBridgeScope
{
    private static readonly AsyncLocal<bool> Current = new();

    public static bool Active => Current.Value;

    public static IDisposable Begin()
    {
        var prior = Current.Value;
        Current.Value = true;
        return new Restore(prior);
    }

    private sealed class Restore(bool prior) : IDisposable
    {
        public void Dispose() => Current.Value = prior;
    }
}

internal static class HostToolBridgeWire
{
    public const string EndpointPrefix = "swarmcode-tools-";
    public const int MaxRequestBytes = 1024 * 1024;
    public const int MaxResponseBytes = 32 * 1024 * 1024; // screenshots travel as images
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 64 };

    public sealed record Request(string Operation, string ConversationId, string? Tool = null, JsonElement? Input = null, string? AttemptCapability = null);

    public sealed record Response(bool Ok, string? Error, IReadOnlyList<HostToolManifest>? Tools = null, HostToolResult? Result = null);
}

/// <summary>
/// The one local pipe a native agent's MCP bridge process uses to reach the host. The bridge process is a thin adapter: it
/// lists the tools the host offers for its conversation and forwards each call; every tool runs here, in the host, under
/// that conversation's ambient turn with the same guard and hooks an API lane's call gets. A conversation is identified by
/// the host at launch (<see cref="NativeLaneSupport.ConversationEnvironmentVariable"/>), never by the model, and is served
/// only while it is running a turn here, with the profile the host decides for that turn (a plan turn gets only tools
/// that change nothing). Current-user-only pipe; one request per connection; a dropped connection cancels the call.
/// </summary>
public sealed class HostToolBridgeBroker : IAsyncDisposable, IDisposable
{
    public const string PipeEnvironmentVariable = "SWARMCODE_HOST_TOOLS_PIPE";
    public const string AttemptEnvironmentVariable = "SWARMCODE_HOST_TOOLS_ATTEMPT";
    private readonly AsyncLocal<bool> _boundWire = new();
    private bool AttemptCurrent(string conversationId) => !_boundWire.Value
        || _host is IHostToolBridgeAttemptHost attempt && attempt.IsAttemptCurrent(conversationId);
    private static readonly JsonSerializerOptions ToolJson = ToolUseAgentEngine.CreateToolJsonOptions();
    private readonly IHostToolBridgeHost _host;
    private readonly TimeSpan _requestTimeout;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _slots = new(16, 16);
    private readonly ConcurrentDictionary<long, Task> _clients = new();
    private readonly object _gate = new();
    private readonly string _endpoint;
    private readonly string? _privateDirectory;
    private Task? _listener;
    private long _nextClient;
    private bool _disposed;

    /// <param name="requestTimeout">Upper bound for one call; default 30 minutes (long commands run through shell.exec).</param>
    public HostToolBridgeBroker(IHostToolBridgeHost host, TimeSpan? requestTimeout = null, ILogger<HostToolBridgeBroker>? logger = null)
    {
        _host = host;
        _requestTimeout = requestTimeout ?? TimeSpan.FromMinutes(30);
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        (_endpoint, _privateDirectory) = LocalPipeEndpoint.Create(HostToolBridgeWire.EndpointPrefix);
    }

    /// <summary>The endpoint to hand to the bridge process; starts listening on first use.</summary>
    public string GetEndpoint()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _listener ??= Task.Run(ListenAsync);
            return _endpoint;
        }
    }

    /// <summary>The tools offered for a conversation's running turn (also the unit-test seam).</summary>
    public async Task<IReadOnlyList<HostToolManifest>?> ListAsync(string conversationId, CancellationToken ct)
    {
        if (!Agents.AgentIds.IsValid(conversationId) || _host.Admit(conversationId) is not { } profile) return null;
        var tools = HostToolBridgeCatalog.Select(await _host.ToolsAsync(conversationId, ct).ConfigureAwait(false), profile);
        if (!AttemptCurrent(conversationId)) return null;
        return tools.Select(t => HostToolBridgeCatalog.Manifest(t.Key, t.Value)).ToList();
    }

    /// <summary>Runs one tool for a conversation's running turn (also the unit-test seam).</summary>
    public async Task<HostToolResult> CallAsync(string conversationId, string name, JsonElement? arguments, CancellationToken ct)
    {
        if (!Agents.AgentIds.IsValid(conversationId) || _host.Admit(conversationId) is not { } profile)
            return NotDispatched("Talavine Code tools are available only while this Talavine Code conversation is running a turn.");
        var tools = HostToolBridgeCatalog.Select(await _host.ToolsAsync(conversationId, ct).ConfigureAwait(false), profile);
        if (!AttemptCurrent(conversationId)) return NotDispatched("This native worker attempt is no longer active.");
        if (!tools.TryGetValue(name, out var tool))
            return NotDispatched(profile == HostToolProfile.Plan ? $"{name} is not available in plan mode." : $"Unknown Talavine Code tool {name}.");

        object input;
        try
        {
            var json = arguments is { ValueKind: JsonValueKind.Object } a ? a.GetRawText() : "{}";
            input = JsonSerializer.Deserialize(json, tool.InputType, ToolJson) ?? throw new JsonException("missing input");
        }
        catch (JsonException ex) { return NotDispatched("Invalid input for " + name + ": " + ex.Message); }

        using var scope = _host.BeginCall(conversationId);
        var effect = tool.EffectiveSideEffect(input);
        var decision = await _host.InspectAsync(new ToolInvocationRequest(tool.ToolId, effect, input, conversationId), ct)
            .ConfigureAwait(false);
        if (!decision.Allowed) return NotDispatched(decision.BlockReason ?? "Talavine Code refused this call.", effect);
        // No one can answer an approval prompt on a native agent's call: escalation refuses with the reason.
        if (decision.RequiresApproval) return NotDispatched((decision.BlockReason ?? "This call needs the person's approval.") + " Ask the person to run it themselves.", effect);

        if (!AttemptCurrent(conversationId)) return NotDispatched("This native worker attempt is no longer active.", effect);
        ct.ThrowIfCancellationRequested();
        object? value;
        try { value = await tool.Invoke(input, ct).ConfigureAwait(false); }
        // Exactly what an API lane's model reads for the same failure (ToolUseAgentEngine): the refusal text for a call
        // that never ran, else the exception type and message. Logs keep only the type.
        catch (ToolCallNotDispatchedException ex) { return NotDispatched(ex.Message, effect); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Bridged tool {Tool} threw ({Error}).", tool.ToolId, ex.GetType().Name);
            return Error($"{ex.GetType().Name}: {ex.Message}") with { SideEffect = effect };
        }
        // The CLI reads the annotated text (hook notes included); images and failure-as-data come from the tool's own object.
        var own = ToolResultAnnotations.Unwrap(value);
        var images = own is IToolResultImages withImages
            ? withImages.Images.Select(i => new HostToolImage(i.Base64Data, i.MediaType)).ToList()
            : [];
        var text = value switch
        {
            null => "null",
            string s => s,
            _ => JsonSerializer.Serialize(value, value.GetType(), ToolJson),
        };
        return new HostToolResult(text, images, IsFailure(own))
        {
            SideEffect = effect,
            WasDispatched = own is not SwarmCore.Abstractions.Tools.IToolDispatchEvidence { NoPhysicalAction: true },
            LocalProcessCompleted = own is IToolExecutionEvidence { LocalProcessCompleted: true }
                && effect is not (ToolSideEffect.ExternalSend or ToolSideEffect.Destructive),
        };
    }

    private static HostToolResult Error(string message) => new(message, [], true);
    private static HostToolResult NotDispatched(string message, ToolSideEffect? effect = null)
        => Error(message) with { WasDispatched = false, SideEffect = effect };

    /// <summary>Results that report a failure as data (Success/Succeeded false) are marked as errors for the CLI.</summary>
    private static bool IsFailure(object? value)
    {
        if (value is null || value is string) return false;
        var type = value.GetType();
        foreach (var name in new[] { "Success", "Succeeded", "Ok" })
            if (type.GetProperty(name)?.GetValue(value) is false) return true;
        return false;
    }

    private async Task<HostToolBridgeWire.Response> HandleAsync(HostToolBridgeWire.Request request, CancellationToken ct)
    {
        using var attempt = _host is IHostToolBridgeAttemptHost boundHost
            ? boundHost.BeginAttempt(request.ConversationId, request.AttemptCapability) : null;
        if (attempt is null)
            return new(false, "This native worker attempt is no longer active.");
        using var lifetime = _host is IHostToolBridgeAttemptHost binding
            ? CancellationTokenSource.CreateLinkedTokenSource(ct, binding.AttemptCancellation(request.ConversationId)) : null;
        var prior = _boundWire.Value;
        _boundWire.Value = attempt is not null;
        try { return await HandleBoundAsync(request, lifetime?.Token ?? ct).ConfigureAwait(false); }
        finally { _boundWire.Value = prior; }
    }

    private async Task<HostToolBridgeWire.Response> HandleBoundAsync(HostToolBridgeWire.Request request, CancellationToken ct)
    {
        switch (request.Operation)
        {
            case "list":
                return await ListAsync(request.ConversationId, ct).ConfigureAwait(false) is { } tools
                    ? new(true, null, tools)
                    : new(false, "Talavine Code tools are available only while this Talavine Code conversation is running a turn.");
            case "call" when !string.IsNullOrWhiteSpace(request.Tool):
                var result = await CallAsync(request.ConversationId, request.Tool, request.Input, ct).ConfigureAwait(false);
                NativeHostReceiptJournal.Record(request.ConversationId, request.AttemptCapability,
                    request.Tool, request.Input, result);
                return new(true, null, Result: result);
            default:
                return new(false, "Unknown bridge operation.");
        }
    }

    private async Task ListenAsync()
    {
        var failures = 0;
        while (!_lifetime.IsCancellationRequested)
        {
            try { await _slots.WaitAsync(_lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(_endpoint, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_lifetime.Token).ConfigureAwait(false);
                var connected = pipe;
                pipe = null;
                failures = 0;
                var id = Interlocked.Increment(ref _nextClient);
                var task = ServeAsync(connected);
                _clients[id] = task;
                _ = task.ContinueWith(_ => _clients.TryRemove(id, out var _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                try { _slots.Release(); } catch (ObjectDisposedException) { return; }
                if (_lifetime.IsCancellationRequested) return;
                failures++;
                _logger.LogWarning("Host tool bridge accept failed ({Error}); still listening.", ex.GetType().Name);
                try { await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(2_000, 50 * failures)), _lifetime.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        using (pipe)
        using (var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token))
        {
            request.CancelAfter(_requestTimeout);
            Task? disconnect = null;
            try
            {
                var bytes = await LocalPipeEndpoint.ReadFrameAsync(pipe, HostToolBridgeWire.MaxRequestBytes, request.Token).ConfigureAwait(false);
                // One request per connection: the bridge process closing the pipe (the CLI cancelled) cancels the call.
                disconnect = WatchDisconnectAsync(pipe, request);
                HostToolBridgeWire.Response response;
                try
                {
                    var parsed = JsonSerializer.Deserialize<HostToolBridgeWire.Request>(bytes, HostToolBridgeWire.Json) ?? throw new JsonException("missing request");
                    response = await HandleAsync(parsed, request.Token).ConfigureAwait(false);
                }
                catch (JsonException) { response = new(false, "Invalid bridge request."); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Tool exception text can carry personal or credential data: report only the type.
                    _logger.LogWarning("Host tool bridge call failed ({Error}).", ex.GetType().Name);
                    response = new(true, null, Result: Error("The Talavine Code tool could not finish (" + ex.GetType().Name + "). Check its state before retrying."));
                }
                await LocalPipeEndpoint.WriteFrameAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(response, HostToolBridgeWire.Json),
                    HostToolBridgeWire.MaxResponseBytes, request.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException or EndOfStreamException) { }
            finally
            {
                request.Cancel();
                if (disconnect is not null) { try { await disconnect.ConfigureAwait(false); } catch (IOException) { } }
                _slots.Release();
            }
        }
    }

    private static async Task WatchDisconnectAsync(Stream pipe, CancellationTokenSource request)
    {
        try
        {
            await pipe.ReadAsync(new byte[1], request.Token).ConfigureAwait(false);
            request.Cancel();
        }
        catch (OperationCanceledException) { }
        catch (IOException) { request.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
        }
        if (_listener is not null) await _listener.ConfigureAwait(false);
        await Task.WhenAll(_clients.Values).ConfigureAwait(false);
        _lifetime.Dispose();
        if (_privateDirectory is not null)
        {
            try { Directory.Delete(_privateDirectory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}

/// <summary>The bridge-process side: lists and calls the host's tools for the conversation named at launch.</summary>
public sealed class HostToolBridgeProxy(string endpoint, string conversationId, string? attemptCapability = null)
{
    public string ConversationId { get; } = conversationId;

    /// <summary>The proxy for this bridge process, or null when the host handed it no tool connection.</summary>
    public static HostToolBridgeProxy? FromEnvironment()
        => Environment.GetEnvironmentVariable(HostToolBridgeBroker.PipeEnvironmentVariable) is { Length: > 0 } pipe
           && LocalPipeEndpoint.IsValid(pipe, HostToolBridgeWire.EndpointPrefix)
           && Environment.GetEnvironmentVariable(NativeLaneSupport.ConversationEnvironmentVariable) is { } conversation
           && Agents.AgentIds.IsValid(conversation)
            ? new HostToolBridgeProxy(pipe, conversation, Environment.GetEnvironmentVariable(HostToolBridgeBroker.AttemptEnvironmentVariable)) : null;

    public async Task<IReadOnlyList<HostToolManifest>> ListAsync(CancellationToken ct)
    {
        var response = await SendAsync(new HostToolBridgeWire.Request("list", ConversationId, AttemptCapability: attemptCapability), ct).ConfigureAwait(false);
        return response.Tools ?? [];
    }

    public async Task<HostToolResult> CallAsync(string name, JsonElement? arguments, CancellationToken ct)
    {
        var response = await SendAsync(new HostToolBridgeWire.Request("call", ConversationId, name, arguments, attemptCapability), ct).ConfigureAwait(false);
        return response.Result ?? new HostToolResult("The host returned no result.", [], true);
    }

    private async Task<HostToolBridgeWire.Response> SendAsync(HostToolBridgeWire.Request request, CancellationToken ct)
    {
        if (!LocalPipeEndpoint.IsValid(endpoint, HostToolBridgeWire.EndpointPrefix))
            throw new InvalidOperationException("Invalid host tool connection.");
        using var pipe = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(5000, ct).ConfigureAwait(false); }
        catch (TimeoutException) { throw new InvalidOperationException("Talavine Code is not reachable; its tools are unavailable for this run."); }
        await LocalPipeEndpoint.WriteFrameAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(request, HostToolBridgeWire.Json), HostToolBridgeWire.MaxRequestBytes, ct).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<HostToolBridgeWire.Response>(
            await LocalPipeEndpoint.ReadFrameAsync(pipe, HostToolBridgeWire.MaxResponseBytes, ct).ConfigureAwait(false), HostToolBridgeWire.Json)
            ?? throw new InvalidOperationException("The host tool connection returned no result.");
        if (!response.Ok) throw new InvalidOperationException(response.Error ?? "The Talavine Code tool connection refused the request.");
        return response;
    }
}
