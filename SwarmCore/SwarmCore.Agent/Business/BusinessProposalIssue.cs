namespace SwarmCore.Agent.Business;

/// <summary>Exact version and customer-facing content bound together for review and dispatch.</summary>
public sealed record BusinessProposalIssue(Guid ProposalId, string ProposalHash, string Subject, string PlainText);
