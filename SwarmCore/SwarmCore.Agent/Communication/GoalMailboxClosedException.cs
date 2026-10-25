namespace SwarmCore.Agent.Communication;

/// <summary>A definitive rejection of new work, distinct from uncertain delivery or an offline profile.</summary>
public sealed class GoalMailboxClosedException() : InvalidOperationException(
    "This goal is closed. Its messages and progress remain available; start a new goal for new work.");
