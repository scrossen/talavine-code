using System.Text.Json;
using Microsoft.Extensions.Hosting;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;

namespace SwarmCore.Coding.Peers;

public sealed record PeersListInput(PeerRequirements? Requirements = null);
public sealed record PeerDelegateInput(string Title, string Goal, IReadOnlyList<string> SuccessCriteria,
    string? Peer = null, PeerRequirements? Requirements = null, string? Context = null,
    IReadOnlyList<string>? ExpectedArtifacts = null, IReadOnlyList<string>? JobArguments = null);
public sealed record PeerWorkInput(string WorkId, long? Since = null, int TimeoutSeconds = 50);

public static class PeerTools
{
    public const string Guidance = "Peers: when the person requests a swarm or delegation across their devices, use peers.list to discover paired "
        + "computers and their fresh OS, architecture, CPU, RAM, GPUs, local inference providers/models, and jobs. Use peers.delegate for each "
        + "independent piece, specifying requirements (platform, architecture, memory, GPU, local inference, provider/model) or an exact peer id. "
        + "The least busy matching peer is selected. Use executionMode=job and an advertised job for repeatable builds/checks; do not spend "
        + "a model turn executing an available job. Give a complete goal, source revision, how the receiver obtains source, constraints, success "
        + "criteria, and expected artifacts. Local paths do not identify remote paths. Transfer selected source/patches using peers.send_file "
        + "and confirm completion with peers.transfer_status, or use a receiver-owned checkout/job. Do not share credentials or assume dirty "
        + "files are present remotely. Keep one writer per checkout; require isolated branches/patches for parallel edits and review them "
        + "before integration. Continue local work and collect with peers.wait or peers.work. Keep following required assignments until they "
        + "finish or report a concrete blocker; do not end the project merely because dispatch succeeded. A timeout or stale status does not prove "
        + "the remote process stopped: reconcile peers.work before retrying an assignment. Results and artifact paths are evidence to "
        + "verify; a launch is not completion. Even a terminal Completed status can mean an agent turn ended while a detached process "
        + "continues. Check the returned goal, successCriteria, expectedArtifacts and completionNotice: require actual artifacts and "
        + "acceptance evidence before treating that part as done. Keep short component tests in the foreground; use a receiver-owned "
        + "job for long deterministic builds so process exit owns completion. peers.cancel requests a stop but inspect the returned state before reassigning. Only delegate "
        + "work within the person's requested swarm; do not start unrelated work or nested peer swarms. SwarmSpan owns pairing and remote "
        + "authorization; use its normal pairing/settings workflow if it reports missing setup.";

    public static IReadOnlyList<ToolDescriptor> Create(PeerCoordinator peers, Func<string?> owner, Func<bool> canDelegate) =>
    [
        ToolDescriptor.Create<PeersListInput, string>("peers.list", "coding.peers",
            "Discover paired devices with fresh hardware and provider/model capabilities and explain which satisfy requirements. Does not start work. Requires the local SwarmSpan bridge.",
            ToolCostClass.Cheap, ToolSideEffect.ReadOnly,
            (input, ct) => Result(() => peers.DiscoverAsync(input.Requirements ?? new(), ct)), keywords: ["peer", "hardware", "GPU", "swarm", "devices"]),
        ToolDescriptor.Create<PeerDelegateInput, string>("peers.delegate", "coding.peers",
            "Delegate a complete user-requested task to a paired device. Discovers fresh capabilities and picks the least busy matching peer, or the specified peer. "
            + "Returns a durable workId immediately. Specify requirements, goal, observable successCriteria, source/artifact context. Nothing is automatically merged. "
            + "A failed response may have assigned work: inspect peers.work before retrying.",
            ToolCostClass.Expensive, ToolSideEffect.Exec,
            (input, ct) => InTurn(owner, async id =>
            {
                if (!canDelegate()) throw new InvalidOperationException("Peer delegation requires a user-authorized turn and is unavailable in a child agent.");
                return await peers.DelegateAsync(id, input, ct).ConfigureAwait(false);
            }), keywords: ["peer", "delegate", "swarm", "remote", "agent"]),
        ToolDescriptor.Create<PeersListInput, string>("peers.work", "coding.peers",
            "List this conversation's durable peer assignments, progress, terminal replies and artifacts. Use after reconnecting or an uncertain dispatch to avoid duplicate work.",
            ToolCostClass.Trivial, ToolSideEffect.ReadOnly,
            (_, ct) => InTurn(owner, id => peers.WorkAsync(id, ct)), keywords: ["peer", "work", "status", "results"]),
        ToolDescriptor.Create<PeerWorkInput, string>("peers.wait", "coding.peers",
            "Wait up to 50 seconds for this conversation's peer work to change. Pass updatedUtcMs as since on subsequent waits. Returns progress or a terminal result; timeout is not failure.",
            ToolCostClass.Cheap, ToolSideEffect.ReadOnly,
            (input, ct) => InTurn(owner, id => peers.ObserveAsync(id, input, true, false, ct)), keywords: ["peer", "wait", "result"]),
        ToolDescriptor.Create<PeerWorkInput, string>("peers.cancel", "coding.peers",
            "Request cancellation of one of this conversation's peer assignments. Inspect its state before reassigning; network silence is not confirmation of a stopped process.",
            ToolCostClass.Cheap, ToolSideEffect.Exec,
            (input, ct) => InTurn(owner, async id =>
            {
                if (!canDelegate()) throw new InvalidOperationException("Canceling peer work requires a user-authorized turn.");
                return await peers.ObserveAsync(id, input, false, true, ct).ConfigureAwait(false);
            }), keywords: ["peer", "cancel", "stop"]),
    ];

    internal static Task<string> InTurn<T>(Func<string?> owner, Func<string, Task<T>> call)
        => Result(() => owner() is { Length: > 0 } id ? call(id) : throw new InvalidOperationException("Peer work requires a conversation turn."));

    internal static async Task<string> Result<T>(Func<Task<T>> call)
    {
        try { return JsonSerializer.Serialize(new { success = true, result = await call().ConfigureAwait(false) }, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or HttpRequestException or IOException or UnauthorizedAccessException)
        { return JsonSerializer.Serialize(new { success = false, error = ex is HttpRequestException ? "SwarmSpan connection failed; inspect peers.work before retrying an assignment." : ex.Message }); }
    }
}

public sealed class PeerToolRegistration(ICapabilityCatalog catalog, PeerCoordinator peers, ICurrentTurnContextAccessor turns,
    PeerToolScope scope) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var tool in PeerTools.Create(peers, () => scope.ConversationId(turns.Current), () => scope.CanDelegate(turns.Current))) catalog.Register(tool);
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Host-owned partition and delegated-turn admission, never set by model input.</summary>
public sealed record PeerToolScope(Func<CurrentTurnContext?, string?> ConversationId, Func<CurrentTurnContext?, bool> CanDelegate);
