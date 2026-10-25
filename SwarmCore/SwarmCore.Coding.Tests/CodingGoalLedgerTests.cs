using SwarmCore.Agent.Continual;
using SwarmCore.Coding.Goals;

namespace SwarmCore.Coding.Tests;

public sealed class CodingGoalLedgerTests
{
    private static readonly Guid GoalId = Guid.Parse("5d2d987d-1177-4cb1-9175-4cce567b11e7");
    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 20, 0, 0, TimeSpan.Zero);

    private static ContinualObjectiveSnapshot Goal(params ContinualToolReceipt[] receipts) => new()
    {
        Id = GoalId, Surface = "coding-goal", PartitionKey = "talavine-code", IdempotencyKey = "k", ConversationId = "conversation",
        ExecutorKey = "coding.goal.v1", ObjectiveText = "Grow", ObjectiveSummary = "Grow",
        Authority = ContinualObjectiveAuthority.UserRequested("test"), Status = ContinualObjectiveStatus.InProgress,
        AttemptCount = 3, Version = 7, LastEventSequence = 99, Receipts = receipts,
    };

    private static ContinualToolReceipt Receipt(long sequence, string tool, ContinualSideEffectClass effect, string? input, string? result,
        bool error = false, int attempt = 3, ContinualToolReceiptState state = ContinualToolReceiptState.Completed)
        => new(GoalId, attempt, sequence, "call-" + sequence, tool, state, effect, input, result, error, T0.AddMinutes(sequence),
            state == ContinualToolReceiptState.Completed ? T0.AddMinutes(sequence).AddSeconds(5) : null);

    [Fact]
    public void A_compacted_result_is_summarized_from_its_kept_excerpt()
    {
        var full = "Deployed the site and verified the home page.\n" + new string('x', 5_000);
        var receipt = ContinualReceiptRetentionPolicy.Compact(
            Receipt(4, "codex.command", ContinualSideEffectClass.Exec, "{\"command\":\"deploy.sh\"}", full),
            new ContinualReceiptRetentionOptions(), T0.AddDays(40));
        Assert.NotNull(receipt.CompactedResult);
        var entry = CodingGoalLedger.Describe(Goal(receipt), receipt, includeObservations: false)!;
        Assert.StartsWith("Deployed the site and verified the home page.", entry.Summary);
        Assert.DoesNotContain("Compacted receipt text", entry.Summary);
    }

    [Fact]
    public void Read_only_calls_are_hidden_unless_requested_and_entries_are_newest_first()
    {
        var goal = Goal(
            Receipt(1, "codex.mcp_tool", ContinualSideEffectClass.ReadOnly, "{\"server\":\"swarmcode\",\"tool\":\"fs_read_json\"}", "{\"ok\":true}"),
            Receipt(2, "codex.command", ContinualSideEffectClass.Exec, "{\"command\":\"/bin/zsh -lc 'git status --short\\n git log -1'\"}", "{\"exitCode\":0,\"output\":\"## master\"}"),
            Receipt(3, "codex.mcp_tool", ContinualSideEffectClass.ReadOnly, "{\"server\":\"swarmcode\",\"tool\":\"code_grep\"}", "{}", error: true));
        var visible = CodingGoalLedger.Build(goal);
        var entry = Assert.Single(visible);
        Assert.Equal(CodingGoalLedgerKind.Ran, entry.Kind);
        Assert.Equal("Ran: git status --short", entry.Title);
        Assert.Equal("## master", entry.Summary);
        var all = CodingGoalLedger.Build(goal, includeObservations: true);
        Assert.Equal([3L, 2L, 1L], all.Select(e => e.Sequence));
        Assert.Equal(CodingGoalLedgerKind.Failed, all[0].Kind);
        Assert.Equal(CodingGoalLedgerKind.Observed, all[2].Kind);
        Assert.Equal("Used fs_read_json", all[2].Title);
    }

    [Fact]
    public void External_sends_become_published_entries_with_deduplicated_cleaned_links()
    {
        var result = "{\"content\":[{\"type\":\"text\",\"text\":\"{\\u0022content\\u0022:[{\\u0022type\\u0022:\\u0022text\\u0022,\\u0022text\\u0022:\\u0022Posted https://github.com/scrossen/talavine-code/discussions/8#discussioncomment-19158534. See https://github.com/scrossen/talavine-code/discussions/8#discussioncomment-19158534 and http://localhost:5000/x plus http://microsoft.com/schemas/VisualStudio/TeamTest/2010\\u0022}]}\"}]}";
        var goal = Goal(Receipt(5, "codex.mcp_tool", ContinualSideEffectClass.ExternalSend,
            "{\"server\":\"swarmcode\",\"tool\":\"browser_act\",\"arguments\":{\"url\":\"https://github.com/scrossen/talavine-code/discussions/8\"}}", result));
        var entry = Assert.Single(CodingGoalLedger.Build(goal));
        Assert.Equal(CodingGoalLedgerKind.Published, entry.Kind);
        Assert.Equal("Browser act: github.com/scrossen/talavine-code/discussions/8", entry.Title);
        Assert.StartsWith("Posted https://github.com/scrossen/talavine-code/discussions/8", entry.Summary);
        Assert.Equal(
            ["https://github.com/scrossen/talavine-code/discussions/8#discussioncomment-19158534", "https://github.com/scrossen/talavine-code/discussions/8"],
            entry.Links.Select(link => link.Url));
        Assert.Equal("github.com/scrossen/talavine-code/discussions/8#discussioncomment-19158534", entry.Links[0].Label);
    }

    [Fact]
    public void File_changes_list_their_paths_and_searches_show_their_query()
    {
        var goal = Goal(
            Receipt(7, "codex.file_change", ContinualSideEffectClass.WritesLocal,
                "{\"changes\":[{\"path\":\"/repo/docs/guide.md\",\"kind\":\"update\"},{\"path\":\"docs/new.md\",\"kind\":\"add\"}]}",
                "{\"id\":\"item_32\",\"type\":\"file_change\",\"status\":\"completed\"}"),
            Receipt(8, "codex.web_search", ContinualSideEffectClass.Exec, "{\"query\":\"\"}", "{\"type\":\"web_search\",\"query\":\"talavine code review\",\"results\":[]}"),
            Receipt(6, "codex.file_change", ContinualSideEffectClass.WritesLocal, "{\"changes\":[{\"path\":\"/repo/docs/guide.md\",\"kind\":\"update\"}]}", "{\"status\":\"completed\"}"),
            Receipt(5, "codex.mcp_tool", ContinualSideEffectClass.ReadOnly, "{\"server\":\"swarmcode\",\"tool\":\"fs_read_json\",\"arguments\":{\"path\":\"docs/growth/state.json\"}}", "{\"ok\":true}"));
        var entries = CodingGoalLedger.Build(goal, includeObservations: true);
        Assert.Equal("Searched: talavine code review", entries[0].Title);
        Assert.Equal(CodingGoalLedgerKind.Changed, entries[1].Kind);
        Assert.Equal("Edited 2 files", entries[1].Title);
        Assert.Equal(["/repo/docs/guide.md", "docs/new.md"], entries[1].Files);
        Assert.Null(entries[1].Summary);
        Assert.Equal("Edited guide.md", entries[2].Title);
        Assert.Equal("Fs read json: state.json", entries[3].Title);
        Assert.Null(entries[3].Summary);
        Assert.Empty(entries[3].Files);
        Assert.Empty(entries[0].Files);
    }

    [Fact]
    public void Errors_open_older_attempts_replies_and_host_proofs_are_distinguished()
    {
        var target = Receipt(10, "codex.mcp_tool", ContinualSideEffectClass.Exec, "{\"server\":\"swarmcode\",\"tool\":\"computer_act\"}", "{\"Succeeded\":true}");
        var observation = Receipt(13, "host.goal-outcome-inspection", ContinualSideEffectClass.ReadOnly, "{}", "observed");
        var verified = target with
        {
            HostResolution = new ContinualReceiptResolution(GoalId, 3, "call-10", ContinualReceiptResolutionPolicy.Fingerprint(target),
                ContinualReceiptResolutionOutcome.KnownOutcome, "host", "evidence",
                [new(3, "call-13", ContinualReceiptResolutionPolicy.Fingerprint(observation))], T0.AddMinutes(14)),
        };
        var goal = Goal(observation,
            Receipt(9, "codex.command", ContinualSideEffectClass.Exec, "{\"command\":\"false\"}", "{\"exitCode\":1,\"output\":\"\",\"error\":\"boom\"}", error: true),
            verified,
            Receipt(11, "codex.mcp_tool", ContinualSideEffectClass.ExecutionMetadata, "{\"server\":\"swarmcode\",\"tool\":\"goal_messages_reply\"}", "{\"content\":[{\"type\":\"text\",\"text\":\"Reply saved.\"}]}"),
            Receipt(4, "codex.mcp_tool", ContinualSideEffectClass.ExternalSend, "{\"server\":\"swarmcode\",\"tool\":\"browser_act\"}", null, attempt: 1, state: ContinualToolReceiptState.Started),
            Receipt(12, "codex.mcp_tool", ContinualSideEffectClass.Exec, "{\"server\":\"swarmcode\",\"tool\":\"shell_exec\"}", null, state: ContinualToolReceiptState.Started));
        var entries = CodingGoalLedger.Build(goal);
        Assert.Equal([11L, 10L, 9L, 4L], entries.Select(e => e.Sequence));
        Assert.Equal(CodingGoalLedgerKind.Reported, entries[0].Kind);
        Assert.Equal("Reply saved.", entries[0].Summary);
        Assert.True(entries[1].HostVerified);
        Assert.Equal(CodingGoalLedgerKind.Ran, entries[1].Kind);
        Assert.Equal(CodingGoalLedgerKind.Failed, entries[2].Kind);
        Assert.Equal("boom", entries[2].Summary);
        Assert.Equal(CodingGoalLedgerKind.Uncertain, entries[3].Kind);
        Assert.Equal(T0.AddMinutes(4), entries[3].AtUtc);
        var paused = CodingGoalLedger.Build(goal with { Status = ContinualObjectiveStatus.Paused });
        Assert.Contains(paused, e => e.Sequence == 12 && e.Kind == CodingGoalLedgerKind.Uncertain);
    }

    [Fact]
    public void The_builder_reuses_descriptions_and_shows_verified_only_for_a_matching_proof()
    {
        var builder = new CodingGoalLedgerBuilder();
        var receipts = new[]
        {
            Receipt(1, "codex.command", ContinualSideEffectClass.Exec, "{\"command\":\"echo one\"}", "{\"output\":\"one two\"}"),
            Receipt(2, "codex.command", ContinualSideEffectClass.Exec, "{\"command\":\"echo two\"}", "{\"output\":\"two three\"}"),
        };
        var goal = Goal(receipts);
        var first = builder.Build(goal);
        var second = builder.Build(goal);
        Assert.Equal(2, first.Count);
        Assert.All(first.Zip(second), pair => Assert.Same(pair.First, pair.Second));
        var third = builder.Build(Goal([.. receipts, Receipt(3, "codex.command", ContinualSideEffectClass.Exec, "{\"command\":\"echo three\"}", "{\"output\":\"three four\"}")]));
        Assert.Equal([3L, 2L, 1L], third.Select(e => e.Sequence));
        Assert.Same(first[0], third[1]);
        var unproven = Receipt(4, "codex.mcp_tool", ContinualSideEffectClass.Exec, "{\"server\":\"swarmcode\",\"tool\":\"computer_act\"}", "{\"Succeeded\":true}") with
        {
            HostResolution = new ContinualReceiptResolution(GoalId, 3, "call-4", "not-the-fingerprint", ContinualReceiptResolutionOutcome.KnownOutcome,
                "host", "evidence", [new(3, "missing", "x")], T0.AddHours(1)),
        };
        Assert.False(Assert.Single(builder.Build(Goal(unproven))).HostVerified);
    }

    [Fact]
    public void Own_host_process_calls_show_their_command_and_list_no_files()
    {
        var goal = Goal(Receipt(21, "codex.mcp_tool", ContinualSideEffectClass.Exec,
            "{\"server\":\"swarmcode\",\"tool\":\"shell_exec\",\"arguments\":{\"fileName\":\"python3\",\"argumentList\":[\"-B\",\"-c\",\"import json\\nprint(1)\"],\"timeoutSeconds\":15}}",
            "{\"content\":[{\"type\":\"text\",\"text\":\"{\\\"ExitCode\\\":0,\\\"Stdout\\\":\\\"1\\\\n\\\",\\\"Stderr\\\":\\\"\\\",\\\"TimedOut\\\":false}\"}]}"));
        var entry = Assert.Single(CodingGoalLedger.Build(goal));
        Assert.Equal(CodingGoalLedgerKind.Ran, entry.Kind);
        Assert.Equal("Shell exec: python3 -B -c import json", entry.Title);
        Assert.Empty(entry.Files);
        Assert.Null(entry.Summary);
    }

    [Fact]
    public void Pages_report_totals_offsets_and_whether_more_remain()
    {
        var goal = Goal(Enumerable.Range(1, 7).Select(i => Receipt(i, "codex.command", ContinualSideEffectClass.Exec, "{\"command\":\"echo " + i + "\"}", "{\"output\":\"" + i + "\"}")).ToArray());
        var entries = CodingGoalLedger.Build(goal);
        var first = CodingGoalLedger.Page(goal, entries, 0, 3, "/repo");
        Assert.Equal(3, first.Entries.Count); Assert.Equal(7, first.Total); Assert.True(first.HasMore); Assert.Equal("/repo", first.RepositoryRoot);
        Assert.Equal([7L, 6L, 5L], first.Entries.Select(e => e.Sequence));
        var last = CodingGoalLedger.Page(goal, entries, 6, 3);
        Assert.Single(last.Entries); Assert.False(last.HasMore); Assert.Equal(6, last.Offset);
        Assert.Equal(7, last.GoalVersion); Assert.Equal(99, last.EventSequence);
        var beyond = CodingGoalLedger.Page(goal, entries, 40, 3);
        Assert.Empty(beyond.Entries); Assert.False(beyond.HasMore);
    }
}
