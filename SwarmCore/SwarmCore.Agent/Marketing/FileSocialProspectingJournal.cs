using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SwarmCore.Agent.Marketing;

/// <summary>
/// Profile-owned atomic journal. One exclusive OS file lock per browser/account serializes all
/// assignments across desktop/worker processes. No credentials. Host chooses the profile directory.
/// </summary>
public sealed class FileSocialProspectingJournal : ISocialProspectingJournal, IAsyncDisposable
{
    private readonly FileStream _lease;
    private readonly string _path;
    private readonly Guid _assignmentId;
    private readonly SocialProspectingPlan _plan;
    private readonly TimeProvider _clock;
    private readonly State _state;
    private FileSocialProspectingJournal(FileStream lease, string path, Guid id, SocialProspectingPlan plan, State state, TimeProvider clock)
    { _lease = lease; _path = path; _assignmentId = id; _plan = plan; _state = state; _clock = clock; }
    public sealed class State
    {
        public Dictionary<Guid, SocialProspectingPlan> Assignments { get; set; } = [];
        public List<Entry> Entries { get; set; } = [];
        public List<Event> Events { get; set; } = [];
        public Dictionary<Guid, string> CrmDestinations { get; set; } = [];
        public Dictionary<Guid, string> BusinessDestinations { get; set; } = [];
    }
    public sealed class Entry
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid AssignmentId { get; set; }
        public SocialProspectEvidence Evidence { get; set; } = null!;
        public SocialFollowState State { get; set; }
        public DateTimeOffset? DispatchedAt { get; set; }
        public string Detail { get; set; } = "";
    }
    public sealed record Event(Guid EntryId, SocialFollowState State, string Detail, DateTimeOffset At);
    public static async Task<FileSocialProspectingJournal> OpenAsync(string profileDirectory, string connectionId,
        Guid assignmentId, SocialProspectingPlan plan, CancellationToken ct = default, TimeProvider? clock = null)
    {
        plan.Validate();
        if (!Guid.TryParseExact(connectionId, "D", out var connection) || connection == Guid.Empty || assignmentId == Guid.Empty)
            throw new ArgumentException("A saved browser connection and assignment id are required.");
        var directory = Path.Combine(Path.GetFullPath(profileDirectory), "marketing", "browser-jobs"); Directory.CreateDirectory(directory);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(connection.ToString("D") + "\n" + plan.AccountId.ToLowerInvariant())));
        var path = Path.Combine(directory, hash + ".json");
        // Failure to acquire means the existing objective is still running. Never delete another process's lock.
        var lease = new FileStream(Path.Combine(directory, hash + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            State state;
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidOperationException("Marketing journal needs archival; no actions were dispatched.");
                state = JsonSerializer.Deserialize<State>(await File.ReadAllTextAsync(path, ct))
                    ?? throw new InvalidOperationException("Marketing journal is invalid; restore it before acting.");
            }
            else state = new();
            if (state.Assignments.TryGetValue(assignmentId, out var prior)
                && JsonSerializer.Serialize(prior) != JsonSerializer.Serialize(plan))
                throw new InvalidOperationException("This assignment id already has different frozen instructions. Use a new assignment for a changed objective.");
            state.Assignments.TryAdd(assignmentId, plan);
            var journal = new FileSocialProspectingJournal(lease, path, assignmentId, plan, state, clock ?? TimeProvider.System);
            await journal.SaveAsync(ct); return journal;
        }
        catch { await lease.DisposeAsync(); throw; }
    }
    public IReadOnlyList<Entry> Entries => _state.Entries.Where(e => e.AssignmentId == _assignmentId).ToArray();
    public Guid AssignmentId => _assignmentId;
    public bool IsBoundToDestination(string destination) => _state.CrmDestinations.TryGetValue(_assignmentId,out var existing)&&existing==destination;
    public bool IsBoundToBusinessDestination(string destination)=>_state.BusinessDestinations.TryGetValue(_assignmentId,out var existing)&&existing==destination;
    public async Task<bool> BindBusinessDestinationAsync(string destination,CancellationToken ct)
    {
        if(_state.BusinessDestinations.TryGetValue(_assignmentId,out var existing))return existing==destination;
        _state.BusinessDestinations.Add(_assignmentId,destination);await SaveAsync(ct);return true;
    }
    public async Task RecordBusinessOutcomeAsync(Guid id,SocialFollowOutcome outcome,bool reserved,CancellationToken ct)
    {
        var entry=Entries.Single(e=>e.Id==id);
        if(reserved)entry.DispatchedAt??=_clock.GetUtcNow();
        await RecordOutcomeAsync(id,outcome,ct);
    }
    public async Task SaveCandidateAsync(SocialProspectEvidence evidence, CancellationToken ct)
    {
        if (_state.Entries.Any(e => e.Evidence.Person.Id.Equals(evidence.Person.Id, StringComparison.OrdinalIgnoreCase))) return;
        var entry = new Entry { AssignmentId = _assignmentId, Evidence = evidence, State = SocialFollowState.Candidate,
            Detail = "Observed public profile; exact bio phrases matched. No buying intent or messaging consent inferred." };
        _state.Entries.Add(entry); _state.Events.Add(new(entry.Id, entry.State, entry.Detail, _clock.GetUtcNow())); await SaveAsync(ct);
    }
    public Task<IReadOnlyList<SocialProspectWork>> ReadWorkAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<SocialProspectWork>>(Entries.Select(e => new SocialProspectWork(e.Id, e.Evidence.Person.Id, e.State)).ToArray());
    public async Task<bool> ReserveDispatchAsync(Guid id, CancellationToken ct)
    {
        var entry = Entries.Single(e => e.Id == id); var now = _clock.GetUtcNow();
        if (!_plan.FollowEnabled || entry.State != SocialFollowState.Candidate
            || entry.Evidence.ObservedAt < now.AddDays(-7)
            || Entries.Count(e => e.DispatchedAt is not null) >= _plan.MaxFollows
            || _state.Entries.Count(e => e.DispatchedAt?.UtcDateTime.Date == now.UtcDateTime.Date) >= 3) return false;
        entry.State = SocialFollowState.Dispatching; entry.DispatchedAt = now;
        entry.Detail = "Follow reserved before browser dispatch.";
        _state.Events.Add(new(id, entry.State, entry.Detail, now)); await SaveAsync(ct); return true;
    }
    public async Task RecordOutcomeAsync(Guid id, SocialFollowOutcome outcome, CancellationToken ct)
    {
        var entry = Entries.Single(e => e.Id == id);
        entry.State = outcome.State; entry.Detail = outcome.Detail;
        _state.Events.Add(new(id, outcome.State, outcome.Detail, _clock.GetUtcNow())); await SaveAsync(ct);
    }
    public Guid OperationIdFor(string targetId) => Entries.Single(e => e.Evidence.Person.Id == targetId).Id;
    public async Task<bool> BindCrmDestinationAsync(string destination, CancellationToken ct)
    {
        if (_state.CrmDestinations.TryGetValue(_assignmentId, out var existing)) return existing == destination;
        _state.CrmDestinations.Add(_assignmentId, destination);
        await SaveAsync(ct); return true;
    }
    private async Task SaveAsync(CancellationToken ct)
    {
        var temporary = _path + ".pending-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { await JsonSerializer.SerializeAsync(stream, _state, cancellationToken: ct); await stream.FlushAsync(ct); stream.Flush(flushToDisk: true); }
            File.Move(temporary, _path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public ValueTask DisposeAsync() => _lease.DisposeAsync();
}
