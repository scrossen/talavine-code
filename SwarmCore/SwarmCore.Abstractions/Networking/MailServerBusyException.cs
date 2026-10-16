namespace SwarmCore.Abstractions.Networking;

/// <summary>A mail endpoint refused a connection before TLS could begin.</summary>
public sealed class MailServerBusyException() : IOException(
    "The mail server refused the connection with 421 (service temporarily unavailable). Mail sync will retry automatically.");

/// <summary>A concurrent connection or a known refusal already owns endpoint recovery.</summary>
public sealed class MailConnectionDeferredException(DateTimeOffset retryAt) : IOException(
    "Mail connection deferred while the endpoint recovers; sync will retry automatically.")
{
    public DateTimeOffset RetryAt { get; } = retryAt;
}
