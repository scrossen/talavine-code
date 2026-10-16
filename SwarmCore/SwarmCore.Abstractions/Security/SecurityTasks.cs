using SwarmCore.Abstractions.CodingTasks;

namespace SwarmCore.Abstractions.Security;

public enum SecurityCoverageStatus { Completed, Unavailable, Failed, Cancelled, Inconclusive }
public enum SecurityFindingStatus { Suspected, Reproduced, Fixed, Accepted, Unverified }
public enum SecurityRunPhase { Registered, Running, Partial, Complete, Blocked }

/// <summary>Caller-supplied reproduction definition is evidence to test, never authority.</summary>
public sealed record SecurityReproductionCheck(string RuleId, string Category, string SourcePath, int? Line,
    string FileName, IReadOnlyList<string> ArgumentList, int VulnerableExitCode, int PassingExitCode = 0,
    string? VulnerableOutput = null, string? PassingOutput = null)
{
    public string Description { get; init; } = "";
    public string Impact { get; init; } = "";
    public string SeverityRationale { get; init; } = "";
    public string Remediation { get; init; } = "";
}
public sealed record SecurityAnalysisRequest
{
    public required string TaskKey { get; init; }
    public required string RepositoryPath { get; init; }
    [System.ComponentModel.Description("Source inventory/reproduction selection. Build/test commands use the already authorized repository inputs; Exclusions are the hard process/read boundary. Repair paths must remain within this selection.")]
    public IReadOnlyList<string> Scope { get; init; } = [];
    public IReadOnlyList<string> Exclusions { get; init; } = [];
    public IReadOnlyList<SecurityReproductionCheck> ReproductionChecks { get; init; } = [];
    public bool AuditDependencies { get; init; }
    public DateTimeOffset? DeadlineUtc { get; init; }
    public int? MaximumAttempts { get; init; }
    public string? BudgetReference { get; init; }
    public CodingTaskRequest? Repair { get; init; }
}
public sealed record SecurityCommandEvidence(string Id, string Tool, string FileName,
    IReadOnlyList<string> Arguments, string WorkingDirectory, DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc,
    int? ExitCode, bool TimedOut, bool Cancelled, string Stdout, string Stderr, bool Settled);
public sealed record SecurityCoverage(string Scanner, string? Version, SecurityCoverageStatus Status,
    string Reason, IReadOnlyList<string> EvidenceReferences);
public sealed record SecurityFinding(string Fingerprint, string RuleId, string Category, string? File, int? Line,
    string? Component, string? Version, string Description, string Impact, string SeverityRationale,
    double Confidence, SecurityFindingStatus Status, IReadOnlyList<string> EvidenceReferences,
    IReadOnlyList<string> ReproductionSteps, string Remediation)
{
    public string? SourceDigest { get; init; }
    public string? AdvisoryUrl { get; init; }
    public string? FixedTreeDigest { get; init; }
}
public sealed record SecurityReport
{
    public Guid RunId { get; init; }
    public Guid? MissionId { get; init; }
    public string RequestHash { get; init; } = "";
    public string RepositoryPath { get; init; } = "";
    /// <summary>Non-null when checks ran on an isolated committed-baseline copy of RepositoryPath.</summary>
    public string? ExecutionWorkspacePath { get; init; }
    public string? TargetTreeDigest { get; init; }
    public SecurityRunPhase Phase { get; init; }
    public IReadOnlyList<SecurityCoverage> Coverage { get; init; } = [];
    public IReadOnlyList<SecurityFinding> Findings { get; init; } = [];
    public IReadOnlyList<SecurityCommandEvidence> Commands { get; init; } = [];
    public IReadOnlyList<string> Uncertainty { get; init; } = [];
    public CodingTaskResult? Repair { get; init; }
    public string? Error { get; init; }
    public bool Truncated { get; init; }
}
public sealed record SecurityRunState
{
    public required Guid RunId { get; init; }
    public required Guid ProfileId { get; init; }
    public required string ConversationId { get; init; }
    public required string RequestHash { get; init; }
    public required SecurityAnalysisRequest Request { get; init; }
    public string? WorkspaceRoot { get; init; }
    public SwarmCore.Abstractions.CodingTasks.CodingWorkspaceIdentity? Workspace { get; init; }
    public IReadOnlyList<string> GeneratedOutputDirectories { get; init; } = [];
    public SecurityRunPhase Phase { get; init; }
    public string? Owner { get; init; }
    public DateTimeOffset? OwnerExpiresUtc { get; init; }
    public int Attempt { get; init; }
    public SecurityReport? Report { get; init; }
}
public interface ISecurityTaskService
{
    Task<SecurityReport> RunAsync(SecurityAnalysisRequest request, CancellationToken ct = default);
    Task<SecurityReport?> InspectAsync(Guid missionId, CancellationToken ct = default);
}
