namespace SwarmCore.Agent.Procedures;

/// <summary>
/// Registered work authority requirements. LocalOwnedOnly must be an explicit
/// immutable host work declaration; absence retains the connected boundary.
/// A declaration is integrity metadata, never a current authority lease.
/// </summary>
public enum AgentProcedureAuthorityDomain { Connected = 0, LocalOwnedOnly = 1 }
