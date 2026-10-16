using System.Collections.Concurrent;
using System.Text.Json;
using SwarmCore.Coding.Planning;

namespace SwarmCode.Hosting;

/// <summary>
/// The session's task list (<c>plan.update</c>, mirrored native todo lists) at <c>sessions/plans/&lt;id&gt;.json</c>,
/// rewritten atomically on every change. Ids follow <see cref="StorageIds"/>; a file that cannot be read loads as an
/// empty list (the list is a convenience, never authority) and is replaced by the next write.
/// </summary>
public sealed class FileConversationPlanStore(SwarmCodeDataPaths paths) : IConversationPlanStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, ConversationPlan> _cache = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public event Action<string, ConversationPlan>? Changed;

    public string PathFor(string conversationId)
        => Path.Combine(paths.SessionsDirectory, "plans", StorageIds.Validate(conversationId) + ".json");

    public ValueTask<ConversationPlan> GetAsync(string conversationId, CancellationToken ct = default)
    {
        var path = PathFor(conversationId);
        if (_cache.TryGetValue(conversationId, out var cached)) return ValueTask.FromResult(cached);
        lock (_gate)
        {
            if (_cache.TryGetValue(conversationId, out cached)) return ValueTask.FromResult(cached);
            var plan = Load(path);
            _cache[conversationId] = plan;
            return ValueTask.FromResult(plan);
        }
    }

    public ValueTask<ConversationPlan> SetAsync(string conversationId, IReadOnlyList<PlanItem> items, CancellationToken ct = default)
    {
        var path = PathFor(conversationId);
        var (normalized, _) = ConversationPlanRules.Normalize(items.Select(i => ((string?)i.Content, (string?)ConversationPlanRules.Format(i.Status))), strict: false);
        var plan = new ConversationPlan(normalized ?? [], DateTime.UtcNow);
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, plan, Json);
                    stream.Flush(flushToDisk: true);
                }
                SwarmCore.Abstractions.Storage.TransientFileLock.Retry(
                    () => File.Move(temp, path, overwrite: true), accessDeniedIsTransient: true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            _cache[conversationId] = plan;
        }
        Changed?.Invoke(conversationId, plan);
        return ValueTask.FromResult(plan);
    }

    public ValueTask DeleteAsync(string conversationId, CancellationToken ct = default)
    {
        var path = PathFor(conversationId);
        bool existed;
        lock (_gate)
        {
            existed = _cache.TryRemove(conversationId, out var removed) && removed.Total > 0 || File.Exists(path);
            File.Delete(path);
        }
        if (existed) Changed?.Invoke(conversationId, ConversationPlan.Empty);
        return ValueTask.CompletedTask;
    }

    private static ConversationPlan Load(string path)
    {
        if (!File.Exists(path)) return ConversationPlan.Empty;
        try
        {
            var saved = JsonSerializer.Deserialize<ConversationPlan>(File.ReadAllText(path), Json);
            if (saved?.Items is null) return ConversationPlan.Empty;
            var (items, _) = ConversationPlanRules.Normalize(saved.Items.Where(i => i is not null)
                .Select(i => ((string?)i.Content, (string?)ConversationPlanRules.Format(i.Status))), strict: false);
            return new ConversationPlan(items ?? [], saved.UpdatedUtc);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return ConversationPlan.Empty;
        }
    }
}
