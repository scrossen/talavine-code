using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Routing;
using SwarmCore.Coding.Tools;
using SwarmCore.Agent.Git;
using SwarmCore.Missions;
using System.Text.Json.Serialization;

namespace SwarmCore.Coding.Evaluation;

public sealed record CodingProbeRoute(string Provider, string Model);
public sealed record CodingProbeCase(string Id, string Request, string ExpectedContent, bool RequireCheck, bool RequireCommit);
public sealed record CodingProbeExecution(string ConversationId, string RepositoryRoot, CodingProbeRoute Route, CodingProbeCase Case);
public interface ICodingProbeExecutor
{
    Task<IReadOnlyList<TurnEvent>> ExecuteAsync(CodingProbeExecution request, CancellationToken ct);
}
public sealed record CodingProbeCaseScore([property: JsonPropertyName("kind")] string Id,
    [property: JsonPropertyName("succeeded")] bool Passed);
public sealed record CodingProbeReport(string SuiteVersion, string SuiteSha256, string Provider, string Model,
    DateTime EvaluatedAtUtc, IReadOnlyList<CodingProbeCaseScore> Cases)
{
    public decimal Score => Cases.Count == 0 ? 0 : (decimal)Cases.Count(c => c.Passed) / Cases.Count;
}

public sealed class CodingProbeSuite(ICodingProbeExecutor executor)
{
    public const string Version = "coding-edit-check-commit-v1";
    public static IReadOnlyList<CodingProbeCase> Cases { get; } =
    [
        new("edit", "Replace the entire content of value.txt with ready followed by a newline. Preserve expected.txt and untouched.txt.", "ready\n", false, false),
        new("check", "Replace value.txt with ready followed by a newline. Run shell.exec with git diff --no-index -- expected.txt value.txt and fix any mismatch. Preserve expected.txt and untouched.txt.", "ready\n", true, false),
        new("commit", "Replace value.txt with ready followed by a newline and make a local git commit containing only value.txt. Preserve expected.txt and untouched.txt. Do not push.", "ready\n", false, true),
    ];
    public static string SuiteSha256 => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(Version + System.Text.Json.JsonSerializer.Serialize(Cases))));

    public async Task<CodingProbeReport> RunAsync(CodingProbeRoute route, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route.Provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(route.Model);
        var scores = new List<CodingProbeCaseScore>();
        foreach (var probe in Cases)
        {
            ct.ThrowIfCancellationRequested();
            var directory = Directory.CreateTempSubdirectory("swarmcore-coding-probe-");
            Task<IReadOnlyList<TurnEvent>>? execution = null;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            try
            {
                // This directory is host-created; canonicalize its parent aliases before any work.
                // Child symlink rejection in the acceptance verifier remains strict.
                var root = SandboxPathResolver.ResolvePhysicalPath(directory.FullName);
                await File.WriteAllTextAsync(Path.Combine(root, "value.txt"), "pending\n", ct);
                await File.WriteAllTextAsync(Path.Combine(root, "expected.txt"), probe.ExpectedContent, ct);
                await File.WriteAllTextAsync(Path.Combine(root, "untouched.txt"), "preserve\n", ct);
                await Git(root, ["init", "--template="], ct);
                await Git(root, ["config", "user.name", "Coding probe"], ct);
                await Git(root, ["config", "user.email", "coding-probe@example.invalid"], ct);
                await Git(root, ["config", "core.autocrlf", "false"], ct);
                await Git(root, ["add", "--", "value.txt", "expected.txt", "untouched.txt"], ct);
                await Git(root, ["commit", "-m", "Synthetic probe baseline"], ct);
                var baseline = await Git(root, ["rev-parse", "HEAD"], ct);
                var request = new CodingProbeExecution("coding-probe-" + Guid.NewGuid().ToString("N"), root, route, probe);
                execution = executor.ExecuteAsync(request, deadline.Token);
                var events = await execution.WaitAsync(deadline.Token).ConfigureAwait(false);
                var verifier = new CodingProbeAcceptanceVerifier(probe, baseline.Trim(), events);
                var result = await verifier.VerifyAsync(new(request.ConversationId, root, probe.Request, "",
                    new CodingLane("probe", route.Provider, route.Model)), deadline.Token).ConfigureAwait(false);
                scores.Add(new(probe.Id, result.Status == CodingAcceptanceStatus.Passed));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { scores.Add(new(probe.Id, false)); }
            finally
            {
                // A non-cooperative model may still have physical work in flight. Its private fixture
                // remains until that work settles; never delete files underneath a detached turn.
                if (execution is { IsCompleted: false }) _ = CleanAfterAsync(execution, directory);
                else Clean(directory);
            }
        }
        return new(Version, SuiteSha256, route.Provider, route.Model, DateTime.UtcNow, scores);
    }

    internal static async Task<string> Git(string root, string[] args, CancellationToken ct)
    {
        var result = await GitProcess.RunAsync(root, args, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new InvalidOperationException("Synthetic probe git check failed.");
        return result.Stdout;
    }
    private static async Task CleanAfterAsync(Task execution, DirectoryInfo directory)
    { try { await execution.ConfigureAwait(false); } catch { } finally { Clean(directory); } }
    private static void Clean(DirectoryInfo directory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory.FullName, "*", new EnumerationOptions
                { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) File.SetAttributes(file, FileAttributes.Normal);
            directory.Delete(true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Scores host-observed artifacts and physical receipts; model prose never changes the verdict.</summary>
public sealed class CodingProbeAcceptanceVerifier(CodingProbeCase probe, string baselineCommit,
    IReadOnlyList<TurnEvent> events) : ICodingAcceptanceVerifier
{
    public async Task<CodingAcceptanceResult> VerifyAsync(CodingAcceptanceContext context, CancellationToken ct = default)
    {
        var checks = new List<CodingAcceptanceCheck>();
        var root = context.RepositoryRoot;
        async Task<bool> Content(string name, string expected)
        {
            var path = Path.Combine(root, name);
            return File.Exists(path) && SandboxPathResolver.ResolvePhysicalPath(path) == Path.GetFullPath(path)
                && (await File.ReadAllTextAsync(path, ct)).Replace("\r\n", "\n") == expected;
        }
        var edit = await Content("value.txt", probe.ExpectedContent);
        var preserved = await Content("expected.txt", probe.ExpectedContent) && await Content("untouched.txt", "preserve\n");
        checks.Add(new("edit", edit && preserved, null, false, "Synthetic file contents and preserved fixtures verified."));
        if (probe.RequireCheck)
        {
            var starts = events.OfType<ToolCallStarted>().Where(e => e.ToolName == "shell.exec").ToDictionary(e => e.ToolCallId, e => e.Input);
            var receipt = events.OfType<ToolCallCompleted>().Any(e => e.ToolName == "shell.exec" && !e.IsError
                && e.WasDispatched && e.LocalProcessCompleted && starts.TryGetValue(e.ToolCallId, out var input) && IsCheck(input));
            var check = await GitProcess.RunAsync(root, ["diff", "--no-index", "--", "expected.txt", "value.txt"], TimeSpan.FromSeconds(20), ct);
            checks.Add(new("check", receipt && check.ExitCode == 0, check.ExitCode, check.ExitCode == -2, "Independent check plus dispatched physical check receipt verified."));
        }
        if (probe.RequireCommit)
        {
            var head = (await CodingProbeSuite.Git(root, ["rev-parse", "HEAD"], ct)).Trim();
            var files = (await CodingProbeSuite.Git(root, ["diff", "--name-only", baselineCommit, "HEAD"], ct)).Trim();
            var committed = (await CodingProbeSuite.Git(root, ["show", "HEAD:value.txt"], ct)).Replace("\r\n", "\n");
            var status = await CodingProbeSuite.Git(root, ["status", "--porcelain", "--untracked-files=all"], ct);
            checks.Add(new("commit", head != baselineCommit && files == "value.txt" && committed == probe.ExpectedContent
                && string.IsNullOrWhiteSpace(status), 0, false, "New commit, explicit file diff and clean working tree verified."));
        }
        var passed = checks.All(c => c.Passed);
        return new(passed ? CodingAcceptanceStatus.Passed : CodingAcceptanceStatus.Failed, passed ? "Coding probe accepted." : "Coding probe failed host acceptance.", checks);
    }

    private static bool IsCheck(System.Text.Json.Nodes.JsonObject input)
    {
        var file = input.FirstOrDefault(p => p.Key.Equals("fileName", StringComparison.OrdinalIgnoreCase)).Value?.ToString();
        var args = input.FirstOrDefault(p => p.Key.Equals("argumentList", StringComparison.OrdinalIgnoreCase)).Value as System.Text.Json.Nodes.JsonArray;
        return file is not null && (file == "git" || Path.GetFileName(file).Equals(Path.GetFileName(GitExecutable.Resolve()), StringComparison.OrdinalIgnoreCase))
            && args is not null && args.Select(n => n?.ToString()).SequenceEqual(["diff", "--no-index", "--", "expected.txt", "value.txt"]);
    }
}
