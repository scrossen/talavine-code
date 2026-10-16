using System.Text.Json;
using System.Text.Json.Serialization;

namespace SwarmCore.Agent.BusinessScenarios;

/// <summary>
/// Agent-visible, data-only description of a repeatable business scenario. Evaluator
/// expectations deliberately live outside this model and its serialized document.
/// </summary>
public sealed record BusinessScenarioDocument(
    int SchemaVersion,
    string ScenarioId,
    BusinessScenarioProfile Business,
    IReadOnlyList<BusinessScenarioEvent> SeedEvents,
    IReadOnlyList<BusinessScenarioEvent> InjectedEvents,
    IReadOnlyList<BusinessScenarioEvent> AgentOutputEvents);

public sealed record BusinessScenarioProfile(
    string BusinessId,
    string Name,
    string BusinessType,
    string TimeZone,
    string? WebsiteUrl,
    BusinessScenarioOwnerIdentity Owner,
    IReadOnlyList<BusinessScenarioRole> Roles,
    IReadOnlyList<BusinessScenarioOffer> Offers,
    IReadOnlyList<BusinessScenarioReference> PublicReferences);

public sealed record BusinessScenarioOwnerIdentity(
    string DisplayName,
    string WorkEmail,
    string PersonalAccountPolicy);

public sealed record BusinessScenarioRole(
    string RoleId,
    string Title,
    string RoutineScope,
    IReadOnlyList<string> OwnerDecisions);

public sealed record BusinessScenarioOffer(
    string OfferId,
    string Name,
    string Description);

public sealed record BusinessScenarioReference(
    string SourceId,
    string Title,
    string Url,
    DateTimeOffset RetrievedAtUtc,
    string UseScope);

public sealed record BusinessScenarioEvent(
    string EventId,
    BusinessScenarioEventKind Kind,
    DateTimeOffset OccurredAtUtc,
    BusinessScenarioSource Source,
    IReadOnlyList<string> DependsOn,
    JsonElement Evidence,
    SimulatedDeliveryEvidence? DeliveryEvidence = null);

public enum BusinessScenarioEventKind
{
    Unspecified,
    Seed,
    Injected,
    AgentOutput,
}

public sealed record BusinessScenarioSource(
    string SourceId,
    string SourceType,
    string? Uri = null);

/// <summary>
/// This receipt is intentionally incapable of claiming external delivery, acceptance,
/// payment, or customer receipt. Only the local simulation sink may produce it.
/// </summary>
public sealed record SimulatedDeliveryEvidence(
    string OperationId,
    SimulatedDeliveryStatus Status,
    string Recipient,
    string Subject,
    string Body,
    IReadOnlyList<string> SourceEventIds,
    IReadOnlyList<string> AttachmentSourceIds,
    string SimulationLabel);

[JsonConverter(typeof(JsonStringEnumConverter<SimulatedDeliveryStatus>))]
public enum SimulatedDeliveryStatus
{
    Simulated,
}
