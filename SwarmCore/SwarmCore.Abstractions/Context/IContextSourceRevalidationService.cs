namespace SwarmCore.Abstractions.Context;

public interface IContextSourceRevalidationService
{
    Task<ContextPack> RevalidateAsync(ContextPack pack, CancellationToken ct = default);
}

/// <summary>Host-observed validity of one exact source or context item; contains no private source text.</summary>
public sealed record ContextEvidenceValidation(string Id, bool IsValid, string Reason);

/// <summary>Host tool result carries the exact admitted pack out-of-band; never rebuild proof from formatted text.</summary>
public interface IContextPackEvidence
{
    ContextPack? WorkingContextSourcePack { get; }
}
