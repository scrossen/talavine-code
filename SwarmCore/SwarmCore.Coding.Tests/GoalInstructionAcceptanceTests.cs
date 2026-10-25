using System.Reflection;
using System.Text.Json;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.Continual;
using SwarmCore.Agent.Communication;
using SwarmCore.Coding.Goals;

namespace SwarmCore.Coding.Tests;

public sealed class GoalInstructionAcceptanceTests
{
    private static readonly Guid Id = Guid.NewGuid();
    private static readonly DateTimeOffset Time = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
    private static GoalInstructionSource Source(int i, string text = "Finish the requested work") => new(i == 0 ? "objective" : "message-" + i, i, i == 0 ? "Objective" : "Correction", text, Time.AddMinutes(i));
    private static ContinualAcceptanceRequest Request(GoalInstructionSource first) => new(Id, first.Text,
        new string('o', 7000) + "FULL_OUTPUT_END", [], false);
    public class Router : DispatchProxy
    {
        public List<(string Prompt, LlmRouterOptions Options)> Calls { get; } = [];
        public Func<string, GoalInstructionAudit> Answer { get; set; } = Delivered;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Assert.Equal("GenerateResponseAsync", method!.Name); Assert.True(method.IsGenericMethod);
            Assert.Equal(FunctionTypes.AgentChat, args![0]);
            var prompt = (string)args[1]!; Calls.Add((prompt, (LlmRouterOptions)args[2]!));
            return Task.FromResult(Answer(prompt));
        }
    }
    private static GoalInstructionSource Exact(string prompt, string marker = "Exact source: ")
        => JsonSerializer.Deserialize<GoalInstructionSource>(prompt.Split(marker)[1].Split('\n')[0])!;
    private static GoalInstructionSource[] Later(string prompt)
        => JsonSerializer.Deserialize<GoalInstructionSource[]>(prompt.Split("Exact later sources: ")[1].Split('\n')[0])!;
    private static GoalInstructionAudit Delivered(string prompt)
    {
        var source = Exact(prompt);
        return new() { SourceId = source.Id, SourceSha256 = source.Sha256, Verdict = "delivered", HostDisposition = "completed" };
    }
    private static (GoalInstructionAcceptance Service, Router Router) Auditor(Func<string, GoalInstructionAudit>? answer = null)
    {
        var router = DispatchProxy.Create<ILlmRouter, Router>(); var proxy = (Router)(object)router;
        if (answer is not null) proxy.Answer = answer;
        return (new(router), proxy);
    }

    [Fact]
    public async Task More_than_one_context_of_exact_sources_is_audited_once_each_without_truncating_output()
    {
        var sources = Enumerable.Range(0, 40).Select(i => Source(i, "Instruction " + i + new string((char)('a' + i % 20), 8000) + " SOURCE_END_" + i)).ToArray();
        var a = Auditor(); var request = Request(sources[0]);
        var result = await a.Service.EvaluateAsync(request, new(Id, sources));
        Assert.Equal(ContinualAcceptanceOutcome.Delivered, result.Outcome); Assert.Equal(40, a.Router.Calls.Count);
        Assert.All(a.Router.Calls, c => { Assert.Contains("FULL_OUTPUT_END", c.Prompt); Assert.True(c.Prompt.Length < GoalInstructionAcceptance.MaximumPromptCharacters); });
        for (var i = 0; i < sources.Length; i++) Assert.Contains("SOURCE_END_" + i, a.Router.Calls[i].Prompt);
        Assert.All(a.Router.Calls, c => Assert.Equal(1, c.Options.RequestControl!.MaxPhysicalRequests));
    }

    [Fact]
    public async Task Partial_original_correction_requires_an_independent_full_pair_and_preserves_remaining_obligations()
    {
        var sources = new[] { Source(0, "Use blue. Also preserve all data."), Source(1, "Correction: use green instead of blue.") };
        var a = Auditor(prompt =>
        {
            var answer = Delivered(prompt);
            if (Exact(prompt).Order == 0)
            {
                if (prompt.Contains("Independent chronological source-bundle audit:"))
                {
                    var later = Later(prompt).Single(); answer.LaterSourceBindings = [new(later.Id, later.Order, later.Sha256)];
                    answer.SupersessionVerified = true;
                    Assert.Contains("all earlier obligations NOT changed", prompt); Assert.Contains("Also preserve all data.", prompt);
                }
                else { answer.Verdict = "retry"; answer.LaterSourceIds = [sources[1].Id]; }
            }
            return answer;
        });
        Assert.Equal(ContinualAcceptanceOutcome.Delivered, (await a.Service.EvaluateAsync(Request(sources[0]), new(Id, sources))).Outcome);
        Assert.Equal(3, a.Router.Calls.Count);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("objective")]
    public async Task Invented_or_nonlater_override_never_waives_an_obligation(string reference)
    {
        var sources = new[] { Source(0), Source(1) };
        var a = Auditor(p => { var x = Delivered(p); x.LaterSourceIds = [reference]; return x; });
        var result = await a.Service.EvaluateAsync(Request(sources[0]), new(Id, sources));
        Assert.Equal(ContinualAcceptanceOutcome.VerificationUnavailable, result.Outcome); Assert.Single(a.Router.Calls);
    }

    [Fact]
    public async Task Failed_pair_and_null_model_arrays_are_unavailable_not_pass_or_execution_retry()
    {
        var sources = new[] { Source(0), Source(1) };
        foreach (var nullArray in new[] { false, true })
        {
            var a = Auditor(p => { var x = Delivered(p); x.LaterSourceIds = nullArray ? null! : [sources[1].Id]; return x; });
            var result = await a.Service.EvaluateAsync(Request(sources[0]), new(Id, sources));
            Assert.Equal(ContinualAcceptanceOutcome.VerificationUnavailable, result.Outcome); Assert.True(result.EvaluationUnavailable);
        }
    }

    [Fact]
    public async Task Missing_lookup_page_is_conservative_and_explicit_lookup_can_fetch_another_bounded_page()
    {
        var sources = Enumerable.Range(0, 30).Select(i => Source(i)).ToArray();
        var a = Auditor(p => { var x = Delivered(p); x.Verdict = "retry"; return x; });
        Assert.Equal(ContinualAcceptanceOutcome.VerificationUnavailable, (await a.Service.EvaluateAsync(Request(sources[0]), new(Id, sources))).Outcome);
        var first = true;
        a = Auditor(p => { var x = Delivered(p); if (first) { first = false; x.Verdict = "lookup"; x.LookupPage = 1; } return x; });
        Assert.Equal(ContinualAcceptanceOutcome.Delivered, (await a.Service.EvaluateAsync(Request(sources[0]), new(Id, sources))).Outcome);
        Assert.Equal(31, a.Router.Calls.Count); Assert.Contains("\"Page\":1", a.Router.Calls[1].Prompt);
    }

    [Fact]
    public async Task Exact_route_and_prospective_time_context_are_preserved_without_changing_router_admission()
    {
        var sources = new[] { Source(0), Source(1, "Do not post any further messages.") };
        var a = Auditor(); var request = Request(sources[0]) with { Provider = "selected", Model = "selected-model", RequireExactRoute = true };
        Assert.Equal(ContinualAcceptanceOutcome.Delivered, (await a.Service.EvaluateAsync(request, new(Id, sources))).Outcome);
        Assert.All(a.Router.Calls, c => { Assert.True(c.Options.DisableFallback); Assert.Equal(LlmOverrideProvenance.Policy, c.Options.OverrideProvenance); Assert.Contains("must not retroactively invalidate", c.Prompt); Assert.Contains("CreatedUtc", c.Prompt); });
        var mismatched = request with { ObjectiveText = "Different authority" };
        Assert.Equal(ContinualAcceptanceOutcome.VerificationUnavailable, (await a.Service.EvaluateAsync(mismatched, new(Id, sources))).Outcome);
    }
    [Fact]
    public async Task New_explicit_goal_authority_can_supersede_an_older_inbox_source_in_its_exact_chronological_position()
    {
        var earlier = new GoalInstructionSource("old-message", 0, "Correction", "Use blue", Time);
        var current = new GoalInstructionSource("objective", 1, "Objective", "Use green", Time.AddMinutes(1));
        var a = Auditor(p =>
        {
            var answer = Delivered(p);
            if (Exact(p).Id != "objective")
            {
                if (p.Contains("Independent chronological source-bundle audit:"))
                { answer.SupersessionVerified = true; answer.LaterSourceBindings = [new(current.Id, current.Order, current.Sha256)]; }
                else { answer.Verdict = "retry"; answer.LaterSourceIds = [current.Id]; }
            }
            return answer;
        });
        Assert.Equal(ContinualAcceptanceOutcome.Delivered, (await a.Service.EvaluateAsync(Request(current), new(Id, [earlier, current]))).Outcome);
        Assert.Equal(3, a.Router.Calls.Count);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Full_independent_bundles_compose_disjoint_partial_corrections_and_chained_overrides(bool chain)
    {
        var sources = new[] { Source(0, "Use blue and red. Preserve all records."), Source(1, "Replace blue with green."),
            Source(2, chain ? "Replace green with black." : "Replace red with black.") };
        var a = Auditor(p =>
        {
            var x = Delivered(p); var source = Exact(p);
            if (p.Contains("Independent chronological source-bundle audit:"))
            {
                var later = Later(p); x.SupersessionVerified = true;
                x.LaterSourceBindings = later.Select(s => new GoalInstructionAuditBinding(s.Id, s.Order, s.Sha256)).ToArray();
                Assert.Contains("composing disjoint partial changes and chains", p);
                if (source.Id == "objective") Assert.Equal(2, later.Length);
            }
            else if (source.Order == 0 || chain && source.Order == 1)
            { x.Verdict = "retry"; x.LaterSourceIds = sources.Skip(source.Order + 1).Select(s => s.Id).ToArray(); }
            return x;
        });
        Assert.Equal(ContinualAcceptanceOutcome.Delivered, (await a.Service.EvaluateAsync(Request(sources[0]), new(Id, sources))).Outcome);
        Assert.Equal(chain ? 5 : 4, a.Router.Calls.Count);
    }
    [Theory]
    [InlineData("omitted")]
    [InlineData("wrong-hash")]
    [InlineData("oversized")]
    public async Task Incomplete_wrong_hash_or_oversized_bundle_cannot_drop_any_authority(string mode)
    {
        var sources = Enumerable.Range(0, mode == "oversized" ? 35 : 3)
            .Select(i => Source(i, "Exact source " + i + (mode == "oversized" ? new string('x', 8000) : ""))).ToArray();
        var a = Auditor(p =>
        {
            var x = Delivered(p);
            if (p.Contains("Independent chronological source-bundle audit:"))
            {
                var later = Later(p); x.SupersessionVerified = true;
                x.LaterSourceBindings = later.Select(s => new GoalInstructionAuditBinding(s.Id, s.Order, s.Sha256)).ToArray();
                if (mode == "omitted") x.LaterSourceBindings = x.LaterSourceBindings.Take(1).ToArray();
                if (mode == "wrong-hash") x.LaterSourceBindings = [x.LaterSourceBindings[0] with { Sha256 = "wrong" }, x.LaterSourceBindings[1]];
            }
            else { x.Verdict = "retry"; x.LaterSourceIds = sources.Skip(1).Select(s => s.Id).ToArray(); }
            return x;
        });
        var result = await a.Service.EvaluateAsync(Request(sources[0]), new(Id, sources));
        Assert.Equal(ContinualAcceptanceOutcome.VerificationUnavailable, result.Outcome); Assert.True(result.EvaluationUnavailable);
        if (mode == "oversized") Assert.Single(a.Router.Calls);
    }
    private static GoalMessageAttachment ReferenceFile() => new("fixture", "source.png", "/fixture/source.png", "image/png", 10, new string('a', 64));

    [Fact]
    public async Task Attachment_only_reference_remains_exact_context_but_does_not_create_an_instruction_or_override_authority()
    {
        var objective = Source(0, "Keep writing short natural replies.");
        var reference = Source(1, GoalMessaging.AttachmentOnlyText) with { Attachments = [ReferenceFile()] };
        var correction = Source(2, "Write a real pitch and caption.") with { Attachments = [ReferenceFile()] };
        var a = Auditor(); var snapshot = new GoalInstructionSnapshot(Id, [objective, reference, correction]);
        Assert.Equal(ContinualAcceptanceOutcome.Delivered, (await a.Service.EvaluateAsync(Request(objective), snapshot)).Outcome);
        Assert.Equal(2, a.Router.Calls.Count); Assert.DoesNotContain(a.Router.Calls, c => Exact(c.Prompt).Id == reference.Id);
        Assert.Contains(reference.Sha256, a.Router.Calls[0].Prompt);
        Assert.Contains(ReferenceFile().Sha256, a.Router.Calls[1].Prompt); // Explicit directive retains its exact attachments.
        Assert.Equal(reference, snapshot.Sources.Single(s => s.Id == reference.Id));
        Assert.NotEqual(snapshot.Revision, (snapshot with { Sources = [objective, reference with { Attachments = [ReferenceFile() with { Sha256 = new string('b', 64) }] }, correction] }).Revision);
        a = Auditor(p => { var verdict = Delivered(p); verdict.LaterSourceIds = [reference.Id]; return verdict; });
        Assert.Equal(ContinualAcceptanceOutcome.VerificationUnavailable, (await a.Service.EvaluateAsync(Request(objective), snapshot)).Outcome);
    }

    [Theory]
    [InlineData("no-files")]
    [InlineData("invalid-file")]
    [InlineData("padded-marker")]
    [InlineData("objective")]
    public async Task Only_nonobjective_exact_marker_with_valid_nonempty_references_is_reference_material(string change)
    {
        var source = Source(change == "objective" ? 0 : 1, change == "padded-marker" ? " " + GoalMessaging.AttachmentOnlyText : GoalMessaging.AttachmentOnlyText)
            with { Attachments = change == "no-files" ? null : [change == "invalid-file" ? ReferenceFile() with { Sha256 = "invalid" } : ReferenceFile()] };
        Assert.False(source.IsReferenceOnly);
        if (change == "invalid-file") return;
        var objective = change == "objective" ? source : Source(0);
        var snapshot = new GoalInstructionSnapshot(Id, change == "objective" ? [source] : [objective, source]);
        var a = Auditor(); Assert.Equal(ContinualAcceptanceOutcome.Delivered, (await a.Service.EvaluateAsync(Request(objective), snapshot)).Outcome);
        Assert.Equal(snapshot.Sources.Count, a.Router.Calls.Count);
    }

    [Fact]
    public void Existing_instruction_revision_identity_is_preserved_without_a_migration_or_rehash()
    {
        var snapshot = new GoalInstructionSnapshot(Id, [Source(0), Source(1, "Keep the existing voice correction.")]);
        var expected = ContinualDeliveryEvidence.OutputDigest(JsonSerializer.Serialize(new
        { GoalId = Id, Sources = snapshot.Sources.Select(s => new { s.Id, s.Order, s.Kind, s.Sha256, s.CreatedUtc }) }));
        Assert.Equal(expected, snapshot.Revision);
    }

    [Fact]
    public async Task Many_reference_messages_remain_in_full_history_without_filling_each_instruction_audit_or_waiving_explicit_work()
    {
        var objective = Source(0, "Continue the real task without interpreting references as new instructions.");
        var refs = Enumerable.Range(1, 500).Select(i => Source(i, GoalMessaging.AttachmentOnlyText) with
        { Attachments = [ReferenceFile() with { Id = "file-" + i, LocalPath = "/fixture/" + new string('f', 2000) + i + ".png" }] }).ToArray();
        var directive = Source(501, "Write the real pitch and caption.") with { Attachments = [ReferenceFile()] };
        var snapshot = new GoalInstructionSnapshot(Id, [objective, .. refs, directive]);
        var a = Auditor(); Assert.Equal(ContinualAcceptanceOutcome.Delivered, (await a.Service.EvaluateAsync(Request(objective), snapshot)).Outcome);
        Assert.Equal(2, a.Router.Calls.Count); Assert.Equal(502, snapshot.Sources.Count);
        Assert.All(a.Router.Calls, c => Assert.True(c.Prompt.Length < 30000));
        Assert.Contains(directive.Text, a.Router.Calls[1].Prompt); Assert.Contains(ReferenceFile().Sha256, a.Router.Calls[1].Prompt);
        a = Auditor(p => { var verdict = Delivered(p); if (Exact(p).Id == directive.Id) verdict.Verdict = "retry"; return verdict; });
        Assert.Equal(ContinualAcceptanceOutcome.Retry, (await a.Service.EvaluateAsync(Request(objective), snapshot)).Outcome);
    }

}
