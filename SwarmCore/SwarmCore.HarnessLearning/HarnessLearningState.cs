using System.Text.Json;

namespace SwarmCore.HarnessLearning;

public sealed record HarnessLearningState
{
    public string EnvironmentHash { get; init; } = "";
    public string Model { get; init; } = "";
    public string? CandidateStrategy { get; init; }
    public string Stage { get; init; } = "Waiting for an available model";
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset NextRunAt { get; init; }
    public string ActiveStrategy { get; init; } = "plain";
    public string? PreviousStrategy { get; init; }
    public string[] TriedStrategies { get; init; } = [];
    public Guid? ReportId { get; init; }
    public Guid? RunId { get; init; }
    public int CompletedTrials { get; init; }
    public int TotalTrials { get; init; }
    public long ReportedTokens { get; init; }
    public int UnknownUsageCalls { get; init; }
    public decimal RecordedCost { get; init; }
    public int UnpricedCalls { get; init; }
    public long ReservedTokens { get; init; }
    public bool PendingCall { get; init; }
    public int ConsecutiveFailures { get; init; }
    public bool SearchComplete { get; init; }
    public string? Outcome { get; init; }
    public string? FailureCode { get; init; }
}

/// <summary>One private host/profile/tenant root. Writers hold a cross-process lease; reads are atomic snapshots.
/// The control file owns only Pause. State never overwrites it. No customer inputs or model answers are saved.</summary>
public sealed class FileHarnessLearningState
{
    public string Root { get; }
    private readonly string _scopeId;
    public FileHarnessLearningState(string root)
    {
        Root = Path.GetFullPath(root);
        FileHarnessExperimentHistory.EnsurePhysicalDirectory(Root);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(Root);
        else Directory.CreateDirectory(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var marker = Path.Combine(Root, "scope-id");
        if (File.Exists(marker) && (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Learning scope cannot follow links.");
        if (!File.Exists(marker))
        {
            var pending = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".scope.tmp");
            try
            {
                File.WriteAllText(pending, Guid.NewGuid().ToString("N"));
                try { File.Move(pending, marker); }
                catch (IOException) when (File.Exists(marker)) { }
            }
            finally { if (File.Exists(pending)) File.Delete(pending); }
        }
        _scopeId = File.ReadAllText(marker);
        if (!Guid.TryParseExact(_scopeId, "N", out _)) throw new InvalidDataException("Learning scope marker is invalid.");
    }
    public void EnsureCurrentScope()
    {
        FileHarnessExperimentHistory.EnsurePhysicalDirectory(Root);
        if (!Directory.Exists(Root)) throw new DirectoryNotFoundException("Learning scope was removed.");
        var marker = Path.Combine(Root, "scope-id");
        if (!File.Exists(marker) || (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0
            || new FileInfo(marker).Length > 64 || File.ReadAllText(marker) != _scopeId)
            throw new InvalidOperationException("Learning scope was replaced.");
    }
    private string PathFor(string name)
    {
        EnsureCurrentScope();
        var path = Path.Combine(Root, name);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Learning state cannot follow links.");
        return path;
    }
    public FileStream? TryLease()
    {
        var path = PathFor("cycle.lease");
        try { return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
    }
    public async Task<HarnessLearningState> ReadAsync(CancellationToken ct = default)
    {
        var path = PathFor("state.json");
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > 64_000) throw new InvalidDataException("Learning state is too large.");
        return JsonSerializer.Deserialize<HarnessLearningState>(await File.ReadAllBytesAsync(path, ct))
            ?? throw new InvalidDataException("Learning state is invalid.");
    }
    public async Task<bool> IsPausedAsync(CancellationToken ct = default)
    {
        var path = PathFor("paused.json");
        return !File.Exists(path) || JsonSerializer.Deserialize<bool>(await File.ReadAllBytesAsync(path, ct));
    }
    public Task SetPausedAsync(bool paused, CancellationToken ct = default) => WriteAsync("paused.json", paused, ct);
    internal Task SaveAsync(HarnessLearningState state, CancellationToken ct = default)
        => WriteAsync("state.json", state with { UpdatedAt = DateTimeOffset.UtcNow }, ct);
    private async Task WriteAsync<T>(string name, T value, CancellationToken ct)
    {
        var path = PathFor(name);
        var temporary = PathFor(Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(value), ct);
                stream.Flush(true);
            }
            PathFor(name); // Recheck scope and links after the await; never recreate a removed scope.
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async Task<bool> RollbackAsync(CancellationToken ct = default)
    {
        using var lease = TryLease();
        if (lease is null) return false;
        var state = await ReadAsync(ct);
        if (state.PreviousStrategy is null) return false;
        await SaveAsync(state with { ActiveStrategy = state.PreviousStrategy, PreviousStrategy = null,
            Stage = "Rolled back", Outcome = "Previous verified strategy restored." }, ct);
        return true;
    }
    public async Task ReconcileInterruptedAsync(CancellationToken ct = default)
    {
        using var lease = TryLease();
        if (lease is null) return;
        var state = await ReadAsync(ct);
        if (state.Stage is not ("Proposing" or "Evaluating")) return;
        await SaveAsync(state with { Stage = "Interrupted", PendingCall = false,
            UnknownUsageCalls = state.UnknownUsageCalls + (state.PendingCall ? 1 : 0),
            UnpricedCalls = state.UnpricedCalls + (state.PendingCall ? 1 : 0),
            Outcome = "The previous host stopped during a cycle. Its spend reservation is retained; uncertain calls are not retried." }, ct);
    }
    public async Task RecordWaitingAsync(string diagnostic, CancellationToken ct = default)
    {
        using var lease = TryLease();
        if (lease is null) return;
        var state = await ReadAsync(ct);
        if (state.NextRunAt > DateTimeOffset.UtcNow) return;
        await SaveAsync(state with { Stage = "Waiting for a model", FailureCode = diagnostic,
            NextRunAt = DateTimeOffset.UtcNow.AddMinutes(5),
            Outcome = "No ready chat route is available for learning. Check model settings; the host will retry automatically." }, ct);
    }
}
