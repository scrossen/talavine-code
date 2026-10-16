using System.Security.Cryptography;
using System.Text.Json;

namespace SwarmCore.Abstractions.Simulation;

/// <summary>A local recording request, never authorization for external delivery.</summary>
public sealed record SimulationDispatchRequest(
    string ScopeId,
    string OperationId,
    string Channel,
    string? From,
    IReadOnlyList<string> To,
    string? Subject,
    string? TextBody,
    string? HtmlBody,
    IReadOnlyList<string> SourceReferences,
    IReadOnlyList<SimulationAttachment> Attachments)
{
    public IReadOnlyList<string> Cc { get; init; } = [];
    public IReadOnlyList<string> Bcc { get; init; } = [];
    public string? InReplyToNativeId { get; init; }
    public string? ConversationSourceId { get; init; }
    public string? AuthorId { get; init; }
}

public sealed record SimulationAttachment(string FileName, string ContentType, byte[] Content);

/// <summary>Recorded means simulated locally. It never means sent, delivered or accepted.</summary>
public sealed record SimulationDispatchReceipt(
    string OperationId,
    string ScopeId,
    string NativeId,
    string ContentHash,
    DateTimeOffset RecordedAtUtc)
{
    public string Disposition => "simulated";
}

/// <summary>
/// Hosts persist a request and receipt atomically under (scope, operation).
/// Same identity and content returns the original receipt; changed content must
/// conflict rather than overwrite. This interface has no physical transport.
/// </summary>
public interface IAgentSimulationSink
{
    Task<SimulationDispatchReceipt> RecordAsync(SimulationDispatchRequest request,
        CancellationToken cancellationToken = default);
}

public static class SimulationContentFingerprint
{
    public static string Compute(SimulationDispatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Require(request.ScopeId, nameof(request.ScopeId));
        Require(request.OperationId, nameof(request.OperationId));
        Require(request.Channel, nameof(request.Channel));
        ArgumentNullException.ThrowIfNull(request.To);
        ArgumentNullException.ThrowIfNull(request.SourceReferences);
        ArgumentNullException.ThrowIfNull(request.Attachments);
        if (request.To.Count == 0 || request.To.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Simulation requires explicit recipients.", nameof(request));
        // A fixed property order and hashes of attachment bytes give both hosts
        // the same fingerprint without retaining an answer or a credential.
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            request.ScopeId, request.OperationId, request.Channel, request.From,
            To = request.To.ToArray(), request.Subject, request.TextBody, request.HtmlBody,
            Cc = request.Cc.ToArray(), Bcc = request.Bcc.ToArray(),
            request.InReplyToNativeId, request.ConversationSourceId, request.AuthorId,
            SourceReferences = request.SourceReferences.ToArray(),
            Attachments = request.Attachments.Select(a => new
            {
                a.FileName, a.ContentType,
                Hash = Convert.ToHexString(SHA256.HashData(a.Content)).ToLowerInvariant()
            }).ToArray()
        });
        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Simulation identity cannot be empty.", name);
    }
}
