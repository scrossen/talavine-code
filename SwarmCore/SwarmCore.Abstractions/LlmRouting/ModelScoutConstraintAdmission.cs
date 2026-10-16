namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// Scout admission of a host-validated exact constraint. It carries no invented
/// benchmark scores and cannot authorize a different function or model.
/// </summary>
public sealed record ModelScoutConstraintAdmission(
    string FunctionType, string Provider, string Model, string Kind, DateTime ExpiresAtUtc);
