using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.Runtime;
using SwarmCore.Agent.ToolUseEngine;

namespace SwarmCode.Hosting;

/// <summary>Phase timings and memory for opening one session from its files, as a fresh process would.</summary>
public sealed record SessionOpenReport
{
    public required string ConversationId { get; init; }
    /// <summary>Milliseconds from process start until the benchmark began (runtime and host start-up).</summary>
    public double ProcessStartMs { get; init; }
    /// <summary>Listing recent sessions (header files only).</summary>
    public double ListRecentMs { get; init; }
    public int SessionsListed { get; init; }
    /// <summary>Loading the provider transcript (JSONL fold).</summary>
    public double TranscriptLoadMs { get; init; }
    public int TranscriptMessages { get; init; }
    /// <summary>Rebuilding the visible history (presentation journal replay plus reconciliation).</summary>
    public double HistoryLoadMs { get; init; }
    public int HistoryEntries { get; init; }
    /// <summary>Restoring the working-context checkpoint for the next turn.</summary>
    public double CheckpointRestoreMs { get; init; }
    public string CheckpointStatus { get; init; } = "";
    public double TotalMs { get; init; }
    /// <summary>Managed heap retained after a full collection, with the loaded session still referenced.</summary>
    public double RetainedHeapMb { get; init; }
    public double WorkingSetMb { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static SessionOpenReport? FromJson(string json) => JsonSerializer.Deserialize<SessionOpenReport>(json, Json);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
}

/// <summary>
/// Measures what reopening a session costs: listing sessions, loading the provider
/// transcript, rebuilding the visible history, and restoring the working-context
/// checkpoint, plus the memory the loaded session retains. The console exposes it as
/// <c>swarmcode --bench-open</c> so tests can run it in a fresh process (cold file caches,
/// cold JIT, optionally a constrained heap) and catch degradation as sessions grow.
/// </summary>
public static class SessionOpenBenchmark
{
    /// <summary>Authority digest the benchmark checkpoint is published and restored with.</summary>
    public const string AuthorityDigest = "session-open-benchmark";

    public static async Task<SessionOpenReport> RunAsync(SwarmCodeDataPaths paths, string conversationId, Guid profileId, CancellationToken ct = default)
    {
        var processStart = (DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalMilliseconds;
        var total = Stopwatch.StartNew();

        var conversations = new FileConversationStore(paths);
        var transcripts = new FileAgentSessionStore(paths);
        var journal = new FileTranscriptJournal(paths);

        var sw = Stopwatch.StartNew();
        var listed = await conversations.ListRecentAsync(25, ct).ConfigureAwait(false);
        var listMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        var transcript = await transcripts.LoadAsync(conversationId, ct).ConfigureAwait(false);
        var transcriptMs = sw.Elapsed.TotalMilliseconds;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConversationStore>(conversations);
        services.AddSingleton<IAgentSessionStore>(transcripts);
        services.AddSingleton(journal);
        using var provider = services.BuildServiceProvider();
        using var runner = new ConversationRunner(provider, conversationId);
        sw.Restart();
        await runner.LoadHistoryAsync(ct).ConfigureAwait(false);
        var historyMs = sw.Elapsed.TotalMilliseconds;
        var entries = runner.Entries;

        var manager = new WorkingContextCheckpointManager(new FileWorkingContextCheckpointStore(paths), new CodingWorkingContextSourceValidator());
        var state = new WorkingContextState(new WorkingContextIdentity(profileId, conversationId, null), new WorkingContextAuthority { Objective = "benchmark", Digest = AuthorityDigest });
        sw.Restart();
        var restored = await manager.RestoreAsync(state, transcript, ct).ConfigureAwait(false);
        var checkpointMs = sw.Elapsed.TotalMilliseconds;
        total.Stop();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var heap = GC.GetTotalMemory(forceFullCollection: false);
        var workingSet = Process.GetCurrentProcess().WorkingSet64;

        // Keep the loaded objects alive through the measurement.
        GC.KeepAlive(transcript);
        GC.KeepAlive(entries);
        GC.KeepAlive(restored);
        GC.KeepAlive(listed);

        return new SessionOpenReport
        {
            ConversationId = conversationId,
            ProcessStartMs = Math.Round(processStart, 1),
            ListRecentMs = Math.Round(listMs, 1),
            SessionsListed = listed.Count,
            TranscriptLoadMs = Math.Round(transcriptMs, 1),
            TranscriptMessages = transcript.Count,
            HistoryLoadMs = Math.Round(historyMs, 1),
            HistoryEntries = entries.Count,
            CheckpointRestoreMs = Math.Round(checkpointMs, 1),
            CheckpointStatus = restored.Status.ToString(),
            TotalMs = Math.Round(total.Elapsed.TotalMilliseconds, 1),
            RetainedHeapMb = Math.Round(heap / 1048576.0, 1),
            WorkingSetMb = Math.Round(workingSet / 1048576.0, 1),
        };
    }
}
