using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Review;

namespace SwarmCore.Coding.History;

public sealed class HistorySearchInput
{
    [Description("Words or a \"quoted phrase\" to look for. Every term must appear in a session's title, the person's requests, or (with include_responses) the replies.")]
    public required string Query { get; set; }

    [Description("Also search the assistant's replies, not only titles and the person's requests. Default false.")]
    public bool? IncludeResponses { get; set; }

    [Description("Limit to a period in words, such as \"past week\", \"last month\", \"yesterday\" or \"2026-10-01\". Default: all time.")]
    public string? Period { get; set; }

    [Description("Limit to one project: its repository path or folder name. Default: every project.")]
    public string? Project { get; set; }

    [Description("Most sessions to return (default 20, at most 100).")]
    public int? Take { get; set; }
}

/// <summary>One matching session, without its turns.</summary>
public sealed record HistorySearchHit(string SessionId, string Title, string Project, string ProjectName, DateTime CreatedUtc, DateTime LastActivityUtc,
    string? Lane, int TurnCount, string MatchedIn, string Preview, int MatchingTurns);

public sealed record HistorySearchOutput(string Query, string? PeriodLabel, string? Project, int Count, IReadOnlyList<HistorySearchHit> Results);

public sealed class HistorySummaryInput
{
    [Description("The period in the person's words: \"today\", \"yesterday\", \"this week\", \"past week\", \"last 10 days\", \"last month\", \"2026-10-01 2026-10-07\". Default: the past week.")]
    public string? Period { get; set; }

    [Description("Limit to one project: its repository path or folder name. Default: every project.")]
    public string? Project { get; set; }

    [Description("True returns the plain activity digest (each session's requests and final answer) instead of a written summary; use it to answer a specific question about past work.")]
    public bool? Digest { get; set; }
}

/// <summary>The <c>history.*</c> tools: read-only lookups over the person's saved sessions across repositories.</summary>
public static class ConversationHistoryTools
{
    public const string OwnerAgentId = "history";
    public const string SearchToolId = "history.search";
    public const string SummaryToolId = "history.summary";
    public static readonly IReadOnlyList<string> ToolIds = [SearchToolId, SummaryToolId];

    public static ToolDescriptor CreateSearch(IConversationHistorySource source)
        => ToolDescriptor.Create<HistorySearchInput, HistorySearchOutput>(SearchToolId, OwnerAgentId,
            "Search the person's saved Talavine Code sessions across every repository by title and by what they asked (and, with include_responses, by the replies). "
            + "Returns matching sessions newest first with an excerpt around the match. Use it to find an earlier conversation or what was asked about a topic.",
            ToolCostClass.Moderate, ToolSideEffect.ReadOnly,
            async (input, ct) =>
            {
                var now = DateTime.Now;
                SummaryPeriod? period = SummaryPeriod.TryParse(input.Period, now, out var parsed) ? parsed : null;
                var options = new HistorySearchOptions(input.Query ?? "")
                {
                    IncludeResponses = input.IncludeResponses == true,
                    SinceUtc = period is { } p && p.SinceUtc != DateTime.MinValue ? p.SinceUtc : null,
                    UntilUtc = period?.UntilUtc,
                    Project = input.Project,
                };
                var sessions = await source.LoadAsync(new HistoryScope(options.SinceUtc, options.UntilUtc, options.Project), ct).ConfigureAwait(false);
                var take = Math.Clamp(input.Take ?? 20, 1, 100);
                var hits = ConversationSearch.Search(sessions, options, take).Select(r => new HistorySearchHit(
                    r.Session.Id, r.Session.Title, r.Session.Project, r.Session.ProjectName, r.Session.CreatedUtc, r.Session.LastActivityUtc,
                    r.Session.Lane, r.Session.Turns.Count, r.Field.ToString().ToLowerInvariant(), r.Preview, r.MatchingTurns)).ToList();
                return new HistorySearchOutput(input.Query ?? "", period?.Label, input.Project, hits.Count, hits);
            },
            keywords: ["history", "search", "past sessions", "earlier conversation", "previous chat", "find session", "what did I ask"],
            formatResultForUser: output => output.Count == 0
                ? $"No saved sessions match \"{output.Query}\"."
                : $"{output.Count} session{(output.Count == 1 ? "" : "s")} match \"{output.Query}\": " + string.Join("; ", output.Results.Take(5).Select(h => $"{h.Title} ({h.ProjectName})")));

    public static ToolDescriptor CreateSummary(WorkSummaryService service, Func<string?> conversation)
        => ToolDescriptor.Create<HistorySummaryInput, WorkSummaryResult>(SummaryToolId, OwnerAgentId,
            "Summarize the work done in the person's saved Talavine Code sessions over a period (default: the past week), across every repository or in one project. "
            + "Returns a written report by project (sessions, what was asked, outcomes, open items), or with digest=true the plain facts to answer a specific question. "
            + "Use when the person asks what was done recently or for a report of their work.",
            ToolCostClass.LlmCall, ToolSideEffect.ReadOnly,
            (input, ct) =>
            {
                var period = SummaryPeriod.TryParse(input.Period, DateTime.Now, out var parsed) ? parsed : SummaryPeriod.PastDays(7, DateTime.Now);
                return service.SummarizeAsync(new WorkSummaryRequest(period, input.Project, ConversationId: conversation()) { DigestOnly = input.Digest == true }, ct);
            },
            keywords: ["history", "summary", "summarize", "what did we do", "recent work", "report", "this week", "last week", "past week"],
            formatResultForUser: result => $"Work summary for {result.PeriodLabel}: {result.SessionCount} session{(result.SessionCount == 1 ? "" : "s")}, {result.TurnCount} turn{(result.TurnCount == 1 ? "" : "s")}"
                + (result.Projects.Count > 0 ? $" across {string.Join(", ", result.Projects)}" : "") + (result.ModelWritten ? $" (written by {result.Lane})." : "."));
}

/// <summary>Publishes <c>history.search</c> and <c>history.summary</c> when the host supplies an <see cref="IConversationHistorySource"/> and the tools are enabled.</summary>
public sealed class ConversationHistoryToolRegistration(IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!services.GetRequiredService<IOptions<WorkSummaryOptions>>().Value.ToolsEnabled) return Task.CompletedTask;
        if (services.GetService<IConversationHistorySource>() is not { } source) return Task.CompletedTask;
        var catalog = services.GetRequiredService<ICapabilityCatalog>();
        var turns = services.GetService<ICurrentTurnContextAccessor>();
        catalog.Register(ConversationHistoryTools.CreateSearch(source));
        catalog.Register(ConversationHistoryTools.CreateSummary(services.GetRequiredService<WorkSummaryService>(), () => turns?.Current?.ConversationId));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class ConversationHistoryServiceCollectionExtensions
{
    /// <summary>
    /// Conversation history for a host: options (<c>Coding:History</c>), the router-backed completion (TryAdd, shared with the
    /// model panel), <see cref="WorkSummaryService"/>, and the <c>history.*</c> catalog registration. The host registers its
    /// <see cref="IConversationHistorySource"/> first and adds <see cref="WorkSummaryPrompts.Guidance"/> to its instructions.
    /// </summary>
    public static IServiceCollection AddSwarmCoreConversationHistory(this IServiceCollection services, Action<WorkSummaryOptions>? configure = null)
    {
        services.AddOptions<WorkSummaryOptions>();
        if (configure is not null) services.Configure(configure);
        services.TryAddSingleton<IModelPanelCompletion, RouterModelPanelCompletion>();
        services.TryAddSingleton<WorkSummaryService>();
        if (!services.Any(d => d.ImplementationType == typeof(ConversationHistoryToolRegistration)))
            services.AddHostedService<ConversationHistoryToolRegistration>();
        return services;
    }
}
