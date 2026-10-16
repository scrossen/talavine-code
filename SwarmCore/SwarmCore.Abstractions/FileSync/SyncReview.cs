namespace SwarmCore.Abstractions.FileSync;

/// <summary>A content fingerprint, including the causal history of a missing/deleted file.</summary>
public sealed record SyncReviewCopy
{
    public string RelativePath { get; init; } = string.Empty;
    public string? Sha256 { get; init; }
    public long SizeBytes { get; init; }
    public long ModifiedUtcMs { get; init; }
    public IReadOnlyDictionary<string, long> Version { get; init; } = new Dictionary<string, long>();
}

public sealed record SyncReviewDevice(string DeviceId, string DisplayName, IReadOnlyList<SyncReviewCopy> Files, string? Error = null)
{
    public bool IsAvailable => Error is null;
}

/// <summary>Metadata only. Hosts own authenticated transport and local comparison windows.</summary>
public sealed record SyncReview(string RootId, long Revision, IReadOnlyList<SyncReviewDevice> Devices)
{
    public bool IsComplete => Devices.Count > 0 && Devices.All(d => d.IsAvailable);

    public IReadOnlyList<string> Paths => Devices.Where(d => d.IsAvailable).SelectMany(d => d.Files).Select(f => f.RelativePath)
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    public SyncReviewCopy Copy(string deviceId, string path)
    {
        var device = Devices.Single(d => d.DeviceId == deviceId);
        // An unreadable device is unknown, never a missing/deleted copy.
        if (!device.IsAvailable) throw new InvalidOperationException($"{device.DisplayName} is unavailable. Refresh the comparison after updating or reconnecting it.");
        return device.Files.SingleOrDefault(f => f.RelativePath == path) ?? new SyncReviewCopy { RelativePath = path };
    }

    public bool Differs(string path) => !IsComplete || Devices.Select(d => Copy(d.DeviceId, path).Sha256)
        .Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any();

    public IReadOnlyDictionary<string, long> ResolutionVersion(string path, string resolver)
    {
        if (!IsComplete) throw new InvalidOperationException("Every selected device must be reviewed before applying choices.");
        return SyncVersion.Next(Devices.Aggregate(new Dictionary<string, long>(),
            (version, device) => SyncVersion.Merge(version, Copy(device.DeviceId, path).Version)), resolver);
    }

    public static bool SameSnapshot(SyncReviewCopy a, SyncReviewCopy b)
        => string.Equals(a.Sha256, b.Sha256, StringComparison.OrdinalIgnoreCase)
            && SyncVersion.Dominates(a.Version, b.Version) && SyncVersion.Dominates(b.Version, a.Version);
}
