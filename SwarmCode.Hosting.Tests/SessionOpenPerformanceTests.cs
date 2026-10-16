using System.Diagnostics;
using System.Text.Json.Nodes;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.ToolUseEngine;
using Xunit.Abstractions;

namespace SwarmCode.Hosting.Tests;

/// <summary>Timing tests must not share the machine with the rest of the suite, or budgets measure contention.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SessionOpenPerformanceCollection
{
    public const string Name = "SessionOpenPerformance";
}

/// <summary>
/// Large-history performance budgets. A synthetic session of many turns and tool calls is
/// written through the real stores, then opened in fresh <c>swarmcode --bench-open</c>
/// processes (cold JIT, cold file cache, and once under a constrained GC heap). The median
/// of several attempts must stay inside generous budgets so a regression in listing,
/// transcript folding, history replay, or checkpoint restore is caught before a user with
/// a long session notices. Budgets scale with <c>SWARMCODE_PERF_BUDGET_SCALE</c> for slow
/// machines; every run logs the measured report so the numbers can be read even when the
/// assertions pass.
/// </summary>
[Trait("Category", "Performance")]
[Collection(SessionOpenPerformanceCollection.Name)]
public class SessionOpenPerformanceTests(ITestOutputHelper output) : TempDirTest
{
    private const int Turns = 200;
    private const int ToolCallsPerTurn = 3;
    private const int ToolResultChars = 2_000;
    private const int OtherSessions = 60;
    private const int Attempts = 3;
    private const int ConstrainedHeapMb = 128;

    private static readonly Guid ProfileId = Guid.Parse("b4a2d6f0-5c1e-4a9b-8e7d-0f1a2b3c4d5e");

    private static double Scale => double.TryParse(Environment.GetEnvironmentVariable("SWARMCODE_PERF_BUDGET_SCALE"), out var s) && s > 0 ? s : 1.0;
    private static double Budget(double ms) => ms * Scale;

    [Fact]
    public async Task Opening_a_long_session_in_a_fresh_process_stays_within_budget()
    {
        var conversationId = await GenerateWorkloadAsync();
        var reports = new List<SessionOpenReport>();
        for (var i = 0; i < Attempts; i++) reports.Add(await RunWorkerAsync(conversationId, constrainedHeapMb: null));
        var median = Median(reports);
        output.WriteLine("median (normal heap): " + median.ToJson());

        Assert.Equal(Turns * (1 + ToolCallsPerTurn * 2 + 1), median.TranscriptMessages);
        Assert.Equal(Turns * (2 + ToolCallsPerTurn), median.HistoryEntries);
        Assert.Equal(WorkingContextLoadStatus.Available.ToString(), median.CheckpointStatus);
        Assert.True(median.SessionsListed >= 25);

        Assert.True(median.ListRecentMs <= Budget(300), $"Listing sessions took {median.ListRecentMs} ms with {OtherSessions + 1} sessions on disk; headers only should be far cheaper.");
        Assert.True(median.TranscriptLoadMs <= Budget(1_500), $"Transcript load took {median.TranscriptLoadMs} ms for {median.TranscriptMessages} messages.");
        Assert.True(median.HistoryLoadMs <= Budget(1_500), $"History replay took {median.HistoryLoadMs} ms for {median.HistoryEntries} entries.");
        Assert.True(median.CheckpointRestoreMs <= Budget(1_500), $"Checkpoint restore took {median.CheckpointRestoreMs} ms.");
        Assert.True(median.TotalMs <= Budget(5_000), $"Opening the session took {median.TotalMs} ms in total.");
        Assert.True(median.RetainedHeapMb <= 200, $"The opened session retains {median.RetainedHeapMb} MB of managed heap.");
    }

    [Fact]
    public async Task Opening_a_long_session_completes_under_a_constrained_heap()
    {
        var conversationId = await GenerateWorkloadAsync();
        var report = await RunWorkerAsync(conversationId, constrainedHeapMb: ConstrainedHeapMb);
        output.WriteLine($"constrained heap ({ConstrainedHeapMb} MB): " + report.ToJson());
        Assert.Equal(Turns * (2 + ToolCallsPerTurn), report.HistoryEntries);
        Assert.Equal(WorkingContextLoadStatus.Available.ToString(), report.CheckpointStatus);
        Assert.True(report.TotalMs <= Budget(10_000), $"Opening the session under a {ConstrainedHeapMb} MB heap took {report.TotalMs} ms.");
    }

    [Fact]
    public async Task In_process_benchmark_reports_every_phase()
    {
        var conversationId = await GenerateWorkloadAsync(turns: 20, otherSessions: 3);
        var report = await SessionOpenBenchmark.RunAsync(Paths, conversationId, ProfileId);
        output.WriteLine("in-process: " + report.ToJson());
        Assert.Equal(20 * (1 + ToolCallsPerTurn * 2 + 1), report.TranscriptMessages);
        Assert.Equal(20 * (2 + ToolCallsPerTurn), report.HistoryEntries);
        Assert.Equal(4, report.SessionsListed);
        Assert.Equal(WorkingContextLoadStatus.Available.ToString(), report.CheckpointStatus);
        Assert.True(report.TotalMs >= 0 && report.RetainedHeapMb > 0);
        var roundTrip = SessionOpenReport.FromJson(report.ToJson());
        Assert.Equal(report, roundTrip);
    }

    // ----- workload -----

    /// <summary>
    /// Writes a session through the real stores: conversation header + turns journal, provider
    /// transcript, presentation journal, and a published working-context checkpoint covering
    /// the whole transcript. Also writes a number of small sessions so listing has to skip them.
    /// </summary>
    private async Task<string> GenerateWorkloadAsync(int turns = Turns, int otherSessions = OtherSessions)
    {
        var conversations = new FileConversationStore(Paths);
        var transcripts = new FileAgentSessionStore(Paths);
        var journal = new FileTranscriptJournal(Paths);
        var conversationId = "bench-" + Guid.NewGuid().ToString("N");
        await conversations.GetOrCreateAsync(conversationId, "Benchmark session", ProfileId);

        var result = new string('r', ToolResultChars);
        var transcript = new List<ChatMessage>(turns * (2 + ToolCallsPerTurn * 2));
        var entries = new List<TranscriptEntry>(turns * (2 + ToolCallsPerTurn));
        for (var t = 0; t < turns; t++)
        {
            var userText = $"Turn {t}: inspect module-{t} and fix the warning.";
            var turn = await conversations.AppendUserTurnAsync(conversationId, userText);
            transcript.Add(ChatMessage.Text(ChatRole.User, userText));
            entries.Add(new UserEntry { Text = userText });
            for (var c = 0; c < ToolCallsPerTurn; c++)
            {
                var id = $"call-{t}-{c}";
                var input = new JsonObject { ["command"] = $"dotnet build module-{t} --step {c}" };
                transcript.Add(new ChatMessage(ChatRole.Assistant, [new ToolUsePart(id, "shell.exec", input)]));
                transcript.Add(new ChatMessage(ChatRole.Tool, [new ToolResultPart(id, result)]));
                entries.Add(new ToolCallEntry { ToolCallId = id, ToolName = "shell.exec", InputJson = input.ToJsonString(), Result = result, State = ToolCallState.Completed, Elapsed = TimeSpan.FromMilliseconds(12) });
            }
            var answer = $"Fixed module-{t}; the build is clean.";
            transcript.Add(ChatMessage.Text(ChatRole.Assistant, answer));
            await conversations.CompleteAssistantTurnAsync(turn.Id, answer, ProfileId, TimeSpan.FromSeconds(3));
            var assistant = new AssistantEntry { IsStreaming = false, LaneName = "claude", Duration = TimeSpan.FromSeconds(3) };
            assistant.ReplaceText(answer);
            entries.Add(assistant);
        }
        await transcripts.AppendRangeAsync(conversationId, transcript);
        journal.AppendRange(conversationId, entries);

        // A checkpoint over the whole durable transcript, published the way the engine does at a boundary.
        var manager = new WorkingContextCheckpointManager(new FileWorkingContextCheckpointStore(Paths), new CodingWorkingContextSourceValidator());
        var state = new WorkingContextState(new WorkingContextIdentity(ProfileId, conversationId, null), new WorkingContextAuthority { Objective = "benchmark", Digest = SessionOpenBenchmark.AuthorityDigest })
        { CommittedPrefixCount = transcript.Count };
        state.PersistDurableBoundaryAsync = _ => Task.CompletedTask;
        var snapshot = new WorkingContextSnapshot([], [], [], [], [], []);
        var published = await manager.PublishAsync(state, new WorkingContextCompactionResult(transcript, snapshot, 1, 1, 0, true), transcript, CancellationToken.None, isCompaction: false);
        Assert.True(published, "The benchmark checkpoint must publish.");

        for (var i = 0; i < otherSessions; i++)
        {
            var other = $"other-{i:D3}";
            var row = await conversations.AppendUserTurnAsync(other, $"small question {i}");
            await conversations.CompleteAssistantTurnAsync(row.Id, "small answer", ProfileId, TimeSpan.FromSeconds(1));
        }
        return conversationId;
    }

    // ----- fresh-process worker -----

    private async Task<SessionOpenReport> RunWorkerAsync(string conversationId, int? constrainedHeapMb)
    {
        var baseDir = AppContext.BaseDirectory;
        var worker = Path.Combine(baseDir, "swarmcode.dll");
        Assert.True(File.Exists(worker), $"The console worker was not copied beside the tests: {worker}");
        var testAssembly = Path.GetFileNameWithoutExtension(typeof(SessionOpenPerformanceTests).Assembly.Location);
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet",
            WorkingDirectory = baseDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // Run the console inside the test project's dependency closure so no separate publish is needed.
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("--depsfile");
        startInfo.ArgumentList.Add(Path.Combine(baseDir, testAssembly + ".deps.json"));
        startInfo.ArgumentList.Add("--runtimeconfig");
        startInfo.ArgumentList.Add(Path.Combine(baseDir, testAssembly + ".runtimeconfig.json"));
        startInfo.ArgumentList.Add(worker);
        startInfo.ArgumentList.Add("--bench-open");
        startInfo.ArgumentList.Add(conversationId);
        startInfo.ArgumentList.Add("--profile");
        startInfo.ArgumentList.Add(ProfileId.ToString());
        startInfo.Environment[SwarmCodeDataPaths.HomeEnvironmentVariable] = Paths.RootDirectory;
        if (constrainedHeapMb is { } mb)
            startInfo.Environment["DOTNET_GCHeapHardLimit"] = "0x" + ((long)mb * 1024 * 1024).ToString("X");

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            Assert.Fail("The session-open worker did not finish within two minutes.");
        }
        var text = await stdout;
        var errors = await stderr;
        Assert.True(process.ExitCode == 0, $"Worker exited with {process.ExitCode}.\nstdout: {text}\nstderr: {errors}");
        var line = text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{'));
        Assert.False(string.IsNullOrEmpty(line), $"Worker printed no report.\nstdout: {text}\nstderr: {errors}");
        var report = SessionOpenReport.FromJson(line!);
        Assert.NotNull(report);
        output.WriteLine((constrainedHeapMb is null ? "attempt: " : $"constrained {constrainedHeapMb} MB: ") + line);
        return report!;
    }

    private static SessionOpenReport Median(List<SessionOpenReport> reports)
    {
        static double Mid(IEnumerable<double> values) { var s = values.OrderBy(v => v).ToList(); return s[s.Count / 2]; }
        var first = reports[0];
        return first with
        {
            ProcessStartMs = Mid(reports.Select(r => r.ProcessStartMs)),
            ListRecentMs = Mid(reports.Select(r => r.ListRecentMs)),
            TranscriptLoadMs = Mid(reports.Select(r => r.TranscriptLoadMs)),
            HistoryLoadMs = Mid(reports.Select(r => r.HistoryLoadMs)),
            CheckpointRestoreMs = Mid(reports.Select(r => r.CheckpointRestoreMs)),
            TotalMs = Mid(reports.Select(r => r.TotalMs)),
            RetainedHeapMb = Mid(reports.Select(r => r.RetainedHeapMb)),
            WorkingSetMb = Mid(reports.Select(r => r.WorkingSetMb)),
        };
    }
}
