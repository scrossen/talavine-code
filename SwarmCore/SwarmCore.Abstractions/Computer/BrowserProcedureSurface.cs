using System.Text.Json.Serialization;

namespace SwarmCore.Abstractions.Computer;

/// <summary>Host-owned authority. A learned procedure cannot supply or expand this scope.</summary>
public sealed record BrowserProcedureScope
{
    public required string AllowedOrigin { get; init; }
    public required string StartUrl { get; init; }
    public IReadOnlyList<BrowserProcedureRoute> LoginRoutes { get; init; } = [];
    public IReadOnlyList<BrowserProcedureRoute> ObservationRoutes { get; init; } = [];
    public BrowserProcedureConsent? Consent { get; init; }
}

public sealed record BrowserProcedureRoute(string Path)
{
    public IReadOnlyDictionary<string, string> RequiredQuery { get; init; } = new Dictionary<string, string>();
}

public sealed record BrowserProcedureConsent(string Notice, IReadOnlyList<BrowserProcedureConsentLink> Links, bool Authorized);
public sealed record BrowserProcedureConsentLink(string Name, string Url);

/// <summary>Semantic data only: no arbitrary selectors, scripts, URLs, or field values.</summary>
public sealed record BrowserProcedureTarget
{
    public string Role { get; init; } = "";
    public string Name { get; init; } = "";
    public string InputType { get; init; } = "";
    public string Autocomplete { get; init; } = "";
}

[JsonConverter(typeof(JsonStringEnumConverter<BrowserProcedureActionKind>))]
public enum BrowserProcedureActionKind { FillUsername, FillPassword, Click, Wait, NavigateStart }

public sealed record BrowserProcedureStep
{
    public required string ObservationId { get; init; }
    public BrowserProcedureActionKind Kind { get; init; }
    public BrowserProcedureTarget? Target { get; init; }
    public string? ControlReference { get; init; }
}

public sealed record BrowserProcedureControl(string Reference, BrowserProcedureTarget Target,
    bool Enabled, bool ReadOnly, string CredentialSlot, IReadOnlyList<BrowserProcedureActionKind> Actions);

public sealed record BrowserProcedureObservation
{
    public bool Success { get; init; }
    public string Status { get; init; } = "Unavailable";
    public string ObservationId { get; init; } = "";
    public string PageFingerprint { get; init; } = "";
    public string Url { get; init; } = "";
    public string Text { get; init; } = "";
    public IReadOnlyList<BrowserProcedureControl> Controls { get; init; } = [];
    public string Trust => "untrusted-browser-evidence";
}

public sealed record BrowserProcedureBindingResult(bool Success, string Status,
    string? BindingId = null, BrowserProcedureObservation? Observation = null,
    BrowserProcedureRecoveryEvidence? Recovery = null);

/// <summary>Host receipt proof for a retained attempt, not page/model evidence. Only confirmed
/// physical actions qualify; their submission reservations remain in that same browser binding.</summary>
public sealed record BrowserProcedureRecoveryEvidence(DateTimeOffset StartedAt, DateTimeOffset ConfirmedAt,
    string ReceiptFingerprint, IReadOnlyList<BrowserProcedureConfirmedAction> Actions);
public sealed record BrowserProcedureConfirmedAction(BrowserProcedureActionKind Kind, BrowserProcedureTarget? Target);

public sealed record BrowserProcedureStepResult(bool Success, string Status, bool Dispatched = false,
    bool OutcomeUncertain = false, BrowserProcedureObservation? Observation = null);
