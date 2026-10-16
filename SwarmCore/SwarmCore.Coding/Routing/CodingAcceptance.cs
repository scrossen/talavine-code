using Microsoft.Extensions.Options;
using SwarmCore.Missions;

namespace SwarmCore.Coding.Routing;

public enum CodingAcceptanceStatus { Unverified, Passed, Failed }

/// <summary>Trusted host configuration, not executable text obtained from the repository or model.</summary>
public sealed class CodingAcceptanceOptions
{
    public List<CodingAcceptanceCommand> Commands { get; set; } = [];
    public int MaxRepairAttempts { get; set; } = 1;
}

public sealed record CodingAcceptanceCommand
{
    public required string Name { get; init; }
    public required string FileName { get; init; }
    public List<string> Arguments { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public int TimeoutSeconds { get; init; } = 300;
    public int ExpectedExitCode { get; init; }
    /// <summary>Optional literal success evidence required in stdout, in addition to the exit code.</summary>
    public string? RequiredOutput { get; init; }
}

public sealed record CodingAcceptanceCheck(string Name, bool Passed, int? ExitCode, bool TimedOut, string Evidence);
public sealed record CodingAcceptanceResult(CodingAcceptanceStatus Status, string Summary, IReadOnlyList<CodingAcceptanceCheck> Checks)
{
    public static CodingAcceptanceResult Unverified { get; } = new(CodingAcceptanceStatus.Unverified,
        "The model finished answering. No host acceptance checks are configured; task completion is unverified.", []);
}

public sealed record CodingAcceptanceContext(string ConversationId, string RepositoryRoot, string OriginalRequest,
    string CandidateAnswer, CodingLane Lane)
{
    /// <summary>The workspace's own acceptance commands (<see cref="CodingWorkspace.AcceptanceChecks"/>); null uses the verifier's configuration.</summary>
    public IReadOnlyList<CodingAcceptanceCommand>? Commands { get; init; }
}

/// <summary>Hosts may replace the command verifier with artifact/UI/domain checks with deterministic proof.</summary>
public interface ICodingAcceptanceVerifier
{
    Task<CodingAcceptanceResult> VerifyAsync(CodingAcceptanceContext context, CancellationToken ct = default);
}

/// <summary>Executes only explicitly configured structured commands, using the existing secret-aware process runner.</summary>
public sealed class CodingCommandAcceptanceVerifier(ShellExecTool shell, IOptions<CodingOptions> options) : ICodingAcceptanceVerifier
{
    public async Task<CodingAcceptanceResult> VerifyAsync(CodingAcceptanceContext context, CancellationToken ct = default)
    {
        var commands = context.Commands ?? options.Value.Acceptance.Commands;
        if (commands.Count == 0) return CodingAcceptanceResult.Unverified;
        var checks = new List<CodingAcceptanceCheck>();
        foreach (var command in commands)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (string.IsNullOrWhiteSpace(command.Name) || string.IsNullOrWhiteSpace(command.FileName) || command.TimeoutSeconds <= 0 || command.ExpectedExitCode < 0)
                    throw new ArgumentException("Acceptance checks require a name, executable and positive timeout.");
                var workingDirectory = string.IsNullOrWhiteSpace(command.WorkingDirectory) ? context.RepositoryRoot
                    : Path.IsPathRooted(command.WorkingDirectory) ? command.WorkingDirectory
                    : Path.GetFullPath(command.WorkingDirectory, context.RepositoryRoot);
                var result = (ShellExecTool.Output)(await shell.Descriptor.Invoke(new ShellExecTool.Input
                {
                    FileName = command.FileName, ArgumentList = command.Arguments, WorkingDirectory = workingDirectory,
                    TimeoutSeconds = command.TimeoutSeconds,
                }, ct).ConfigureAwait(false))!;
                var passed = !result.TimedOut && result.ExitCode == command.ExpectedExitCode
                    && (command.RequiredOutput is null || result.Stdout.Contains(command.RequiredOutput, StringComparison.Ordinal));
                var evidence = "stdout:\n" + result.Stdout + "\nstderr:\n" + result.Stderr;
                checks.Add(new(command.Name, passed, result.ExitCode, result.TimedOut, Bound(evidence)));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Config may refer to credentials; never return exception details which could expose an expanded argument.
                checks.Add(new(command.Name, false, null, false, "Acceptance check could not execute (" + ex.GetType().Name + ")."));
            }
        }
        var allPassed = checks.All(c => c.Passed);
        return new(allPassed ? CodingAcceptanceStatus.Passed : CodingAcceptanceStatus.Failed,
            allPassed ? $"Passed all {checks.Count} configured acceptance checks."
                : $"Failed {checks.Count(c => !c.Passed)} of {checks.Count} configured acceptance checks.", checks);
    }

    private static string Bound(string evidence) => evidence.Length <= 12_000 ? evidence
        : evidence[..6000] + "\n[Middle of verification output omitted]\n" + evidence[^6000..];
}
