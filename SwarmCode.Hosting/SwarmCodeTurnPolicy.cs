using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding;
using SwarmCore.Coding.Permissions;
using SwarmCore.Coding.Routing;
using System.Text.RegularExpressions;

namespace SwarmCode.Hosting;

/// <summary>
/// SwarmCode's host policy for coding turns. The harness never grants authority itself; a direct user
/// request authorizes its own local tool use, narrowed by the person's permission mode and allow/deny
/// rules (<see cref="CodingPermissionPolicy"/>), and its working-context sources are the user's own
/// repository and shell.
/// </summary>
public static class SwarmCodeTurnPolicy
{
    /// <summary>A direct request in SwarmCode is user-requested authority; the permission policy narrows what runs unasked.</summary>
    public static AgentCommunicationScope Scope(string authority) => AgentCommunicationScope.UserRequested(authority);

    /// <summary>
    /// A coding workspace's sources are the user's own repository and shell: no external
    /// communication content needs provenance proof, so receipts checkpoint (and restore)
    /// without a host validator.
    /// </summary>
    public static Task<bool> TrustLocalSources(IReadOnlyList<string> sourceCallIds, CancellationToken ct) => Task.FromResult(true);

    /// <summary>
    /// Applies the SwarmCode policy to a request: user-requested authority plus the person's permission policy. Under a
    /// policy that asks or refuses, side-effecting tools go through the guard and the permission broker, and native lanes
    /// (which bypass both) are not used.
    /// <para>
    /// SwarmCode exists to get the person's request done whatever one model's biases are: a lane that declines hands the
    /// request to the next lane (the person's hand-off preference, <see cref="CodingTurnRequest.AllowHandOff"/>, and
    /// lanes marked <see cref="CodingLane.PreferOnDecline"/> go first). Provider/model selections choose the first attempt;
    /// they do not set <see cref="CodingTurnRequest.SelectionPinned"/>. The host still owns every retry: the inner router may
    /// not swap a lane's model behind these visible hand-offs (<see cref="CodingTurnRequest.DisableRouteFallback"/>).
    /// </para>
    /// </summary>
    public static CodingTurnRequest Apply(CodingTurnRequest request, CodingPermissionPolicy permissions, string authority)
    {
        var original = request.AuthenticatedOriginalUserMessage ?? request.UserMessage;
        var reviewRequested = !request.IsContinuation && !request.PlanMode && SwarmCore.Coding.Review.ModelPanelReviewPrompts.RequestsReview(original);
        return request with
        {
            CommunicationScope = Scope(authority),
            Permissions = permissions,
            StopOnRefusal = false,
            DisableRouteFallback = true,
            RequireWorkspaceChange = RequestsFileChange(original),
            ValidateWorkingContextSourceCallsAsync = TrustLocalSources,
            // A review request is framed for the model; the person's own words stay the authenticated original.
            UserMessage = reviewRequested ? SwarmCore.Coding.Review.ModelPanelReviewPrompts.FrameRequest(request.UserMessage) : request.UserMessage,
            AuthenticatedOriginalUserMessage = reviewRequested ? original : request.AuthenticatedOriginalUserMessage,
        };
    }

    /// <summary>Only direct edit instructions trigger the write receipt requirement; questions and code examples do not.</summary>
    public static bool RequestsFileChange(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        const string fileName = @"[\w./\\-]+\.[a-z0-9]{1,10}";

        // Examples are data, including imperatives inside quoted text or tool arguments.
        // Keep quoted file names usable as edit targets, but never unquote an instruction.
        var prose = Regex.Replace(message,
            @"(?ms)^[ \t]*(?<fence>`{3,}|~{3,})[^\r\n]*\r?\n.*?(?:^[ \t]*\k<fence>[ \t]*(?:\r?\n|$)|\z)", ";");
        prose = Regex.Replace(prose, @"(?m)^[ \t]*>[^\r\n]*", ";");
        prose = Regex.Replace(prose, """`[^`]*`|"[^"]*"|(?<!\w)'[^']*'|“[^”]*”|‘[^’]*’""",
            match => Regex.IsMatch(match.Value[1..^1], "^" + fileName + "$", options)
                ? match.Value[1..^1] : " quoted-value ");

        // Evaluate requests separately: a negated clause cannot supply an edit verb to a
        // later "No files" clause, or suppress a later, explicit positive edit request.
        var clauses = Regex.Split(prose,
            @"[.!?](?=\s|$)|[;\r\n]+|\s*,?\s+\b(?:but|however|instead)\b\s*,?\s*", options);
        foreach (var clause in clauses)
        {
            // Requiring a request at the start excludes negations and supporting prose
            // ("do not edit", "explain how to edit", "the report says to edit", etc.).
            var instruction = Regex.Match(clause,
                @"^\s*(?:[-*]|\d+[.)])?\s*(?:(?:please|then|also|instead)\s*,?\s*)*"
                + @"(?:(?:can|could|would|will)\s+you\s+|i\s+(?:want|need|would\s+like)\s+you\s+to\s+)?"
                + @"(?:please\s+)?(?:go\s+ahead\s+and\s+|(?:do\s+not|don['’]t)\s+(?:forget|hesitate)\s+to\s+)?"
                + @"(?:do\s+)?(?<verb>implement|apply|edit|modify|refactor|fix|change|make|create|add|write|remove|delete|update)\s+"
                + @"(?<object>.+)$", options);
            if (!instruction.Success) continue;

            var target = Regex.Replace(instruction.Groups["object"].Value,
                @"^(?:the|a|an|this|that|these|those|my|our|your)\s+", "", options);
            var kinds = Regex.IsMatch(instruction.Groups["verb"].Value,
                @"^(implement|apply|edit|modify|refactor|fix|change)$", options)
                ? @"changes?|code|files?|bugs?|features?|tests?|project|repository|repo|app"
                : @"code|files?|tests?|features?";
            // Look for the object being edited, not code mentioned as an example, tool
            // input, or supporting material ("add a service with this code").
            if (Regex.IsMatch(target,
                @"^(?:(?!\b(?:not|no|never|neither|none|without|with|using|from|that|which)\b"
                + @"|\b(?:examples?|samples?|snippets?|explanations?|descriptions?|instructions?|reports?)\b(?!\s+(?:files?|tests?)\b))[^,:{}]){0,100}?"
                + @"(?:\b(?:" + kinds + @")\b(?!\s+(?:examples?|samples?|snippets?|reports?)\b)|"
                + fileName + @"(?=\s|$))", options)) return true;
        }
        return false;
    }
}

/// <summary>
/// How SwarmCode chooses a turn's permission policy: the person's mode, the session's inherited floor, the settings
/// rules, and the repository's own rules (<see cref="RepositoryPermissionRules"/>: deny rules always, allow rules only
/// once the person approved that content).
/// </summary>
public static class SwarmCodePermissions
{
    /// <summary>The policy for <paramref name="mode"/> with the allow/deny rules from this workspace's <c>Coding:Permissions</c> only (no repository rules).</summary>
    public static CodingPermissionPolicy Policy(CodingPermissionMode mode, IServiceProvider services)
        => CodingPermissionPolicy.From(mode, services.GetService<IOptions<CodingOptions>>()?.Value.Permissions);

    /// <summary>
    /// The policy for one turn: <paramref name="mode"/> with the person's rules (<paramref name="userRules"/>, or the
    /// workspace's <c>Coding:Permissions</c>) merged with the repository's settings rules. The repository files are read
    /// once per call; the approval store is the host's (<see cref="Extensions.FileCodingRepositorySettingsApprovalStore"/>)
    /// or, without one, nothing is approved. Never throws for a repository problem: those rules simply contribute nothing.
    /// </summary>
    public static async Task<CodingPermissionPolicy> PolicyAsync(CodingPermissionMode mode, IServiceProvider services,
        CodingPermissionRules? userRules = null, string? repositoryRoot = null, CancellationToken ct = default)
    {
        var options = services.GetService<IOptions<CodingOptions>>()?.Value;
        userRules ??= options?.Permissions;
        repositoryRoot ??= options?.RepositoryRoot;
        return CodingPermissionPolicy.From(mode, await MergedRulesAsync(services, userRules, repositoryRoot, ct).ConfigureAwait(false));
    }

    /// <summary>The person's rules plus the repository's (see <see cref="RepositoryPermissionRules.Merge"/>).</summary>
    public static async Task<CodingPermissionRules> MergedRulesAsync(IServiceProvider services, CodingPermissionRules? userRules, string? repositoryRoot, CancellationToken ct = default)
    {
        RepositoryPermissionInspection? repository = null;
        try { repository = await InspectRepositoryAsync(services, repositoryRoot, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { /* a repository that cannot be inspected contributes no rules */ }
        return RepositoryPermissionRules.Merge(userRules, repository);
    }

    /// <summary>Reads the repository's settings files and whether their allow rules are approved.</summary>
    public static Task<RepositoryPermissionInspection> InspectRepositoryAsync(IServiceProvider services, string? repositoryRoot, CancellationToken ct = default)
        => RepositoryPermissionRules.InspectAsync(repositoryRoot, services.GetService<ICodingRepositorySettingsApprovalStore>(), ct);

    /// <summary>The prompt shown before a repository's allow rules may apply.</summary>
    public static string DescribeApprovalRequest(RepositoryPermissionInspection inspection)
        => "This repository asks to let these tools run without a prompt (allow rules):\n"
           + string.Join("\n", inspection.Allow.Select(r => "  " + r))
           + (inspection.Deny.Count == 0 ? "" : "\nIts deny rules always apply: " + string.Join(", ", inspection.Deny))
           + "\nAllow rules widen what runs unasked in this repository. Apply them?";

    /// <summary>An agent's session carries the mode of the session that started it; the stricter of that and the person's current mode applies.</summary>
    public static CodingPermissionMode ForSession(CodingPermissionMode mode, WorkspaceSessionInfo? session)
        => CodingPermissionPolicy.TryParse(session?.PermissionMode, out var inherited)
            ? CodingPermissionPolicy.Stricter(mode, inherited)
            : mode;
}
