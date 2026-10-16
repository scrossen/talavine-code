using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SwarmCore.Coding.Forge;

public sealed record PullRequestSummary(
    int Number, string Title, string State, bool Draft, bool Merged, string HeadRef, string? HeadSha, string BaseRef, string Url, string? Author);

public sealed record PullRequestCheck(string Name, string Source, string Status, string? Conclusion, string? Url);

public sealed record PullRequestReview(long Id, string? Author, string State, string? Body, string? SubmittedAt);

public sealed record PullRequestComment(
    long Id, string Kind, string? Author, string Body, string? Path, int? Line, long? InReplyToId, string? CreatedAt, string? Url);

/// <summary>
/// One pull request with everything needed to decide the next step: mergeability, the CI rollup (check runs plus
/// commit statuses on the head commit), reviews with a derived decision, and review/issue comments.
/// </summary>
public sealed record PullRequestStatus(
    PullRequestSummary PullRequest,
    string Mergeable,
    string? MergeableState,
    string ChecksRollup,
    IReadOnlyList<PullRequestCheck> Checks,
    string ReviewDecision,
    IReadOnlyList<PullRequestReview> Reviews,
    IReadOnlyList<PullRequestComment> Comments,
    bool Truncated);

/// <summary>The GitHub operations the forge tools use. Implemented over an <see cref="IGitHubApiTransport"/>.</summary>
public interface IGitHubForge
{
    Task<string> GetDefaultBranchAsync(ForgeRepository repository, CancellationToken ct);
    Task<PullRequestSummary?> FindPullRequestForBranchAsync(ForgeRepository repository, string branch, CancellationToken ct);
    Task<PullRequestStatus> GetPullRequestStatusAsync(ForgeRepository repository, int number, CancellationToken ct);
    Task<PullRequestSummary> CreatePullRequestAsync(ForgeRepository repository, string title, string? body, string head, string @base, bool draft, CancellationToken ct);
    Task<PullRequestComment> CommentAsync(ForgeRepository repository, int number, string body, CancellationToken ct);
    Task<PullRequestComment> ReplyToReviewCommentAsync(ForgeRepository repository, int number, long commentId, string body, CancellationToken ct);
}

/// <summary>GitHub REST semantics (and parsing) shared by the gh and token transports.</summary>
public sealed class GitHubForge(IGitHubApiTransport transport) : IGitHubForge
{
    public const int PageSize = 100;
    public const int MaxCommentCharacters = 4000;

    public async Task<string> GetDefaultBranchAsync(ForgeRepository repository, CancellationToken ct)
    {
        var repo = await GetObjectAsync(repository, repository.ApiPath, ct).ConfigureAwait(false);
        return Str(repo, "default_branch") ?? throw new GitHubForgeException("GitHub did not report the repository's default branch.");
    }

    public async Task<PullRequestSummary?> FindPullRequestForBranchAsync(ForgeRepository repository, string branch, CancellationToken ct)
    {
        var head = Uri.EscapeDataString(repository.Owner + ":" + branch);
        var list = await GetArrayAsync(repository, $"{repository.ApiPath}/pulls?head={head}&state=all&sort=created&direction=desc&per_page=10", ct).ConfigureAwait(false);
        var prs = list.OfType<JsonObject>().Select(ParseSummary).ToList();
        return prs.FirstOrDefault(p => p.State == "open") ?? prs.FirstOrDefault();
    }

    public async Task<PullRequestStatus> GetPullRequestStatusAsync(ForgeRepository repository, int number, CancellationToken ct)
    {
        var prPath = $"{repository.ApiPath}/pulls/{number.ToString(CultureInfo.InvariantCulture)}";
        var pr = await GetObjectAsync(repository, prPath, ct).ConfigureAwait(false);
        var summary = ParseSummary(pr);
        var sha = summary.HeadSha;
        var checkRuns = sha is null ? Task.FromResult<JsonNode?>(null) : GetNodeAsync(repository, $"{repository.ApiPath}/commits/{sha}/check-runs?per_page={PageSize}", ct);
        var statuses = sha is null ? Task.FromResult<JsonNode?>(null) : GetNodeAsync(repository, $"{repository.ApiPath}/commits/{sha}/status?per_page={PageSize}", ct);
        var reviews = GetArrayAsync(repository, $"{prPath}/reviews?per_page={PageSize}", ct);
        var reviewComments = GetArrayAsync(repository, $"{prPath}/comments?per_page={PageSize}", ct);
        var issueComments = GetArrayAsync(repository, $"{repository.ApiPath}/issues/{number.ToString(CultureInfo.InvariantCulture)}/comments?per_page={PageSize}", ct);
        await Task.WhenAll(checkRuns, statuses, reviews, reviewComments, issueComments).ConfigureAwait(false);
        return BuildStatus(pr, await checkRuns, await statuses, await reviews, await reviewComments, await issueComments);
    }

    public async Task<PullRequestSummary> CreatePullRequestAsync(ForgeRepository repository, string title, string? body, string head, string @base, bool draft, CancellationToken ct)
    {
        var payload = new JsonObject { ["title"] = title, ["head"] = head, ["base"] = @base, ["draft"] = draft };
        if (!string.IsNullOrEmpty(body)) payload["body"] = body;
        var created = await SendAsync(repository, HttpMethod.Post, $"{repository.ApiPath}/pulls", payload.ToJsonString(), ct).ConfigureAwait(false);
        return ParseSummary(created as JsonObject ?? throw new GitHubForgeException("GitHub returned no pull request."));
    }

    public async Task<PullRequestComment> CommentAsync(ForgeRepository repository, int number, string body, CancellationToken ct)
    {
        var payload = new JsonObject { ["body"] = body }.ToJsonString();
        var created = await SendAsync(repository, HttpMethod.Post, $"{repository.ApiPath}/issues/{number.ToString(CultureInfo.InvariantCulture)}/comments", payload, ct).ConfigureAwait(false);
        return ParseComment(created as JsonObject ?? throw new GitHubForgeException("GitHub returned no comment."), "issue");
    }

    public async Task<PullRequestComment> ReplyToReviewCommentAsync(ForgeRepository repository, int number, long commentId, string body, CancellationToken ct)
    {
        var payload = new JsonObject { ["body"] = body }.ToJsonString();
        var path = $"{repository.ApiPath}/pulls/{number.ToString(CultureInfo.InvariantCulture)}/comments/{commentId.ToString(CultureInfo.InvariantCulture)}/replies";
        var created = await SendAsync(repository, HttpMethod.Post, path, payload, ct).ConfigureAwait(false);
        return ParseComment(created as JsonObject ?? throw new GitHubForgeException("GitHub returned no comment."), "review");
    }

    // ---- parsing (internal for fixture tests) ----

    internal static PullRequestStatus BuildStatus(JsonObject pr, JsonNode? checkRuns, JsonNode? combinedStatus, JsonArray reviews, JsonArray reviewComments, JsonArray issueComments)
    {
        var summary = ParseSummary(pr);
        var checks = new List<PullRequestCheck>();
        if (checkRuns?["check_runs"] is JsonArray runs)
            foreach (var run in runs.OfType<JsonObject>())
                checks.Add(new PullRequestCheck(Str(run, "name") ?? "check", "check-run", Str(run, "status") ?? "unknown", Str(run, "conclusion"), Str(run, "html_url") ?? Str(run, "details_url")));
        if (combinedStatus?["statuses"] is JsonArray statuses)
            foreach (var s in statuses.OfType<JsonObject>())
            {
                var state = Str(s, "state") ?? "pending";
                checks.Add(new PullRequestCheck(Str(s, "context") ?? "status", "status", state == "pending" ? "pending" : "completed",
                    state == "pending" ? null : state, Str(s, "target_url")));
            }

        var reviewList = reviews.OfType<JsonObject>()
            .Select(r => new PullRequestReview(Long(r, "id") ?? 0, Str(r["user"] as JsonObject, "login"), Str(r, "state") ?? "COMMENTED",
                Clip(Str(r, "body")), Str(r, "submitted_at")))
            .ToList();

        var comments = reviewComments.OfType<JsonObject>().Select(c => ParseComment(c, "review"))
            .Concat(issueComments.OfType<JsonObject>().Select(c => ParseComment(c, "issue")))
            .OrderBy(c => c.CreatedAt, StringComparer.Ordinal)
            .ToList();

        var mergeable = pr["mergeable"] is JsonValue m && m.TryGetValue<bool>(out var b) ? (b ? "mergeable" : "conflicting") : "unknown";
        var truncated = (checkRuns?["check_runs"] as JsonArray)?.Count >= PageSize || reviews.Count >= PageSize
                        || reviewComments.Count >= PageSize || issueComments.Count >= PageSize;
        return new PullRequestStatus(summary, mergeable, Str(pr, "mergeable_state"), Rollup(checks), checks,
            ReviewDecision(reviewList), reviewList, comments, truncated);
    }

    /// <summary>failure if any check failed, else pending if any has not finished, else success; none without checks.</summary>
    internal static string Rollup(IReadOnlyList<PullRequestCheck> checks)
    {
        if (checks.Count == 0) return "none";
        static bool Failed(string? c) => c is "failure" or "timed_out" or "cancelled" or "action_required" or "error" or "startup_failure" or "stale";
        if (checks.Any(c => Failed(c.Conclusion))) return "failure";
        if (checks.Any(c => c.Status != "completed")) return "pending";
        return "success";
    }

    /// <summary>The latest decisive review per reviewer: any CHANGES_REQUESTED wins, then APPROVED, else none.</summary>
    internal static string ReviewDecision(IReadOnlyList<PullRequestReview> reviews)
    {
        var latest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in reviews.OrderBy(r => r.SubmittedAt, StringComparer.Ordinal))
        {
            if (r.Author is null || r.State is not ("APPROVED" or "CHANGES_REQUESTED" or "DISMISSED")) continue;
            latest[r.Author] = r.State;
        }
        if (latest.Values.Contains("CHANGES_REQUESTED")) return "changes_requested";
        if (latest.Values.Contains("APPROVED")) return "approved";
        return "none";
    }

    internal static PullRequestSummary ParseSummary(JsonObject pr)
    {
        var number = (int)(Long(pr, "number") ?? 0);
        var merged = pr["merged"] is JsonValue mv && mv.TryGetValue<bool>(out var mb) ? mb : Str(pr, "merged_at") is not null;
        var state = merged ? "merged" : Str(pr, "state") ?? "unknown";
        var draft = pr["draft"] is JsonValue dv && dv.TryGetValue<bool>(out var db) && db;
        return new PullRequestSummary(number, Str(pr, "title") ?? "", state, draft, merged,
            Str(pr["head"] as JsonObject, "ref") ?? "", Str(pr["head"] as JsonObject, "sha"),
            Str(pr["base"] as JsonObject, "ref") ?? "", Str(pr, "html_url") ?? "", Str(pr["user"] as JsonObject, "login"));
    }

    internal static PullRequestComment ParseComment(JsonObject c, string kind)
    {
        int? line = Long(c, "line") is { } l ? (int)l : Long(c, "original_line") is { } ol ? (int)ol : null;
        return new PullRequestComment(Long(c, "id") ?? 0, kind, Str(c["user"] as JsonObject, "login"), Clip(Str(c, "body")) ?? "",
            Str(c, "path"), kind == "review" ? line : null, Long(c, "in_reply_to_id"), Str(c, "created_at"), Str(c, "html_url"));
    }

    private static string? Clip(string? text)
        => text is null ? null : text.Length <= MaxCommentCharacters ? text : text[..MaxCommentCharacters] + " [...]";

    private static string? Str(JsonObject? o, string name)
        => o?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static long? Long(JsonObject? o, string name)
        => o?[name] is JsonValue v && v.TryGetValue<long>(out var n) ? n : null;

    // ---- transport ----

    private async Task<JsonObject> GetObjectAsync(ForgeRepository repository, string path, CancellationToken ct)
        => await SendAsync(repository, HttpMethod.Get, path, null, ct).ConfigureAwait(false) as JsonObject
           ?? throw new GitHubForgeException($"GitHub returned an unexpected response for {path}.");

    private async Task<JsonArray> GetArrayAsync(ForgeRepository repository, string path, CancellationToken ct)
        => await SendAsync(repository, HttpMethod.Get, path, null, ct).ConfigureAwait(false) as JsonArray ?? [];

    private Task<JsonNode?> GetNodeAsync(ForgeRepository repository, string path, CancellationToken ct)
        => SendAsync(repository, HttpMethod.Get, path, null, ct);

    private async Task<JsonNode?> SendAsync(ForgeRepository repository, HttpMethod method, string path, string? body, CancellationToken ct)
    {
        var response = await transport.SendAsync(repository, method, path, body, ct).ConfigureAwait(false);
        JsonNode? node = null;
        try { node = string.IsNullOrWhiteSpace(response.Body) ? null : JsonNode.Parse(response.Body); }
        catch (JsonException) { if (response.IsSuccess) throw new GitHubForgeException("GitHub returned a response that is not JSON.", response.StatusCode); }
        if (response.IsSuccess) return node;
        var message = Str(node as JsonObject, "message") ?? "request failed";
        if ((node as JsonObject)?["errors"] is JsonArray errors && errors.Count > 0)
            message += ": " + string.Join("; ", errors.Select(e => e is JsonObject eo ? Str(eo, "message") ?? Str(eo, "code") ?? eo.ToJsonString() : e?.ToString()));
        throw new GitHubForgeException($"GitHub {response.StatusCode}: {GitRemoteUrl.RedactUserInfo(message)}", response.StatusCode);
    }
}
