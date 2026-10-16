using SwarmCore.Coding.Review;
using SwarmCore.Coding.Routing;

namespace SwarmCode.Hosting;

/// <summary>What the /compare dialog starts: the prompt, two or more models, and the judge for a disagreement.</summary>
public sealed record ModelCompareStart(string Question, IReadOnlyList<ModelPanelPick> Models, ModelPanelPick Judge);

/// <summary>
/// The person's /compare choices kept in <see cref="AppPreferences"/>: recently compared models (pinned at the top of the
/// picker), the set checked last time, and the judge. Picks always carry the exact model, so a later change to a lane's
/// default model does not silently change what "recent" means.
/// </summary>
public static class ModelCompareChoices
{
    public const int MaxRecent = 8;

    /// <summary>The pick for a lane and model, with the model spelled out.</summary>
    public static ModelPanelPick Pick(CodingLane lane, string? model = null)
        => new(lane.Name, string.IsNullOrWhiteSpace(model) ? lane.Model : model.Trim());

    /// <summary>Same lane (case-insensitive) and same model (exact, model ids are case-sensitive).</summary>
    public static bool Same(ModelPanelPick a, ModelPanelPick b)
        => string.Equals(a.Lane, b.Lane, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Model ?? "", b.Model ?? "", StringComparison.Ordinal);

    /// <summary>Stored picks that still name a configured API lane, most recent first, without duplicates.</summary>
    public static IReadOnlyList<ModelPanelPick> Available(IEnumerable<ModelPanelPick>? stored, IReadOnlyList<CodingLane> lanes)
    {
        var result = new List<ModelPanelPick>();
        foreach (var pick in stored ?? [])
        {
            if (pick is null || string.IsNullOrWhiteSpace(pick.Lane)) continue;
            var lane = lanes.FirstOrDefault(l => !l.IsNative && string.Equals(l.Name, pick.Lane, StringComparison.OrdinalIgnoreCase));
            if (lane is null) continue;
            var normalized = Pick(lane, pick.Model);
            if (!result.Any(r => Same(r, normalized))) result.Add(normalized);
        }
        return result;
    }

    /// <summary>Records a run: its models and judge move to the front of the recent list, the selection and judge are kept for next time.</summary>
    public static void Remember(AppPreferences preferences, IReadOnlyList<ModelPanelPick> models, ModelPanelPick judge)
    {
        var recent = models.Append(judge).Concat(preferences.RecentCompareModels ?? [])
            .Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Lane))
            .Aggregate(new List<ModelPanelPick>(), (list, pick) => { if (!list.Any(p => Same(p, pick))) list.Add(pick); return list; });
        preferences.RecentCompareModels = recent.Take(MaxRecent).ToList();
        preferences.LastCompareModels = models.ToList();
        preferences.CompareJudge = judge;
    }

    /// <summary>The request for the service: a pick of a lane's own model is sent as the lane alone, so reports show the lane name.</summary>
    public static ModelPanelPick ForRequest(ModelPanelPick pick, IReadOnlyList<CodingLane> lanes)
        => lanes.FirstOrDefault(l => string.Equals(l.Name, pick.Lane, StringComparison.OrdinalIgnoreCase)) is { } lane
           && string.Equals(lane.Model, pick.Model, StringComparison.Ordinal)
            ? new ModelPanelPick(lane.Name)
            : pick;
}
