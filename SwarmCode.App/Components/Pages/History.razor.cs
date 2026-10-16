using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using SwarmCode.App.Services;
using SwarmCore.Coding.History;

namespace SwarmCode.App.Components.Pages;

/// <summary>
/// Every saved session across repositories: plain-text search (titles and the person's requests, replies on request),
/// grouped by recency or by project, and a summary of the work done in a period. Searching is in-memory over
/// documents the history source reads from disk; the model-written summary needs the open workspace's lanes.
/// </summary>
public partial class History
{
    private const string CustomPeriod = "custom";

    private sealed record PeriodPreset(string Value, string Label);
    private sealed record ProjectOption(string Root, string Name);
    private sealed record Group(string Label, List<HistorySearchResult> Items);
    private sealed record Segment(string Text, bool Hit);

    private static readonly PeriodPreset[] PeriodPresets =
    [
        new("today", "Today"),
        new("yesterday", "Yesterday"),
        new("this week", "This week"),
        new("past week", "Past 7 days"),
        new("last week", "Last week"),
        new("this month", "This month"),
        new("past month", "Past 30 days"),
        new("last month", "Last month"),
        new("all time", "All time"),
        new(CustomPeriod, "Custom…"),
    ];

    [SupplyParameterFromQuery(Name = "q")] public string? QueryParameter { get; set; }
    [SupplyParameterFromQuery(Name = "summary")] public string? SummaryParameter { get; set; }

    private IReadOnlyList<HistorySessionDocument> _documents = [];
    private IReadOnlyList<HistorySearchResult> _results = [];
    private string _query = "";
    private bool _includeResponses;
    private string _project = "";
    private bool _byProject;
    private bool _loading = true;
    private string? _error;
    private int _refreshState; // 0 idle, 1 running, 2 running + rerun requested
    private CancellationTokenSource? _debounce;
    private string? _appliedQueryParameter, _appliedSummaryParameter;

    private string _periodChoice = "past week";
    private string _periodText = "";
    private bool _summarizing;
    private string? _summaryError;
    private WorkSummaryResult? _summary;
    private bool _disposed;

    private IReadOnlyList<ProjectOption> Projects => _documents
        .GroupBy(d => d.Project, StringComparer.OrdinalIgnoreCase)
        .Select(g => new ProjectOption(g.Key, g.First().ProjectName))
        .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    protected override async Task OnInitializedAsync()
    {
        State.Changed += OnStateChanged;
        await RefreshAsync();
    }

    /// <summary>Query-string changes while the page is open (a /history or /summary slash command) apply once each.</summary>
    protected override async Task OnParametersSetAsync()
    {
        if (!string.Equals(_appliedQueryParameter, QueryParameter, StringComparison.Ordinal))
        {
            _appliedQueryParameter = QueryParameter;
            if (!string.IsNullOrWhiteSpace(QueryParameter))
            {
                _query = QueryParameter.Trim();
                ApplySearch();
            }
        }
        if (!string.Equals(_appliedSummaryParameter, SummaryParameter, StringComparison.Ordinal))
        {
            _appliedSummaryParameter = SummaryParameter;
            if (!string.IsNullOrWhiteSpace(SummaryParameter))
            {
                var text = SummaryParameter.Trim();
                var preset = PeriodPresets.FirstOrDefault(p => p.Value != CustomPeriod && string.Equals(p.Value, text, StringComparison.OrdinalIgnoreCase));
                _periodChoice = preset?.Value ?? CustomPeriod;
                _periodText = preset is null ? text : "";
                await SummarizeAsync();
            }
        }
    }

    // Sessions change on background threads and often while a turn streams; refreshes are coalesced.
    private void OnStateChanged() => _ = InvokeAsync(RefreshAsync);

    private async Task RefreshAsync()
    {
        if (_disposed) return;
        if (Interlocked.CompareExchange(ref _refreshState, 1, 0) != 0)
        {
            Interlocked.Exchange(ref _refreshState, 2);
            return;
        }
        try
        {
            do
            {
                Interlocked.Exchange(ref _refreshState, 1);
                _loading = true;
                StateHasChanged();
                try
                {
                    _documents = await Task.Run(() => State.History.LoadAsync(HistoryScope.Everything));
                    _error = null;
                }
                catch (Exception ex)
                {
                    _error = "Could not read the saved sessions. " + ex.Message;
                    MauiProgram.LogToFile($"History refresh failed: {ex}");
                }
                _loading = false;
                ApplySearch();
                if (State.AnyTurnRunning) await Task.Delay(1000);
            }
            while (Interlocked.CompareExchange(ref _refreshState, 0, 1) == 2);
        }
        catch (Exception ex)
        {
            MauiProgram.LogToFile($"History refresh failed: {ex.Message}");
            Interlocked.Exchange(ref _refreshState, 0);
        }
    }

    /// <summary>Filters the loaded documents; cheap enough to run on every settled keystroke.</summary>
    private void ApplySearch()
    {
        var options = new HistorySearchOptions(_query) { IncludeResponses = _includeResponses, Project = _project.Length == 0 ? null : _project };
        _results = ConversationSearch.Search(_documents, options, take: 0);
        StateHasChanged();
    }

    private void OnQueryInput(ChangeEventArgs e)
    {
        _query = e.Value?.ToString() ?? "";
        _debounce?.Cancel();
        var cts = _debounce = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(200, cts.Token); } catch (OperationCanceledException) { return; }
            if (_disposed) return;
            try { await InvokeAsync(ApplySearch); } catch (ObjectDisposedException) { }
        });
    }

    private void OnIncludeResponsesChanged(ChangeEventArgs e)
    {
        _includeResponses = e.Value is true || string.Equals(e.Value?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
        ApplySearch();
    }

    private void OnProjectChanged(ChangeEventArgs e)
    {
        _project = e.Value?.ToString() ?? "";
        ApplySearch();
    }

    private void SetGrouping(bool byProject) => _byProject = byProject;

    private IReadOnlyList<Group> Groups
    {
        get
        {
            if (_byProject)
            {
                return _results
                    .GroupBy(r => r.Session.Project, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(g => g.Max(r => r.Session.LastActivityUtc))
                    .Select(g => new Group(g.First().Session.ProjectName, g.OrderByDescending(r => r.Session.LastActivityUtc).ToList()))
                    .ToList();
            }
            var today = DateTime.Now;
            var buckets = HistoryRecency.Order.ToDictionary(label => label, label => new Group(label, []), StringComparer.Ordinal);
            foreach (var result in _results.OrderByDescending(r => r.Session.LastActivityUtc))
                buckets[HistoryRecency.Bucket(result.Session.LastActivityUtc.ToLocalTime(), today)].Items.Add(result);
            return HistoryRecency.Order.Select(label => buckets[label]).Where(g => g.Items.Count > 0).ToList();
        }
    }

    private static string FieldLabel(HistoryMatchField field) => field switch
    {
        HistoryMatchField.Title => "the title",
        HistoryMatchField.Prompt => "a request",
        HistoryMatchField.Response => "a reply",
        _ => "",
    };

    /// <summary>Splits a preview into plain and matching segments so matches can be marked without raw HTML.</summary>
    private IReadOnlyList<Segment> Highlight(string preview)
    {
        var terms = ConversationSearch.Terms(_query);
        if (terms.Count == 0 || preview.Length == 0) return [new Segment(preview, false)];
        var segments = new List<Segment>();
        var i = 0;
        while (i < preview.Length)
        {
            var bestAt = -1; var bestLength = 0;
            foreach (var term in terms)
            {
                var at = preview.IndexOf(term, i, StringComparison.OrdinalIgnoreCase);
                if (at >= 0 && (bestAt < 0 || at < bestAt || (at == bestAt && term.Length > bestLength))) { bestAt = at; bestLength = term.Length; }
            }
            if (bestAt < 0) { segments.Add(new Segment(preview[i..], false)); break; }
            if (bestAt > i) segments.Add(new Segment(preview[i..bestAt], false));
            segments.Add(new Segment(preview.Substring(bestAt, bestLength), true));
            i = bestAt + bestLength;
        }
        return segments;
    }

    // ---- summary -------------------------------------------------------------

    private void OnPeriodChoiceChanged(ChangeEventArgs e)
    {
        _periodChoice = e.Value?.ToString() ?? "past week";
        if (_periodChoice != CustomPeriod) _periodText = "";
    }

    private async Task OnPeriodKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter") await SummarizeAsync();
    }

    private SummaryPeriod? CurrentPeriod()
    {
        var text = _periodChoice == CustomPeriod ? _periodText : _periodChoice;
        return SummaryPeriod.TryParse(text, DateTime.Now, out var period) ? period : null;
    }

    private async Task SummarizeAsync()
    {
        if (_summarizing) return;
        var period = CurrentPeriod();
        if (period is null)
        {
            _summaryError = "Say the period in words, such as \"past week\", \"last month\", \"yesterday\", \"past 10 days\" or a date like 2026-10-01.";
            return;
        }
        _summaryError = null;
        _summarizing = true;
        StateHasChanged();
        var project = _project.Length == 0 ? null : _project;
        var lane = State.Preferences.Current.DefaultLane;
        var service = State.Workspace?.Services.GetService<WorkSummaryService>();
        try
        {
            _summary = await Task.Run(async () =>
            {
                if (service is not null)
                    return await service.SummarizeAsync(new WorkSummaryRequest(period, project, lane));
                // No open repository: no lanes, so the digest stands in for the written summary.
                var sessions = await State.History.LoadAsync(period.ToScope(project));
                var digest = WorkSummaryDigest.Build(sessions, period);
                return new WorkSummaryResult(true,
                    digest.IsEmpty ? null : "Open a repository to get a written summary from one of its models; this is the activity digest.",
                    digest.IsEmpty ? $"No sessions had activity {period.Label}{(project is null ? "" : $" in {HistoryProjects.Name(project)}")}." : digest.Text,
                    period.Label, period.SinceUtc, period.UntilUtc, digest.SessionCount, digest.TurnCount, digest.Projects, null, false);
            });
        }
        catch (Exception ex)
        {
            _summaryError = "The summary could not be produced. " + ex.Message;
            MauiProgram.LogToFile($"History summary failed: {ex}");
        }
        finally
        {
            _summarizing = false;
            StateHasChanged();
        }
    }

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private async Task NewSessionAsync()
    {
        var session = await State.NewSessionAsync();
        Nav.NavigateTo(AppNavigation.SessionUrl(session.Id));
    }

    public void Dispose()
    {
        _disposed = true;
        State.Changed -= OnStateChanged;
        _debounce?.Cancel();
    }
}
