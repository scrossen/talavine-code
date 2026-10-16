// Shared personal-memory recall/benefit eval kit. Linked (not referenced) into each host test project
// so every app runs the SAME scenarios against its OWN real wiring (store, capture, dreaming, recall,
// tools, controls). Deliberately free of any xunit dependency: the suite returns scored results and
// the host test decides what to assert. See README.md beside this file.
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using SwarmCore.PersonalMemory;

namespace SwarmCore.Testing.PersonalMemoryEvals;

/// <summary>One memory the scripted model "extracts" from a user message.</summary>
internal sealed record EvalMemory(string Key, string Category, string Text, string? EventAt = null);

/// <summary>Outcome of a host's remember tool call.</summary>
internal sealed record EvalRememberOutcome(bool Ok, string? Message);

/// <summary>
/// A host's real personal-memory wiring, driven by the shared suite. Everything goes through the
/// host's own production path: its store, its post-turn capture, its dreaming entry point, the exact
/// memory block it puts into the model prompt, its remember tool and its control-page operations.
/// "Conversation" is the host's chat/session; temporary (incognito) is a conversation property.
/// </summary>
internal interface IMemoryEvalHost : IAsyncDisposable
{
    string App { get; }
    MemoryScope Scope { get; }
    PersonalMemoryOptions Options { get; }
    MemoryEvalModel Model { get; }
    DateTimeOffset Now { get; }
    void Advance(TimeSpan by);

    /// <summary>Start a new chat/session; subsequent turns belong to it.</summary>
    Task BeginConversationAsync(bool temporary = false);

    /// <summary>A completed user turn followed by the host's post-turn capture (extraction via <see cref="Model"/>).</summary>
    Task TurnAsync(string userMessage);

    /// <summary>The model calls the host's remember tool during a turn whose user message is <paramref name="userMessage"/>.</summary>
    Task<EvalRememberOutcome> RememberAsync(string userMessage, string text, MemoryCategory category);

    /// <summary>Content arriving through a host ingestion channel (email, ticket, document, tool result).</summary>
    Task<MemoryCaptureResult> ImportAsync(MemoryCaptureRequest request);

    /// <summary>
    /// Host-specific poisoning channels (legacy rows, page snapshots, repo files...). For each attempt the
    /// host calls <paramref name="registerAttack"/> with the channel name and embeds the returned canary.
    /// </summary>
    Task PlantHostSpecificPoisonAsync(Func<string, string> registerAttack);

    /// <summary>The host's real background consolidation ("dreaming") entry point, drained.</summary>
    Task DreamAsync();

    /// <summary>The exact memory text the host would put into the model prompt for this turn (null = none).</summary>
    Task<string?> RecallAsync(string userMessage);

    /// <summary>The memory search tool's ranking for the current turn.</summary>
    Task<IReadOnlyList<MemorySearchHit>> SearchAsync(string query, int limit);

    Task<MemoryInventory> InventoryAsync();
    Task<IReadOnlyList<MemoryClaim>> ClaimsAsync();
    Task<IReadOnlyList<MemoryFact>> FactsAsync(bool currentOnly);

    /// <summary>A direct store write (simulates a corrupted, synced or legacy row that bypassed admission).</summary>
    Task<MemoryCommitResult> CommitRawAsync(MemoryChangeSet changes);

    /// <summary>Control page: forget (host path, including any host-side cascade).</summary>
    Task<MemoryEditResult> ForgetAsync(Guid factId, long expectedVersion);

    /// <summary>Control page: "don't bring this up".</summary>
    Task<MemoryEditResult> SuppressAsync(Guid factId, long expectedVersion);

    /// <summary>Isolation boundaries this host enforces (other tenant, other user, other profile/home, agent partition...).</summary>
    IReadOnlyList<string> IsolationBoundaries { get; }

    Task<IMemoryEvalPeer> OpenPeerAsync(string boundary);
}

/// <summary>Another memory domain across an isolation boundary, sharing the primary host's clock and model.</summary>
internal interface IMemoryEvalPeer : IAsyncDisposable
{
    string Boundary { get; }
    Task<EvalRememberOutcome> RememberAsync(string userMessage, string text, MemoryCategory category);
    Task<string?> RecallAsync(string userMessage);
    Task<IReadOnlyList<string>> StoredTextsAsync();
}

/// <summary>
/// Deterministic stand-in for the LLM behind extraction, consolidation ("dreaming") and conversation
/// summaries. It reads the real production prompts, so prompt-format drift breaks the evals loudly.
/// <list type="bullet">
/// <item>Extraction: returns the memories taught for the person's message (exact or contained).</item>
/// <item>Consolidation: SUPERSEDE/EXPIRE for registered contradiction rules; due plans are rewritten
/// into the past tense ("is going to" -> "went to") or expired; everything else is left to the
/// deterministic fallback.</item>
/// </list>
/// </summary>
internal sealed class MemoryEvalModel
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, EvalMemory[]> _taught = new(StringComparer.Ordinal);
    private readonly List<(string FactMarker, string ClaimMarker, bool Expire)> _rules = [];
    private int _calls;
    private int _extractionCalls;
    private int _consolidationCalls;
    private int _summaryCalls;

    public int Calls => Volatile.Read(ref _calls);
    public int ExtractionCalls => Volatile.Read(ref _extractionCalls);
    public int ConsolidationCalls => Volatile.Read(ref _consolidationCalls);
    public int SummaryCalls => Volatile.Read(ref _summaryCalls);

    /// <summary>When false the consolidation answer is always an empty operation list (deterministic fallback only).</summary>
    public bool ProposeOperations { get; set; } = true;

    public string SummaryText { get; set; } = "Discussed design projects and upcoming plans.";

    public void Teach(string userMessage, params EvalMemory[] memories) => _taught[userMessage] = memories;

    /// <summary>A claim containing <paramref name="claimMarker"/> replaces a fact containing <paramref name="factMarker"/>.</summary>
    public void SupersedeWhen(string factMarker, string claimMarker)
    {
        lock (_rules) _rules.Add((factMarker, claimMarker, false));
    }

    /// <summary>A claim containing <paramref name="claimMarker"/> retires a fact containing <paramref name="factMarker"/>.</summary>
    public void ExpireWhen(string factMarker, string claimMarker)
    {
        lock (_rules) _rules.Add((factMarker, claimMarker, true));
    }

    public string Respond(string prompt)
    {
        Interlocked.Increment(ref _calls);
        if (prompt.Contains("You extract durable memories", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _extractionCalls);
            return Extraction(prompt);
        }
        if (prompt.Contains("You consolidate a personal memory store", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _consolidationCalls);
            return ProposeOperations ? Consolidation(prompt) : """{"operations":[]}""";
        }
        if (prompt.Contains("Summarize", StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref _summaryCalls);
            return SummaryText;
        }
        return "{}";
    }

    private string Extraction(string prompt)
    {
        var message = LineAfter(prompt, "The person's message");
        string? said = null;
        if (message is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(message);
                said = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() : null;
            }
            catch (JsonException) { said = null; }
        }
        if (string.IsNullOrEmpty(said)) return """{"memories":[]}""";

        var found = new List<EvalMemory>();
        if (_taught.TryGetValue(said, out var exact)) found.AddRange(exact);
        else
        {
            foreach (var (taught, memories) in _taught.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                if (said.Contains(taught, StringComparison.Ordinal)) found.AddRange(memories);
        }
        return JsonSerializer.Serialize(new
        {
            memories = found.Take(5).Select(m => new { key = m.Key, category = m.Category, text = m.Text, confidence = 0.9, eventAt = m.EventAt, validUntil = (string?)null }),
        }, Json);
    }

    private sealed record PromptFact(string Id, string? Text, bool Due);
    private sealed record PromptClaim(string Id, string? Text, string? Key, string? Category);

    private string Consolidation(string prompt)
    {
        var facts = Parse(LineAfter(prompt, "CURRENT FACTS:"), e => new PromptFact(
            e.GetProperty("id").GetString()!,
            e.TryGetProperty("text", out var t) ? t.GetString() : null,
            e.TryGetProperty("due", out var d) && d.ValueKind == JsonValueKind.True));
        var claims = Parse(LineAfter(prompt, "NEW CLAIMS:"), e => new PromptClaim(
            e.GetProperty("id").GetString()!,
            e.TryGetProperty("text", out var t) ? t.GetString() : null,
            e.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null,
            e.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null));

        var operations = new List<object>();
        var usedFacts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fact in facts.Where(f => f.Due && f.Text is not null))
        {
            usedFacts.Add(fact.Id);
            var text = fact.Text!;
            var past = text.Contains(" is going to ", StringComparison.Ordinal)
                ? text.Replace(" is going to ", " went to ", StringComparison.Ordinal)
                : null;
            operations.Add(past is null
                ? new { op = "EXPIRE", claims = Array.Empty<string>(), fact = fact.Id }
                : new { op = "SUPERSEDE", claims = Array.Empty<string>(), fact = fact.Id, text = past, category = "other" });
        }

        List<(string FactMarker, string ClaimMarker, bool Expire)> rules;
        lock (_rules) rules = [.. _rules.OrderByDescending(r => r.ClaimMarker.Length)]; // most specific marker wins
        foreach (var claim in claims.Where(c => c.Text is not null))
        {
            foreach (var rule in rules)
            {
                if (!claim.Text!.Contains(rule.ClaimMarker, StringComparison.Ordinal)) continue;
                var target = facts.FirstOrDefault(f => !usedFacts.Contains(f.Id) && f.Text?.Contains(rule.FactMarker, StringComparison.Ordinal) == true);
                if (target is null) continue;
                usedFacts.Add(target.Id);
                operations.Add(rule.Expire
                    ? new { op = "EXPIRE", claims = new[] { claim.Id }, fact = target.Id }
                    : new { op = "SUPERSEDE", claims = new[] { claim.Id }, fact = target.Id, text = claim.Text, category = claim.Category ?? "other", key = claim.Key });
                break;
            }
        }
        return JsonSerializer.Serialize(new { operations }, Json);
    }

    private static List<T> Parse<T>(string? line, Func<JsonElement, T> map)
    {
        if (string.IsNullOrWhiteSpace(line)) return [];
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().Select(map).ToList() : [];
        }
        catch (JsonException) { return []; }
    }

    /// <summary>The first non-empty line after the line containing <paramref name="marker"/>.</summary>
    private static string? LineAfter(string text, string marker)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains(marker, StringComparison.Ordinal)) continue;
            for (var j = i + 1; j < lines.Length; j++)
                if (!string.IsNullOrWhiteSpace(lines[j])) return lines[j].Trim();
        }
        return null;
    }

    public static string IsoDate(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
