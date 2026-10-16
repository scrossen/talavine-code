using System.Text.Json;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Continual;
using SwarmCore.Missions;

namespace SwarmCore.Coding.Peers;

public interface IPeerFileBridge
{
    Task<JsonElement> FilesAsync(string peer, string path, CancellationToken ct);
    Task<JsonElement> SendFileAsync(string peer, string localPath, CancellationToken ct);
    Task<JsonElement> PullFileAsync(string peer, string remotePath, string localDest, CancellationToken ct);
    Task<JsonElement> TransferAsync(string transferId, CancellationToken ct);
}
public sealed record PeerFilesInput(string Peer, string Path = "");
public sealed record PeerSendFileInput(string Peer, string LocalPath);
public sealed record PeerPullFileInput(string Peer, string RemotePath, string LocalDestination);
public sealed record PeerTransferInput(string TransferId);

public static class PeerFileTools
{
    public static IReadOnlyList<ToolDescriptor> Create(IPeerFileBridge bridge, ISandboxPathResolver paths, Func<bool> authorized) =>
    [
        ToolDescriptor.Create<PeerFilesInput, string>("peers.files", "coding.peers",
            "Browse a paired device's shared folders. Empty path lists roots; use returned swarmPath values for OS-neutral addresses. Remote file trust still applies.",
            ToolCostClass.Cheap, ToolSideEffect.ReadOnly,
            (input, ct) => Call(() => bridge.FilesAsync(input.Peer, input.Path, ct), authorized), keywords: ["peer", "files", "browse"]),
        ToolDescriptor.Create<PeerSendFileInput, string>("peers.send_file", "coding.peers",
            "Send selected source, a patch, or logs to a paired device's SwarmSpan downloads. For folders, every file is sent: prepare a credential-free source archive first. "
            + "Returns transferId/transferIds; confirm each completes with peers.transfer_status before delegating. This does not sync a checkout.",
            ToolCostClass.Moderate, ToolSideEffect.ExternalSend,
            (input, ct) => Call(() => bridge.SendFileAsync(input.Peer, paths.Resolve(input.LocalPath), ct), authorized), keywords: ["peer", "send", "source", "patch"]),
        ToolDescriptor.Create<PeerPullFileInput, string>("peers.pull_file", "coding.peers",
            "Pull a peer's result artifact using its advertised swarmPath into a local destination folder. Returns a transfer id; verify completion and the artifact before applying any patch.",
            ToolCostClass.Moderate, ToolSideEffect.Exec,
            (input, ct) => Call(() => bridge.PullFileAsync(input.Peer, input.RemotePath, paths.Resolve(input.LocalDestination, MissionEffect.Modify), ct), authorized), keywords: ["peer", "pull", "artifact"]),
        ToolDescriptor.Create<PeerTransferInput, string>("peers.transfer_status", "coding.peers",
            "Inspect one file transfer. A work dispatch must wait for all required source transfers to complete. Completion of a transfer is not completion of the peer task.",
            ToolCostClass.Trivial, ToolSideEffect.ReadOnly,
            (input, ct) => Call(() => bridge.TransferAsync(input.TransferId, ct), authorized), keywords: ["peer", "transfer", "status"]),
    ];

    private static Task<string> Call(Func<Task<JsonElement>> action, Func<bool> authorized) => PeerTools.Result(() => authorized()
        ? action() : throw new InvalidOperationException("Peer file access requires a user-authorized turn and is unavailable in a child agent."));
}
