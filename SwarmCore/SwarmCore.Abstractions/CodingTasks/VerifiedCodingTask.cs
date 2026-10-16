namespace SwarmCore.Abstractions.CodingTasks;

public enum CodingTaskMode { CompatibilityAttempt, Verified }
public enum CodingTaskOutcome { Attempt, Pending, Verified, Failed, Cancelled, Blocked, Conflict }
public enum CodingRunPhase { Registered, Preparing, Ready, Verified, Blocked }

/// <summary>Requested evidence selectors; these never grant resource or tool authority.</summary>
public sealed record CodingAcceptanceCriterion(string Id, string Description, string ToolName, string InputJson,
    string? ResultContains = null, int? ExpectedExitCode = null);

/// <summary>Neutral bridge: the mission implementation reads the authenticated host turn, never a model binding.</summary>
public interface IVerifiedCodingTaskExecutor
{
    Task<CodingTaskResult> RunAsync(CodingTaskRequest request, CancellationToken ct = default);
}

/// <summary>Stored with the existing mission. No parallel run scheduler or ledger.</summary>
public sealed record CodingRunState
{
    public required Guid RunId { get; init; }
    public required string RequestHash { get; init; }
    public required Guid ProfileId { get; init; }
    public required string ConversationId { get; init; }
    public required CodingTaskRequest Request { get; init; }
    public required string WorkspaceRoot { get; init; }
    public CodingRunPhase Phase { get; init; }
    public string? PreparationOwner { get; init; }
    public DateTimeOffset? PreparationExpiresAtUtc { get; init; }
    public CodingWorkspaceIdentity? Workspace { get; init; }
    public CodingVerificationProof? Verification { get; init; }
    public string? Failure { get; init; }
}

public sealed record CodingVerificationProof(string TreeDigest, string CriteriaDigest, string EvidenceDigest,
    int ConstraintVersion, int AuthorityRevision, DateTimeOffset VerifiedAtUtc,
    IReadOnlyList<string> ReceiptReferences)
{
    public string Diff { get; init; } = "";
    public IReadOnlyList<string> ChangedFiles { get; init; } = [];
}
