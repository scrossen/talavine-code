namespace SwarmCore.Mcp.ExternalClients;

/// <summary>
/// What a host wants registered under <see cref="ServerName"/>. When the host cannot produce a
/// working launch entry (for example its server executable is not installed) it passes an
/// unavailable registration and nothing is ever written.
/// </summary>
public sealed record McpServerRegistration
{
    private McpServerRegistration(string serverName, McpServerLaunchEntry? entry, string? unavailableReason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ServerName = serverName;
        Entry = entry;
        UnavailableReason = unavailableReason;
    }

    public string ServerName { get; }

    public McpServerLaunchEntry? Entry { get; }

    public string? UnavailableReason { get; }

    public bool IsAvailable => Entry is not null;

    public static McpServerRegistration Available(string serverName, McpServerLaunchEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new McpServerRegistration(serverName, entry, null);
    }

    public static McpServerRegistration Unavailable(string serverName, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new McpServerRegistration(serverName, null, reason);
    }
}

public enum ExternalMcpClientConnectionState
{
    /// <summary>The client was not found on this computer.</summary>
    NotInstalled,

    /// <summary>The client is installed and has no entry for the server.</summary>
    NotConnected,

    /// <summary>The client's entry is exactly the entry the host would write.</summary>
    Connected,

    /// <summary>The client has an entry for the server, but it launches something else.</summary>
    NeedsUpdate,

    /// <summary>The client has an entry; the host has no entry to compare it with.</summary>
    Configured,

    /// <summary>The client's settings file cannot be read or safely edited.</summary>
    ConfigProblem,
}

public sealed record ExternalMcpClientStatus(
    ExternalMcpClientLocation Location,
    ExternalMcpClientConnectionState State,
    McpServerLaunchEntry? ConfiguredEntry,
    string? Problem);

public enum ExternalMcpClientChangeOutcome
{
    Connected,
    AlreadyConnected,
    Disconnected,
    AlreadyDisconnected,
    ServerUnavailable,
    ClientNotFound,
    Refused,
    Failed,
}

public sealed record ExternalMcpClientChangeResult(
    ExternalMcpClientChangeOutcome Outcome,
    ExternalMcpClientLocation Location,
    string? BackupPath = null,
    string? Problem = null)
{
    public bool Succeeded => Outcome is ExternalMcpClientChangeOutcome.Connected
        or ExternalMcpClientChangeOutcome.AlreadyConnected
        or ExternalMcpClientChangeOutcome.Disconnected
        or ExternalMcpClientChangeOutcome.AlreadyDisconnected;
}

/// <summary>
/// Adds, checks, and removes one named stdio server in external AI apps' own settings files.
/// All methods do blocking file I/O; hosts call them off any UI thread.
/// </summary>
public sealed class ExternalMcpClientRegistrar
{
    private readonly McpClientConfigFileStore _store;
    private readonly IMcpClientConfigFormat _jsonFormat;
    private readonly IMcpClientConfigFormat _codexFormat;

    public ExternalMcpClientRegistrar(
        McpClientConfigFileStore? store = null,
        IMcpClientConfigFormat? jsonFormat = null,
        IMcpClientConfigFormat? codexFormat = null)
    {
        _store = store ?? new McpClientConfigFileStore();
        _jsonFormat = jsonFormat ?? new JsonMcpServersConfigFormat();
        _codexFormat = codexFormat ?? new CodexTomlConfigFormat();
    }

    public IMcpClientConfigFormat FormatFor(ExternalMcpClientConfigKind kind) => kind switch
    {
        ExternalMcpClientConfigKind.JsonMcpServers => _jsonFormat,
        ExternalMcpClientConfigKind.CodexToml => _codexFormat,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public ExternalMcpClientStatus GetStatus(ExternalMcpClientLocation location, McpServerRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(registration);

        if (location.ConfigPath is null)
            return new ExternalMcpClientStatus(location, ExternalMcpClientConnectionState.NotInstalled, null, null);

        var snapshot = _store.Read(location.ConfigPath);
        if (snapshot.Problem is not null)
            return new ExternalMcpClientStatus(location, ExternalMcpClientConnectionState.ConfigProblem, null, snapshot.Problem);

        var inspection = FormatFor(location.Client.ConfigKind).Inspect(snapshot.Content, registration.ServerName);
        if (!inspection.IsValid)
            return new ExternalMcpClientStatus(location, ExternalMcpClientConnectionState.ConfigProblem, null, inspection.Problem);
        if (!inspection.HasEntry)
        {
            if (!inspection.CanEdit)
                return new ExternalMcpClientStatus(location, ExternalMcpClientConnectionState.ConfigProblem, null, inspection.Problem);
            return new ExternalMcpClientStatus(
                location,
                location.IsInstalled ? ExternalMcpClientConnectionState.NotConnected : ExternalMcpClientConnectionState.NotInstalled,
                null,
                null);
        }

        if (!inspection.CanEdit)
            return new ExternalMcpClientStatus(location, ExternalMcpClientConnectionState.ConfigProblem, inspection.Entry, inspection.Problem);
        if (registration.Entry is null)
            return new ExternalMcpClientStatus(location, ExternalMcpClientConnectionState.Configured, inspection.Entry, null);

        return new ExternalMcpClientStatus(
            location,
            registration.Entry.Equals(inspection.Entry)
                ? ExternalMcpClientConnectionState.Connected
                : ExternalMcpClientConnectionState.NeedsUpdate,
            inspection.Entry,
            null);
    }

    /// <summary>The entry the client will actually launch for <paramref name="serverName"/>, if any.</summary>
    public McpServerLaunchEntry? ReadConfiguredEntry(ExternalMcpClientLocation location, string serverName)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (location.ConfigPath is null)
            return null;
        var snapshot = _store.Read(location.ConfigPath);
        if (snapshot.Problem is not null)
            return null;
        var inspection = FormatFor(location.Client.ConfigKind).Inspect(snapshot.Content, serverName);
        return inspection.IsValid ? inspection.Entry : null;
    }

    public ExternalMcpClientChangeResult Connect(ExternalMcpClientLocation location, McpServerRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(registration);

        // Checked before any file is touched: never write an entry that cannot launch.
        if (registration.Entry is not { } entry)
            return new ExternalMcpClientChangeResult(ExternalMcpClientChangeOutcome.ServerUnavailable, location, Problem: registration.UnavailableReason);
        if (location.ConfigPath is null || !location.IsInstalled)
            return new ExternalMcpClientChangeResult(ExternalMcpClientChangeOutcome.ClientNotFound, location);

        var format = FormatFor(location.Client.ConfigKind);
        var result = _store.Update(
            location.ConfigPath,
            snapshot => format.Upsert(snapshot.Content, registration.ServerName, entry, new McpClientConfigTextStyle(snapshot.NewLine)));
        return result.Outcome switch
        {
            McpClientConfigWriteOutcome.Changed => new(ExternalMcpClientChangeOutcome.Connected, location, result.BackupPath),
            McpClientConfigWriteOutcome.Unchanged => new(ExternalMcpClientChangeOutcome.AlreadyConnected, location),
            McpClientConfigWriteOutcome.Refused => new(ExternalMcpClientChangeOutcome.Refused, location, Problem: result.Problem),
            _ => new(ExternalMcpClientChangeOutcome.Failed, location, Problem: result.Problem),
        };
    }

    public ExternalMcpClientChangeResult Disconnect(ExternalMcpClientLocation location, string serverName)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        if (location.ConfigPath is null)
            return new ExternalMcpClientChangeResult(ExternalMcpClientChangeOutcome.ClientNotFound, location);

        var format = FormatFor(location.Client.ConfigKind);
        var result = _store.Update(
            location.ConfigPath,
            snapshot => snapshot.Exists
                ? format.Remove(snapshot.Content, serverName, new McpClientConfigTextStyle(snapshot.NewLine))
                : McpClientConfigEdit.Unchanged());
        return result.Outcome switch
        {
            McpClientConfigWriteOutcome.Changed => new(ExternalMcpClientChangeOutcome.Disconnected, location, result.BackupPath),
            McpClientConfigWriteOutcome.Unchanged => new(ExternalMcpClientChangeOutcome.AlreadyDisconnected, location),
            McpClientConfigWriteOutcome.Refused => new(ExternalMcpClientChangeOutcome.Refused, location, Problem: result.Problem),
            _ => new(ExternalMcpClientChangeOutcome.Failed, location, Problem: result.Problem),
        };
    }
}
