using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SwarmCore.Abstractions.Processes;
using SwarmCore.Abstractions.Security;
using SwarmCore.Mcp.Core;

namespace SwarmCore.Mcp.ExternalClients;

public enum McpConnectionProbeFailure
{
    None,
    CommandNotFound,
    StartFailed,
    ExitedEarly,
    TimedOut,
    ProtocolError,
    Cancelled,
}

public sealed record McpConnectionProbeResult(
    bool Success,
    McpConnectionProbeFailure Failure,
    string Message,
    string? ServerName,
    string? ServerVersion,
    int ToolCount,
    IReadOnlyList<string> SampleToolNames,
    int? ExitCode,
    string? DiagnosticOutput,
    TimeSpan Elapsed);

public sealed record McpConnectionProbeOptions
{
    /// <summary>Budget for start-up, <c>initialize</c>, and <c>tools/list</c> together.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Working directory for the server; defaults to the user's home folder.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Pinned to an initialize-based revision so the probe performs the same
    /// <c>initialize</c> handshake desktop clients use today.
    /// </summary>
    public string ProtocolVersion { get; init; } = "2025-11-25";

    public string ClientName { get; init; } = "swarmcore-connection-test";

    public string ClientVersion { get; init; } = "1.0.0";

    /// <summary>
    /// Variables the launched server starts with before the entry's own <c>env</c> is applied.
    /// Null uses <see cref="McpStdioConnectionProbe.CreateClientDefaultEnvironment"/>, which
    /// mirrors the small set desktop MCP clients pass, so the test sees what they will see.
    /// </summary>
    public IReadOnlyDictionary<string, string>? BaseEnvironment { get; init; }

    public int MaximumSampleToolNames { get; init; } = 8;
}

/// <summary>
/// Launches a stdio MCP server exactly as an external client's entry describes it, completes
/// <c>initialize</c> and <c>tools/list</c>, and shuts the server down. It never calls a tool and
/// declares no sampling capability, so a probe cannot cause any model inference. Server
/// standard error is kept only as a small, credential-scrubbed tail for the result and is
/// never logged.
/// </summary>
public sealed class McpStdioConnectionProbe
{
    private const int MaximumDiagnosticLines = 12;
    private const int MaximumDiagnosticCharacters = 2000;
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(2);

    public async Task<McpConnectionProbeResult> ProbeAsync(
        McpServerLaunchEntry entry,
        McpConnectionProbeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        options ??= new McpConnectionProbeOptions();
        var stopwatch = Stopwatch.StartNew();

        var environment = new Dictionary<string, string?>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var (key, value) in options.BaseEnvironment ?? CreateClientDefaultEnvironment())
            environment[key] = value;
        foreach (var (key, value) in entry.EnvironmentVariables)
            environment[key] = value;

        var workingDirectory = ResolveWorkingDirectory(options.WorkingDirectory);
        var executable = ResolveExecutable(
            entry.Command,
            environment.TryGetValue("PATH", out var searchPath) ? searchPath : null,
            workingDirectory);
        if (executable is null)
        {
            return Failed(
                McpConnectionProbeFailure.CommandNotFound,
                $"The program \"{entry.Command}\" was not found on this computer.",
                stopwatch);
        }

        var startInfo = ChildProcessStdioTransport.BuildStartInfo(new StdioClientTransportOptions
        {
            Name = options.ClientName,
            Command = executable,
            Arguments = entry.Arguments.ToList(),
            WorkingDirectory = workingDirectory,
            InheritEnvironmentVariables = false,
            EnvironmentVariables = environment,
        });

        IChildProcess child;
        try
        {
            child = ChildProcessLauncher.Start(startInfo);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException
                                       or InvalidOperationException or PlatformNotSupportedException)
        {
            return Failed(
                McpConnectionProbeFailure.StartFailed,
                $"The program \"{entry.Command}\" could not be started: {Scrub(ex.Message)}",
                stopwatch);
        }

        var diagnostics = new DiagnosticTail();
        var standardErrorClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var exited = new CancellationTokenSource();
        McpClient? client = null;
        try
        {
            child.ErrorLineReceived += line =>
            {
                if (line is null)
                    standardErrorClosed.TrySetResult();
                else
                    diagnostics.Add(line);
            };
            child.Exited += (_, _) =>
            {
                try { exited.Cancel(); }
                catch (ObjectDisposedException) { }
            };
            child.BeginErrorReadLine();

            using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, exited.Token);
            operation.CancelAfter(options.Timeout);
            try
            {
                var transport = new StreamClientTransport(child.StandardInput.BaseStream, child.StandardOutput.BaseStream);
                client = await McpClient.CreateAsync(
                    transport,
                    new McpClientOptions
                    {
                        ClientInfo = new Implementation { Name = options.ClientName, Version = options.ClientVersion },
                        ProtocolVersion = options.ProtocolVersion,
                        InitializationTimeout = options.Timeout,
                    },
                    loggerFactory: null,
                    operation.Token).ConfigureAwait(false);

                var tools = await client.ListToolsAsync(cancellationToken: operation.Token).ConfigureAwait(false);
                var names = tools
                    .Select(tool => tool.Name)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Take(Math.Max(0, options.MaximumSampleToolNames))
                    .ToArray();
                var serverName = client.ServerInfo?.Name;
                return new McpConnectionProbeResult(
                    true,
                    McpConnectionProbeFailure.None,
                    $"{(string.IsNullOrWhiteSpace(serverName) ? "The server" : serverName)} answered and listed {tools.Count} tool{(tools.Count == 1 ? string.Empty : "s")}.",
                    serverName,
                    client.ServerInfo?.Version,
                    tools.Count,
                    names,
                    null,
                    null,
                    stopwatch.Elapsed);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (cancellationToken.IsCancellationRequested)
                    return Failed(McpConnectionProbeFailure.Cancelled, "The test was cancelled.", stopwatch);

                if (await WaitForExitAsync(child, TimeSpan.FromSeconds(1)).ConfigureAwait(false))
                {
                    await Task.WhenAny(standardErrorClosed.Task, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
                    var exitCode = TryGetExitCode(child);
                    return new McpConnectionProbeResult(
                        false,
                        McpConnectionProbeFailure.ExitedEarly,
                        exitCode is { } code
                            ? $"The program stopped before it finished connecting (exit code {code.ToString(CultureInfo.InvariantCulture)})."
                            : "The program stopped before it finished connecting.",
                        null,
                        null,
                        0,
                        [],
                        exitCode,
                        diagnostics.ToText(),
                        stopwatch.Elapsed);
                }

                if (operation.IsCancellationRequested || ex is TimeoutException)
                {
                    return new McpConnectionProbeResult(
                        false,
                        McpConnectionProbeFailure.TimedOut,
                        $"The program did not finish connecting within {options.Timeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} seconds.",
                        null,
                        null,
                        0,
                        [],
                        null,
                        diagnostics.ToText(),
                        stopwatch.Elapsed);
                }

                return new McpConnectionProbeResult(
                    false,
                    McpConnectionProbeFailure.ProtocolError,
                    $"The program started but did not complete the MCP handshake: {Scrub(ex.Message)}",
                    null,
                    null,
                    0,
                    [],
                    null,
                    diagnostics.ToText(),
                    stopwatch.Elapsed);
            }
        }
        finally
        {
            if (client is not null)
            {
                try { await client.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { }
            }

            await StopAsync(child).ConfigureAwait(false);
            child.Dispose();
        }
    }

    /// <summary>
    /// The variables desktop MCP clients pass to a stdio server by default (the reference
    /// MCP SDK's inherited set), read from the current process.
    /// </summary>
    public static IReadOnlyDictionary<string, string> CreateClientDefaultEnvironment()
    {
        string[] names = OperatingSystem.IsWindows()
            ?
            [
                "APPDATA", "HOMEDRIVE", "HOMEPATH", "LOCALAPPDATA", "PATH", "PROCESSOR_ARCHITECTURE",
                "SYSTEMDRIVE", "SYSTEMROOT", "TEMP", "USERNAME", "USERPROFILE", "PROGRAMFILES",
            ]
            : ["HOME", "LOGNAME", "PATH", "SHELL", "TERM", "USER"];

        var environment = new Dictionary<string, string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var name in names)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value) && !value.StartsWith("()", StringComparison.Ordinal))
                environment[name] = value;
        }

        return environment;
    }

    internal static string? ResolveExecutable(string command, string? searchPath, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        if (Path.IsPathRooted(command))
            return File.Exists(command) ? command : null;

        if (command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
        {
            var relative = Path.GetFullPath(command, workingDirectory);
            return File.Exists(relative) ? relative : null;
        }

        // Windows tries the name as written, then the executable extensions; a dotted name
        // such as "Talavine.McpServer" still needs ".exe" or ".cmd" appended.
        string[] extensions = OperatingSystem.IsWindows()
            ? [string.Empty, ".exe", ".com", ".cmd", ".bat"]
            : [string.Empty];
        foreach (var directory in (searchPath ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                try
                {
                    var candidate = Path.Combine(directory.Trim().Trim('"'), command + extension);
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch (ArgumentException)
                {
                    // Malformed PATH segment; skip it.
                }
            }
        }

        return null;
    }

    private static string ResolveWorkingDirectory(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested) && Directory.Exists(requested))
            return requested;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrWhiteSpace(home) && Directory.Exists(home) ? home : Path.GetTempPath();
    }

    private static McpConnectionProbeResult Failed(McpConnectionProbeFailure failure, string message, Stopwatch stopwatch) =>
        new(false, failure, message, null, null, 0, [], null, null, stopwatch.Elapsed);

    private static async Task<bool> WaitForExitAsync(IChildProcess child, TimeSpan timeout)
    {
        if (child.HasExited)
            return true;
        using var wait = new CancellationTokenSource(timeout);
        try
        {
            await child.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return child.HasExited;
        }
    }

    private static int? TryGetExitCode(IChildProcess child)
    {
        try
        {
            return child.HasExited ? child.ExitCode : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task StopAsync(IChildProcess child)
    {
        try { child.StandardInput.Close(); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }

        if (await WaitForExitAsync(child, ShutdownGrace).ConfigureAwait(false))
            return;

        try { child.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { }
        await WaitForExitAsync(child, ShutdownGrace).ConfigureAwait(false);
    }

    private static string Scrub(string text) =>
        McpTextSanitizer.StripControlCharacters(CredentialPatterns.Scrub(text, "[redacted]"));

    /// <summary>Keeps the last few lines of server standard error, bounded in size.</summary>
    private sealed class DiagnosticTail
    {
        private readonly Queue<string> _lines = new();
        private readonly Lock _gate = new();

        public void Add(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;
            var trimmed = line.Length > 500 ? line[..500] + "…" : line;
            lock (_gate)
            {
                _lines.Enqueue(trimmed);
                while (_lines.Count > MaximumDiagnosticLines)
                    _lines.Dequeue();
            }
        }

        public string? ToText()
        {
            string text;
            lock (_gate)
            {
                if (_lines.Count == 0)
                    return null;
                text = string.Join('\n', _lines);
            }

            text = Scrub(text).Trim();
            return text.Length <= MaximumDiagnosticCharacters ? text : "…" + text[^MaximumDiagnosticCharacters..];
        }
    }
}
