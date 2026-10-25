using System.Text.Json;
using SwarmCode.Hosting.Llm;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCode.Hosting.Tests;

/// <summary>
/// The routing audit ledger appends one line per row (it is written several times per model call and must not
/// rewrite its history each time), migrates the old whole-document file, and survives a line cut short by a crash.
/// The usage ledger keeps an unreadable file aside instead of silently replacing 90 days of history.
/// </summary>
public sealed class LlmLedgerTests : TempDirTest
{
    private static LlmRoutingAuditEntry Row(string kind = LlmRoutingAuditEventKinds.Selection, string functionType = "agent-chat")
        => new() { EventKind = kind, FunctionType = functionType, AfterProvider = "fixture", AfterModel = "model-a" };

    private string AuditLines => Path.ChangeExtension(Paths.RoutingAuditPath, ".jsonl");

    [Fact]
    public async Task Appending_adds_a_line_without_rewriting_the_ledger()
    {
        var store = new JsonLlmRoutingAuditStore(Paths);
        await store.AppendAsync(Row());
        await store.AppendAsync(Row());
        var before = File.ReadAllText(AuditLines);

        await store.AppendAsync(Row(LlmRoutingAuditEventKinds.PreferenceChanged));

        var after = File.ReadAllText(AuditLines);
        Assert.StartsWith(before, after, StringComparison.Ordinal);
        Assert.Equal(3, File.ReadAllLines(AuditLines).Length);
        var reloaded = await new JsonLlmRoutingAuditStore(Paths).QueryAsync(new LlmRoutingAuditQuery { Limit = 10 });
        Assert.Equal(3, reloaded.Count);
    }

    [Fact]
    public async Task The_old_whole_document_ledger_is_migrated_once()
    {
        Directory.CreateDirectory(Paths.RootDirectory);
        File.WriteAllText(Paths.RoutingAuditPath, JsonSerializer.Serialize(new List<LlmRoutingAuditEntry> { Row(), Row() }));

        var rows = await new JsonLlmRoutingAuditStore(Paths).QueryAsync(new LlmRoutingAuditQuery { Limit = 10 });

        Assert.Equal(2, rows.Count);
        Assert.False(File.Exists(Paths.RoutingAuditPath));
        Assert.Equal(2, File.ReadAllLines(AuditLines).Length);
    }

    [Fact]
    public async Task A_line_cut_short_by_a_crash_is_skipped_and_the_original_kept_aside()
    {
        Directory.CreateDirectory(Paths.RootDirectory);
        var good = JsonSerializer.Serialize(Row());
        File.WriteAllText(AuditLines, good + "\n" + good + "\n" + good[..(good.Length / 2)]);

        var store = new JsonLlmRoutingAuditStore(Paths);
        Assert.Equal(2, (await store.QueryAsync(new LlmRoutingAuditQuery { Limit = 10 })).Count);
        Assert.Single(Directory.GetFiles(Paths.RootDirectory, "llm-routing-audit.jsonl.corrupt-*"));

        await store.AppendAsync(Row());
        Assert.Equal(3, (await new JsonLlmRoutingAuditStore(Paths).QueryAsync(new LlmRoutingAuditQuery { Limit = 10 })).Count);
    }

    [Fact]
    public async Task An_unreadable_usage_file_is_kept_aside_and_per_model_totals_are_reported()
    {
        Directory.CreateDirectory(Paths.RootDirectory);
        File.WriteAllText(Paths.UsagePath, "[{\"broken\": ");
        var tracker = new JsonLlmUsageTracker(Paths);

        await tracker.TrackUsageAsync(new LlmUsageRecord { FunctionType = "agent-chat", Provider = "fixture", Model = "model-a", TotalTokens = 120 });
        await tracker.TrackUsageAsync(new LlmUsageRecord { FunctionType = "agent-chat", Provider = "fixture", Model = "model-b", TotalTokens = 30 });

        var aside = Assert.Single(Directory.GetFiles(Paths.RootDirectory, "llm-usage.json.corrupt-*"));
        Assert.Equal("[{\"broken\": ", File.ReadAllText(aside));
        var summary = await tracker.GetCostSummaryAsync();
        Assert.Equal(120, summary.TokensByModel["model-a"]);
        Assert.Equal(30, summary.TokensByModel["model-b"]);
        Assert.Equal(2, summary.RequestsByFunction["agent-chat"]);
    }
}
