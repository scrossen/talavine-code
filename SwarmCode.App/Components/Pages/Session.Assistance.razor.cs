using SwarmCore.Abstractions.Collaboration;
using SwarmCore.Coding.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace SwarmCode.App.Components.Pages;

public partial class Session
{
    private long _assistanceEditRevision;
    private string? _assistanceEditSession;
    private void BeginAssistanceEdit(long revision) { _assistanceEditRevision = revision; _assistanceEditSession = SessionId; }
    private readonly Dictionary<string, IReadOnlyList<string>> _assistanceModels = new(StringComparer.Ordinal);
    private IReadOnlyList<AssistanceModelChoice> AssistanceChoices => Lanes.SelectMany(l =>
        (_assistanceModels.GetValueOrDefault(l.Name) ?? [l.Model]).Select(m =>
            new AssistanceModelChoice(l.Name + "|" + m, l.Provider, m, l.Name)))
        .Concat(_session?.Assistance.OpinionModels ?? [])
        .Concat(_session?.Assistance.Reviewer is { } reviewer ? [reviewer] : [])
        .DistinctBy(c => c.Key).ToArray();

    private async Task LoadAssistanceChoicesAsync()
    {
        if (App.Workspace is not { } host) return;
        var sessionId = SessionId;
        var lanes = Lanes.ToArray();
        var results = await Task.WhenAll(lanes.Select(async lane =>
        {
            try
            {
                using var scope = host.Services.CreateScope();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var catalog = await Task.Run(() => scope.ServiceProvider.GetRequiredService<CodingModelCatalog>().GetCatalogAsync(lane, timeout.Token));
                return (lane.Name, catalog.Models);
            }
            catch (Exception) { return (lane.Name, (IReadOnlyList<string>)[lane.Model]); }
        }));
        if (_disposed || SessionId != sessionId) return;
        foreach (var (name, models) in results) _assistanceModels[name] = models;
    }

    private async Task SaveAssistanceAsync(TurnAssistanceOptions options)
    {
        if (_runner.IsRunning || _session is null || SessionId != _assistanceEditSession) throw new InvalidOperationException("Wait for the current request to finish before changing these options.");
        var id = SessionId;
        await (App.Sessions ?? throw new InvalidOperationException("No conversation is selected.")).SetAssistanceAsync(id, options, _assistanceEditRevision);
        if (SessionId == id) _session = await App.Sessions.GetAsync(id) ?? _session;
    }
}
