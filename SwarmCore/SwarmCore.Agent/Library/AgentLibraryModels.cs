using System.Text.Json;

namespace SwarmCore.Agent.Library;

/// <summary>A versioned starting point. Installing creates a customer-owned copy, never a subscription to changing instructions.</summary>
public sealed record AgentLibraryTemplate(
    string Id,
    string Version,
    string Source,
    string Name,
    string Description,
    string SetupDescription,
    string DefaultCronExpression,
    IReadOnlyList<AgentLibrarySourceOption> SourceOptions,
    string Instructions,
    AgentLibraryExecutionKind ExecutionKind = AgentLibraryExecutionKind.PersonalScheduledTask);

public enum AgentLibraryExecutionKind { PersonalScheduledTask, DedicatedWorker }

public sealed record AgentLibrarySourceOption(string Id, string Name, string HomeUrl);
public sealed record AgentLibrarySourceSelection(string SourceId, string AccountLabel);
public sealed record AgentLibrarySourceBinding(string ProviderId, string AccountLabel, Guid SourceId, string HomeUrl);

public sealed record AgentLibrarySetup
{
    public string TemplateId { get; init; } = "";
    public string Name { get; init; } = "";
    public IReadOnlyList<AgentLibrarySourceSelection> Sources { get; init; } = [];
    public string CronExpression { get; init; } = "";
    public bool CustomizeWithAi { get; init; }
    public string CustomizationRequest { get; init; } = "";
}

public sealed record AgentLibraryDraft
{
    public string TemplateId { get; init; } = "";
    public string TemplateVersion { get; init; } = "";
    public string Name { get; init; } = "";
    public IReadOnlyList<AgentLibrarySourceSelection> Sources { get; init; } = [];
    public string CronExpression { get; init; } = "";
    public string Instructions { get; init; } = "";
    public bool WasCustomized { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>Portable provenance and host execution binding. The host owns persistence and authority.</summary>
public sealed record AgentLibraryInstallation
{
    public int SchemaVersion { get; init; } = 1;
    public Guid ProfileId { get; init; }
    public Guid ScheduledTaskId { get; init; }
    public string TemplateId { get; init; } = "";
    public string TemplateVersion { get; init; } = "";
    public string Source { get; init; } = "";
    public Guid OwningDeviceId { get; init; }
    public IReadOnlyList<AgentLibrarySourceBinding> Sources { get; init; } = [];
    public string BrowserMode { get; init; } = "talavine-browser";
    public DateTime InstalledAtUtc { get; init; }
    public string OriginalInstructionsSha256 { get; init; } = "";
    public bool WasCustomized { get; init; }
    public Guid[] SourceIds => Sources.Select(s => s.SourceId).Distinct().ToArray();
    public string[] AllowedOrigins => Sources.Select(s => new Uri(s.HomeUrl).GetLeftPart(UriPartial.Authority)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}

public sealed record InstalledLibraryAgent
{
    public Guid ProfileId { get; init; }
    public Guid ScheduledTaskId { get; init; }
    public string Name { get; init; } = "";
    public string TemplateId { get; init; } = "";
    public string TemplateVersion { get; init; } = "";
    public string Instructions { get; init; } = "";
    public string CronExpression { get; init; } = "";
    public bool IsEnabled { get; init; }
    public bool IsOnThisDevice { get; init; }
    public bool UpdateAvailable { get; init; }
    public DateTime? LastRunAt { get; init; }
    public string? LastRunStatus { get; init; }
    public string? LastRunOutput { get; init; }
    public DateTime? NextRunAt { get; init; }
    public IReadOnlyList<AgentLibrarySourceBinding> Sources { get; init; } = [];
}

public static class AgentLibraryMetadata
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Write(AgentLibraryInstallation installation) =>
        JsonSerializer.Serialize(new Envelope(installation), Json);

    public static AgentLibraryInstallation? Read(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return null;
        try
        {
            var installation = JsonSerializer.Deserialize<Envelope>(metadataJson, Json)?.AgentLibrary;
            return installation is { SchemaVersion: 1, ProfileId: var profile, ScheduledTaskId: var task, OwningDeviceId: var device }
                && profile != Guid.Empty && task != Guid.Empty && device != Guid.Empty
                && !string.IsNullOrWhiteSpace(installation.TemplateId)
                && !string.IsNullOrWhiteSpace(installation.TemplateVersion)
                && installation.Sources is { Count: > 0 }
                && installation.Sources.All(s => s is not null && s.SourceId != Guid.Empty
                    && !string.IsNullOrWhiteSpace(s.ProviderId) && !string.IsNullOrWhiteSpace(s.AccountLabel)
                    && Uri.TryCreate(s.HomeUrl, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps)
                ? installation : null;
        }
        catch (JsonException) { return null; }
    }

    private sealed record Envelope(AgentLibraryInstallation? AgentLibrary);
}
