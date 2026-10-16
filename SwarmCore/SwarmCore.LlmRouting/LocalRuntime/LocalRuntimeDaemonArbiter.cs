using System.Net;
using System.Net.Sockets;
using SwarmCore.Abstractions.Processes;

namespace SwarmCore.LlmRouting.LocalRuntime;

/// <summary>
/// The state of the user's own launcher for a local runtime, such as the
/// Ollama desktop app. A running launcher starts, configures, and restarts its
/// own server with the user's settings (context length, network exposure,
/// model location), so a host must not put a second server beside it.
/// </summary>
public enum LocalRuntimeLauncherState
{
    NotInstalled,
    Installed,
    Running,
}

/// <summary>What a host sees when it considers starting its own runtime daemon.</summary>
/// <param name="EndpointAnswers">A runtime server answers at the host's loopback endpoint.</param>
/// <param name="PackageOperationInProgress">A runtime install or upgrade holds the package-operation lock.
/// Its installer stops the runtime and relaunches the user's launcher when it finishes.</param>
/// <param name="Launcher">The user's launcher for the runtime.</param>
/// <param name="PortListeners">Every listener on the endpoint's port, on any address. Null when the
/// platform cannot list listeners.</param>
/// <param name="OwnedDaemon">Inspection of the daemon a host process recorded starting, or null when
/// no valid record exists.</param>
/// <param name="Elapsed">Time since this start attempt began.</param>
public sealed record LocalRuntimeStartObservation(
    bool EndpointAnswers,
    bool PackageOperationInProgress,
    LocalRuntimeLauncherState Launcher,
    IReadOnlyList<IPEndPoint>? PortListeners,
    ExactProcessInspectionState? OwnedDaemon,
    TimeSpan Elapsed);

/// <param name="LauncherGrace">How long a running launcher gets to bring up its own server.</param>
/// <param name="InstalledLauncherSettle">How long to re-probe when the launcher is installed but not
/// running, in case it is starting (at sign-in, or relaunched by an installer).</param>
/// <param name="ListenerGrace">How long a listener that holds the port, or the host's own recorded
/// daemon, gets to start answering.</param>
/// <param name="ProbeInterval">Delay between observations while waiting.</param>
public sealed record LocalRuntimeStartPolicy(
    TimeSpan LauncherGrace,
    TimeSpan InstalledLauncherSettle,
    TimeSpan ListenerGrace,
    TimeSpan ProbeInterval)
{
    public static LocalRuntimeStartPolicy Default { get; } = new(
        LauncherGrace: TimeSpan.FromSeconds(20),
        InstalledLauncherSettle: TimeSpan.FromSeconds(3),
        ListenerGrace: TimeSpan.FromSeconds(15),
        ProbeInterval: TimeSpan.FromSeconds(1));
}

public enum LocalRuntimeStartAction
{
    /// <summary>A server already answers at the endpoint.</summary>
    UseRunning,

    /// <summary>A package install or upgrade owns the runtime; start after it finishes.</summary>
    Defer,

    /// <summary>Something that will serve the port may still be starting; observe again.</summary>
    Wait,

    /// <summary>Another listener holds the port without answering; a daemon started now would share the port with it.</summary>
    Decline,

    /// <summary>Nothing answers or listens on the port: start the host's own daemon.</summary>
    Start,
}

public sealed record LocalRuntimeStartDecision(LocalRuntimeStartAction Action, string Reason);

/// <summary>
/// How loopback addressing behaves on this platform, which decides how a host
/// can reach another server on its own port while its daemon holds the
/// more specific loopback bind.
/// </summary>
/// <param name="AlternateIPv4LoopbackReachable">The whole 127.0.0.0/8 range reaches this machine
/// (Windows and Linux). macOS configures only 127.0.0.1 by default.</param>
/// <param name="IPv6WildcardMayAcceptIPv4">A listener reported only as the IPv6 wildcard may be
/// dual-stack. Windows reports a dual-stack socket in both its IPv4 and IPv6 tables, so there a
/// wildcard reported only as IPv6 is IPv6-only; other platforms report a dual-stack socket once,
/// as IPv6.</param>
public sealed record LocalLoopbackTopology(
    bool AlternateIPv4LoopbackReachable,
    bool IPv6WildcardMayAcceptIPv4)
{
    public static LocalLoopbackTopology Current { get; } = new(
        AlternateIPv4LoopbackReachable: OperatingSystem.IsWindows() || OperatingSystem.IsLinux(),
        IPv6WildcardMayAcceptIPv4: !OperatingSystem.IsWindows());
}

/// <summary>What a host sees about the daemon it recorded starting.</summary>
/// <param name="Inspection">Exact PID, start-time, and executable inspection of the recorded daemon.</param>
/// <param name="OwnedEndpoint">The address and port the recorded daemon was told to bind.</param>
/// <param name="PortListeners">Every listener on that port, on any address; null when unknown.</param>
/// <param name="TakeoverProbed">True once the planned takeover probes were asked.</param>
/// <param name="AnsweringTakeoverEndpoint">The first takeover probe at which a runtime server answered.</param>
public sealed record OwnedLocalDaemonObservation(
    ExactProcessInspectionState Inspection,
    IPEndPoint OwnedEndpoint,
    IReadOnlyList<IPEndPoint>? PortListeners,
    LocalLoopbackTopology Topology,
    bool TakeoverProbed = false,
    Uri? AnsweringTakeoverEndpoint = null);

public enum OwnedLocalDaemonAction
{
    /// <summary>No other server shares the port.</summary>
    Keep,

    /// <summary>The recorded daemon is gone, or its PID now names another process: drop the record.</summary>
    Forget,

    /// <summary>Another listener that would take over the endpoint shares the port: ask whether a runtime server answers there.</summary>
    ProbeTakeover,

    /// <summary>Another runtime server answers and will serve the endpoint once the recorded daemon stops.</summary>
    Retire,

    /// <summary>The other listeners are bound to specific addresses, so stopping the recorded daemon would leave the endpoint unserved.</summary>
    KeepUncovered,

    /// <summary>The other listener does not answer as a runtime server (yet).</summary>
    KeepUnanswered,

    /// <summary>The recorded identity cannot be verified; a process that cannot be proven the host's own is never stopped.</summary>
    KeepUnverifiable,
}

public sealed record OwnedLocalDaemonDecision(
    OwnedLocalDaemonAction Action,
    IReadOnlyList<IPEndPoint> OtherListeners,
    IReadOnlyList<Uri> TakeoverProbes,
    string Reason);

/// <summary>
/// Decides when a host may start its own local runtime daemon and when it must
/// retire one it started. The contract is one server per local runtime port:
/// a host never adds a daemon beside a server that holds the port on any
/// address, and it stops the daemon it started (identified only by its
/// recorded PID, start time, and executable, never by process name) once
/// another runtime server answers on the port and will keep serving the host's
/// endpoint. Pure: hosts supply the observations and perform the actions.
/// </summary>
public static class LocalRuntimeDaemonArbiter
{
    public static LocalRuntimeStartDecision DecideStart(
        LocalRuntimeStartObservation observation,
        LocalRuntimeStartPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(observation);
        policy ??= LocalRuntimeStartPolicy.Default;
        var elapsed = observation.Elapsed;

        if (observation.EndpointAnswers)
            return new(LocalRuntimeStartAction.UseRunning, "a server already answers at the endpoint");

        if (observation.PackageOperationInProgress)
            return new(LocalRuntimeStartAction.Defer,
                "a runtime install or upgrade is in progress; its installer stops the runtime and relaunches the user's app");

        if (observation.OwnedDaemon is ExactProcessInspectionState.Matches or ExactProcessInspectionState.Unverifiable)
        {
            return elapsed < policy.ListenerGrace
                ? new(LocalRuntimeStartAction.Wait, "the daemon a host process started is still starting")
                : new(LocalRuntimeStartAction.Decline, "the daemon a host process started is running but does not answer");
        }

        if (observation.Launcher == LocalRuntimeLauncherState.Running && elapsed < policy.LauncherGrace)
            return new(LocalRuntimeStartAction.Wait, "the user's runtime app is running and starts its own server");

        if (observation.Launcher == LocalRuntimeLauncherState.Installed && elapsed < policy.InstalledLauncherSettle)
            return new(LocalRuntimeStartAction.Wait, "the user's runtime app is installed and may be starting");

        if (observation.PortListeners is { Count: > 0 })
        {
            return elapsed < policy.ListenerGrace
                ? new(LocalRuntimeStartAction.Wait, "another server holds the port and may still be starting")
                : new(LocalRuntimeStartAction.Decline,
                    "another server listens on the port but does not answer at the endpoint; a second daemon would share the port with it");
        }

        return new(LocalRuntimeStartAction.Start,
            observation.PortListeners is null
                ? "no server answers at the endpoint (this platform cannot list port listeners)"
                : "no server answers at the endpoint or listens on the port");
    }

    public static OwnedLocalDaemonDecision DecideOwned(OwnedLocalDaemonObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        switch (observation.Inspection)
        {
            case ExactProcessInspectionState.Absent:
                return new(OwnedLocalDaemonAction.Forget, [], [], "the recorded daemon has exited");
            case ExactProcessInspectionState.IdentityMismatch:
                return new(OwnedLocalDaemonAction.Forget, [], [],
                    "the recorded PID now belongs to a different process instance");
            case ExactProcessInspectionState.Unverifiable:
                return new(OwnedLocalDaemonAction.KeepUnverifiable, [], [],
                    "the recorded daemon's identity cannot be verified");
        }

        if (observation.PortListeners is null)
            return new(OwnedLocalDaemonAction.Keep, [], [], "this platform cannot list port listeners");

        var others = OtherListeners(observation.PortListeners, observation.OwnedEndpoint);
        if (others.Count == 0)
            return new(OwnedLocalDaemonAction.Keep, others, [], "no other server listens on the port");

        var probes = PlanTakeoverProbes(others, observation.OwnedEndpoint, observation.Topology);
        if (probes.Count == 0)
        {
            return new(OwnedLocalDaemonAction.KeepUncovered, others, probes,
                "no other listener can be confirmed to serve the endpoint if the recorded daemon stopped");
        }

        if (!observation.TakeoverProbed)
            return new(OwnedLocalDaemonAction.ProbeTakeover, others, probes, "another server shares the port");

        return observation.AnsweringTakeoverEndpoint is not null
            ? new(OwnedLocalDaemonAction.Retire, others, probes,
                $"another runtime server answers at {observation.AnsweringTakeoverEndpoint} and will serve the endpoint")
            : new(OwnedLocalDaemonAction.KeepUnanswered, others, probes,
                "the other listener does not answer as a runtime server");
    }

    /// <summary>Listeners on the owned endpoint's port at any address other than the owned one.</summary>
    public static IReadOnlyList<IPEndPoint> OtherListeners(IEnumerable<IPEndPoint> listeners, IPEndPoint owned)
    {
        ArgumentNullException.ThrowIfNull(listeners);
        ArgumentNullException.ThrowIfNull(owned);
        var ownedAddress = Normalize(owned.Address);
        return listeners
            .Where(listener => listener.Port == owned.Port && !Normalize(listener.Address).Equals(ownedAddress))
            .Select(listener => new IPEndPoint(Normalize(listener.Address), listener.Port))
            .Distinct()
            .ToArray();
    }

    /// <summary>
    /// Where to ask whether another server on the port is a runtime server
    /// that keeps serving the owned endpoint once the owned daemon stops. Only
    /// a wildcard listener that accepts IPv4 does: a listener on a specific
    /// address is reached only there. An IPv4 loopback address other than the
    /// owned one reaches exactly that wildcard socket, so an answer there also
    /// proves the takeover. Without one (macOS), a dual-stack IPv6 wildcard is
    /// asked over IPv6 loopback.
    /// </summary>
    public static IReadOnlyList<Uri> PlanTakeoverProbes(
        IReadOnlyList<IPEndPoint> otherListeners,
        IPEndPoint owned,
        LocalLoopbackTopology topology)
    {
        ArgumentNullException.ThrowIfNull(otherListeners);
        ArgumentNullException.ThrowIfNull(owned);
        ArgumentNullException.ThrowIfNull(topology);
        var ownedAddress = Normalize(owned.Address);
        if (ownedAddress.AddressFamily != AddressFamily.InterNetwork || !IPAddress.IsLoopback(ownedAddress))
            return [];

        var ipv4Wildcard = otherListeners.Any(listener => listener.Address.Equals(IPAddress.Any));
        var ipv6Wildcard = otherListeners.Any(listener => listener.Address.Equals(IPAddress.IPv6Any));
        if (!ipv4Wildcard && !(ipv6Wildcard && topology.IPv6WildcardMayAcceptIPv4))
            return [];

        if (topology.AlternateIPv4LoopbackReachable)
        {
            var alternate = AlternateIPv4Loopback(ownedAddress, otherListeners);
            return alternate is null ? [] : [ToUri(alternate, owned.Port)];
        }

        return ipv6Wildcard ? [ToUri(IPAddress.IPv6Loopback, owned.Port)] : [];
    }

    /// <summary>
    /// An IPv4 loopback address that neither the owned daemon nor another
    /// listener binds specifically, so a connection to it lands on a wildcard.
    /// </summary>
    private static IPAddress? AlternateIPv4Loopback(IPAddress owned, IReadOnlyList<IPEndPoint> otherListeners)
    {
        for (byte last = 1; last < 16; last++)
        {
            var candidate = new IPAddress(new byte[] { 127, 0, 0, last });
            if (candidate.Equals(owned) || otherListeners.Any(listener => listener.Address.Equals(candidate)))
                continue;
            return candidate;
        }

        return null;
    }

    private static Uri ToUri(IPAddress address, int port)
        => new($"http://{new IPEndPoint(address, port)}");

    private static IPAddress Normalize(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
