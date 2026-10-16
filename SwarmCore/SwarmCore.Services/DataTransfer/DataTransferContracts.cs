using Microsoft.Extensions.Logging;

namespace SwarmCore.Services.DataTransfer;

/// <summary>Product adapters own schemas, selection, persistence and verification. Payloads never enter model context.</summary>
public interface IDataTransferProvider
{
    DataTransferCapability Capability { get; }
    Task<byte[]> ExportAsync(DataTransferSelection selection, CancellationToken ct);
    Task<DataTransferApplyResult> ImportAsync(DataTransferSelection selection, byte[] payload, CancellationToken ct);
}

public sealed record DataTransferCapability(string Kind, string Description, bool IncludesCredentials);
public sealed record DataTransferSelection(string Kind, string[] Identifiers, bool IncludeCredentials = false);
public sealed record DataTransferApplyResult(int Applied, int Unchanged, int Conflicts);
public sealed record DataTransferReceiver(string DeviceId, string Profile, string InstanceId,
    string PublicKey, DateTimeOffset ExpiresAt, DataTransferCapability[] Capabilities);
public sealed record DataTransferRequest(string OperationId, string SourceDeviceId, string TargetDeviceId,
    string TargetProfile, string TargetInstanceId, DateTimeOffset ExpiresAt, DataTransferSelection Selection, byte[] Payload);
public sealed record DataTransferReceipt(string OperationId, string State, string Kind,
    int Applied = 0, int Unchanged = 0, int Conflicts = 0);

/// <summary>Claim is durable and atomic. A previous in-progress claim must never be replayed automatically.</summary>
public interface IDataTransferReceiptStore
{
    Task<DataTransferReceipt?> FindAsync(string operationId, CancellationToken ct);
    Task<bool> ClaimAsync(DataTransferReceipt receipt, CancellationToken ct);
    Task SaveAsync(DataTransferReceipt receipt, CancellationToken ct);
}

public sealed class DataTransferReceiverEngine(
    IEnumerable<IDataTransferProvider> providers,
    IDataTransferReceiptStore receipts,
    ILogger? logger = null)
{
    private readonly Dictionary<string, IDataTransferProvider> _providers = providers.ToDictionary(p => p.Capability.Kind, StringComparer.Ordinal);

    public static void ValidateSelection(DataTransferSelection selection)
    {
        if (string.IsNullOrWhiteSpace(selection.Kind) || selection.Identifiers is not { Length: > 0 and <= 1000 }
            || selection.Identifiers.Any(string.IsNullOrWhiteSpace)
            || selection.Identifiers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != selection.Identifiers.Length
            || (selection.Identifiers.Contains("*") && selection.Identifiers.Length != 1))
            throw new InvalidDataException("Select explicit identifiers, or '*' alone for an explicitly requested whole category.");
    }

    public async Task<DataTransferReceipt> ReceiveAsync(DataTransferRequest request, string authenticatedSender,
        DataTransferReceiver current, CancellationToken ct)
    {
        // The transport supplies authenticatedSender from its accepted incoming-transfer ledger, never from a file.
        if (!Guid.TryParseExact(request.OperationId, "N", out _) || request.SourceDeviceId != authenticatedSender
            || request.TargetDeviceId != current.DeviceId || request.TargetInstanceId != current.InstanceId
            || !string.Equals(request.TargetProfile, current.Profile, StringComparison.Ordinal)
            || request.ExpiresAt <= DateTimeOffset.UtcNow || request.ExpiresAt > DateTimeOffset.UtcNow.AddHours(1))
            throw new InvalidDataException("Transfer does not match this authenticated device, profile, instance or validity window.");
        ValidateSelection(request.Selection);
        if (!_providers.TryGetValue(request.Selection.Kind, out var provider)
            || (request.Selection.IncludeCredentials && !provider.Capability.IncludesCredentials))
            throw new InvalidDataException("Requested data capability is unavailable.");
        var prior = await receipts.FindAsync(request.OperationId, ct);
        if (prior is not null) return prior;
        var pending = new DataTransferReceipt(request.OperationId, "applying", request.Selection.Kind);
        if (!await receipts.ClaimAsync(pending, ct))
            return await receipts.FindAsync(request.OperationId, ct) ?? pending;
        try
        {
            var result = await provider.ImportAsync(request.Selection, request.Payload, ct);
            if (result.Applied < 0 || result.Unchanged < 0 || result.Conflicts < 0
                || (long)result.Applied + result.Unchanged + result.Conflicts == 0)
                throw new InvalidDataException("Provider returned no verified import outcome.");
            var receipt = new DataTransferReceipt(request.OperationId, result.Conflicts > 0 ? "conflicts" : "applied",
                request.Selection.Kind, result.Applied, result.Unchanged, result.Conflicts);
            await receipts.SaveAsync(receipt, ct);
            return receipt;
        }
        catch (Exception error)
        {
            // Import may have partially committed. Retain the claim; no blind retry or sensitive exception in receipts.
            // The operator still needs to know why this transfer needs verification.
            logger?.LogWarning("Data transfer {OperationId} ({Kind}) needs verification: import stopped with {ErrorType}: {ErrorMessage}",
                request.OperationId, request.Selection.Kind, error.GetType().Name, error.Message);
            var receipt = pending with { State = "verification_required" };
            await receipts.SaveAsync(receipt, CancellationToken.None);
            return receipt;
        }
    }
}
