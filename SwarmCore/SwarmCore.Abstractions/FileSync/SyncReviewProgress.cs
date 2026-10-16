namespace SwarmCore.Abstractions.FileSync;

/// <summary>Shared readiness policy for native review windows. Contains metadata only.</summary>
public sealed record SyncReviewProgress(bool CanApply, int RemainingChoices, string Guidance)
{
    public static SyncReviewProgress For(SyncReview review, IEnumerable<string> chosenPaths)
    {
        if (!review.IsComplete)
        {
            var unavailable = review.Devices.Where(d => !d.IsAvailable).ToArray();
            var names = string.Join(", ", unavailable.Select(d => d.DisplayName));
            return new(false, review.Paths.Count,
                $"Sync is waiting for {names}. Compare available copies now; choices and editing unlock after every device responds."
                + Environment.NewLine + "Open SwarmSpan on the unavailable devices, resolve the details below, then click ‘Refresh comparison’."
                + Environment.NewLine + string.Join(Environment.NewLine, unavailable.Select(d => $"{d.DisplayName}: {d.Error}")));
        }

        var chosen = chosenPaths.ToHashSet(StringComparer.Ordinal);
        var remaining = review.Paths.Count(path => review.Differs(path) && !chosen.Contains(path));
        return new(remaining == 0, remaining, remaining > 0
            ? $"All {review.Devices.Count} devices responded. {remaining} file(s) still need a starting-content choice. Choose a device copy for each, or compare and save an edited result. Then click ‘Apply choices and start sync’."
            : "All devices responded and every file has a starting-content choice. Click ‘Apply choices and start sync’ to verify the copies and enable automatic two-way sync.");
    }
}
