using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SwarmCore.Abstractions.CodingTasks;
using SwarmCore.Abstractions.Budgets;
using SwarmCore.Agent.Continual;
using SwarmCore.Agent.Runtime;

namespace SwarmCore.Missions;

/// <summary>The coding entry point compiles an assignment into the existing durable mission lifecycle.</summary>
public sealed class VerifiedCodingTaskExecutor(MissionService missions, MissionProcessorHost host, IMissionStore store,
    ICodingWorkspaceService workspaces, ICurrentTurnContextAccessor context, IOptions<CodingTaskOptions> options,
    IContinualObjectiveTextSanitizer sanitizer, ISandboxPathResolver sandbox, TimeProvider time, IMissionBudgetLedger? budgets = null) : IVerifiedCodingTaskExecutor
{
    public async Task<CodingTaskResult> RunAsync(CodingTaskRequest request, CancellationToken ct = default)
    {
        request = request with { RepoPath = string.IsNullOrWhiteSpace(request.RepoPath) ? options.Value.DefaultRepoPath : request.RepoPath };
        var caller = context.Current;
        var systemRepair = caller?.CodingRepairGrant?.Allows(request) == true;
        if (caller?.ProfileId is not { } profile || string.IsNullOrWhiteSpace(caller.ConversationId)
            || (!caller.CommunicationScopeUserInitiated && caller.MissionObjectiveId is null && !systemRepair))
            return Rejected("Verified coding requires an authenticated assignment or a host-configured repository repair grant and profile.");
        if (caller.MissionObjectiveId is not null && request.UseExistingCheckout)
            return Rejected("Attached coding requires an isolated workspace; the parent checkout is preserved.");
        if (request.Mode != CodingTaskMode.Verified || string.IsNullOrWhiteSpace(request.TaskKey)
            || string.IsNullOrWhiteSpace(request.RepoPath) || string.IsNullOrWhiteSpace(request.Title)
            || string.IsNullOrWhiteSpace(request.Prompt) || request.AcceptanceCriteria.Count == 0
            || request.MaximumAttempts is <= 0)
            return Rejected("Specify a stable task key, repository, task and acceptance criteria with valid limits.");
        if (request.AllowedToolIds.Count == 0 || caller.AdmittedToolIds is null
            || request.AllowedToolIds.Any(id => !caller.AdmittedToolIds.Contains(id))
            || request.AcceptanceCriteria.Any(c => !request.AllowedToolIds.Contains(c.ToolName)))
            return Rejected("Requested coding tools must be admitted by the current profile and include every acceptance check.");
        if (request.AcceptanceCriteria.Any(c => string.IsNullOrWhiteSpace(c.Id) || string.IsNullOrWhiteSpace(c.Description))
            || request.AcceptanceCriteria.Select(c => c.Id).Distinct().Count() != request.AcceptanceCriteria.Count)
            return Rejected("Acceptance criteria need distinct stable identifiers and descriptions.");
        string repo;
        try { repo = SandboxPathResolver.ResolvePhysicalPath(Path.GetFullPath(request.RepoPath)); }
        catch (Exception ex) when (ex is ArgumentException or IOException) { return Rejected("Invalid repository path."); }
        if (systemRepair && (caller.CodingRepairGrant?.Allows(request with { RepoPath = repo }) != true || caller.MissionResources is null))
            return Rejected("Automatic repair requires the unchanged configured repository and its host resource boundary.");
        var owner = caller.ActingAgentId ?? profile.ToString("D");
        var resources = caller.MissionResources ?? MissionResourceAuthority.LocalWorkspace(profile, owner, caller.ConversationId, sandbox.Root);
        if (!resources.Evaluate(new(new(MissionResourceKind.Directory, repo),
            MissionEffect.Inspect | MissionEffect.Modify | MissionEffect.Execute, profile.ToString("D"))).Allowed)
            return Rejected("Repository editing and command execution are outside the current assignment.");
        resources = resources with { AssignmentInterpreted = true };
        string Safe(string value) => sanitizer.Sanitize(value, 16000) ?? "";
        request = request with { RepoPath = repo, Title = Safe(request.Title), Prompt = Safe(request.Prompt),
            TaskKey = Safe(request.TaskKey), SourceRef = request.SourceRef is null ? null : Safe(request.SourceRef),
            Constraints = request.Constraints.Select(Safe).ToArray(),
            AcceptanceCriteria = request.AcceptanceCriteria.Select(c => c with
            { Description = Safe(c.Description), InputJson = Safe(c.InputJson), ResultContains = c.ResultContains is null ? null : Safe(c.ResultContains) }).ToArray() };
        var hash = MissionVerification.Digest(JsonSerializer.Serialize(request));
        var identity = MissionVerification.Digest(profile.ToString("D") + ":" + caller.ConversationId + ":" + request.TaskKey);
        var runId = new Guid(Convert.FromHexString(identity[..32]));
        var root = SandboxPathResolver.ResolvePhysicalPath(Path.GetFullPath(options.Value.WorkspaceRoot));
        var expectedWorkspace = request.UseExistingCheckout ? repo : Path.Combine(root, runId.ToString("N"));
        MissionEvidenceRequirement Requirement(CodingAcceptanceCriterion criterion) => new(criterion.ToolName,
            criterion.ToolName != ShellExecTool.ToolId, MapInput(criterion.InputJson, repo, expectedWorkspace), criterion.ResultContains)
            { BindToFileArtifacts = true };
        MissionDefinition definition;
        try
        {
            definition = new() { OwnerId = owner, Constraints = request.Constraints,
                SuccessCriteria = request.AcceptanceCriteria.Select(c => c.Description).ToArray(),
                EvidenceRequirements = request.AcceptanceCriteria.Select(Requirement).ToArray(),
                AcceptanceCriteria = request.AcceptanceCriteria.Select(c => new MissionAcceptanceCriterion(c.Id, c.Description, CriterionValidationMethod.Receipt)
                { Receipt = Requirement(c), ExpectedExitCode = c.ExpectedExitCode })
                    .Append(new MissionAcceptanceCriterion("coding-delivery", "The requested production behavior is fixed without weakening tests, assertions, analyzers or the assigned acceptance criteria.", CriterionValidationMethod.Semantic)).ToArray(),
                DeadlineUtc = request.DeadlineUtc, MaximumAttempts = request.MaximumAttempts, BudgetReference = request.BudgetReference,
                CodingRun = new() { RunId = runId, RequestHash = hash, ProfileId = profile, ConversationId = caller.ConversationId,
                    Request = request, WorkspaceRoot = root, Phase = CodingRunPhase.Registered } };
            definition.Validate();
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException) { return Rejected("Acceptance selectors must be valid JSON objects."); }
        var objective = $"Implement the coding assignment in {expectedWorkspace}. Read repository instructions and architecture first. "
            + "Inspect, edit production code, restore/build and run the exact acceptance checks; repair failures without weakening tests. "
            + "Preserve unrelated/dirty files, do not commit, push, deploy or publish. Return a concise changed-file rationale and verification evidence. "
            + request.Title + "\n" + request.Prompt;
        ContinualObjectiveSnapshot mission;
        try { mission = await missions.CreateAsync(caller with { MissionResources = resources }, "coding:" + identity, objective, definition, ct); }
        catch (InvalidOperationException ex) { return Rejected(ex.Message, CodingTaskOutcome.Conflict); }
        if (mission.Mission?.CodingRun is not { } run || run.RequestHash != hash || run.RunId != runId)
            return Rejected("The task key is already bound to another coding assignment.", CodingTaskOutcome.Conflict);
        var deadlineUtc = mission.Mission!.DeadlineUtc;
        if (run.Workspace is null && deadlineUtc <= time.GetUtcNow())
        {
            var expired = await store.TryUpdateCodingRunAsync(mission.Id, mission.Version, run with
                { Phase = CodingRunPhase.Blocked, PreparationOwner = null, PreparationExpiresAtUtc = null,
                    Failure = "The assigned coding deadline expired before workspace preparation completed." }, ct: ct);
            return Result(expired ?? mission, CodingTaskOutcome.Failed, "The assigned coding deadline expired; no new work was dispatched.");
        }
        if (run.Workspace is null)
        {
            if (run.PreparationExpiresAtUtc > time.GetUtcNow()) return Result(mission, CodingTaskOutcome.Pending, "Workspace preparation is already owned.");
            var preparation = Guid.NewGuid().ToString("N");
            var claimed = await store.TryUpdateCodingRunAsync(mission.Id, mission.Version, run with
            { Phase = CodingRunPhase.Preparing, PreparationOwner = preparation, PreparationExpiresAtUtc = time.GetUtcNow().AddMinutes(5) }, ct: ct);
            if (claimed is null) return Result(mission, CodingTaskOutcome.Pending, "Another caller claimed workspace preparation.");
            mission = claimed; run = mission.Mission!.CodingRun!;
            await using var budgetDeadline = new MissionBudgetDeadlineMonitor(budgets, profile, mission.Mission!.BudgetReference, time, ct);
            try
            {
                using var preparationLifetime = CancellationTokenSource.CreateLinkedTokenSource(budgetDeadline.Token);
                if (deadlineUtc is { } deadline)
                    preparationLifetime.CancelAfter(deadline > time.GetUtcNow() ? deadline - time.GetUtcNow() : TimeSpan.Zero);
                var workspace = await workspaces.PrepareAsync(new(runId, repo, root)
                { BaselineRef = request.BaselineRef, UseExistingCheckout = request.UseExistingCheckout, AdmittedPaths = request.AdmittedPaths,
                    ValidatePreparation = caller.MissionObjectiveId is null ? null : caller.ValidateMissionExecution,
                    AuthorizePreparationWrite = async (target, directory, cancel) =>
                    {
                        cancel.ThrowIfCancellationRequested();
                        if (caller.MissionObjectiveId is not null) await caller.ValidateMissionExecution!(cancel);
                        var physical = SandboxPathResolver.ResolvePhysicalPath(target);
                        var relative = Path.GetRelativePath(expectedWorkspace, physical);
                        if (Path.IsPathFullyQualified(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                            throw new InvalidOperationException("Preparation target is outside the bound workspace.");
                        var source = SandboxPathResolver.ResolvePhysicalPath(Path.Combine(repo, relative));
                        if (!resources.Evaluate(new(new(directory ? MissionResourceKind.Directory : MissionResourceKind.File, source),
                            MissionEffect.Modify, profile.ToString("D"))).Allowed)
                            throw new InvalidOperationException("Preparation target is excluded by the original assignment.");
                    } }, preparationLifetime.Token);
                if (caller.MissionObjectiveId is not null) await caller.ValidateMissionExecution!(preparationLifetime.Token);
                preparationLifetime.Token.ThrowIfCancellationRequested();
                var prepared = CodingRunPolicy.PrepareAuthority(mission.Authority.MissionResources!, workspace);
                mission = await store.TryUpdateCodingRunAsync(mission.Id, mission.Version, run with
                { Phase = CodingRunPhase.Ready, Workspace = workspace, PreparationOwner = null, PreparationExpiresAtUtc = null },
                    prepared, preparationLifetime.Token) ?? throw new InvalidOperationException("Workspace publication lost its run lease; retained work is available for recovery.");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && (deadlineUtc <= time.GetUtcNow() || budgetDeadline.DeadlineExpired))
            {
                var expired = await store.TryUpdateCodingRunAsync(mission.Id, mission.Version, run with
                    { Phase = CodingRunPhase.Blocked, PreparationOwner = null, PreparationExpiresAtUtc = null,
                        Failure = "The assigned coding deadline expired during workspace preparation." }, ct: CancellationToken.None);
                return Result(expired ?? mission, CodingTaskOutcome.Failed, "Coding deadline expired; partial workspace preserved.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
            { return Result(mission, CodingTaskOutcome.Blocked, "Workspace preparation failed; retained work was not removed: " + Safe(ex.Message)); }
        }
        if (mission.Status is not (ContinualObjectiveStatus.Completed or ContinualObjectiveStatus.Cancelled or ContinualObjectiveStatus.Failed))
        {
            await host.Processor.ProcessAsync(mission.Id, "coding:" + runId.ToString("N"), ct);
            mission = await store.GetAsync(mission.Id, ct) ?? throw new InvalidOperationException("Coding mission disappeared.");
        }
        if (mission.Status == ContinualObjectiveStatus.Cancelled) return Result(mission, CodingTaskOutcome.Cancelled, "Coding work was cancelled; workspace preserved.");
        if (mission.Status != ContinualObjectiveStatus.Completed)
            return Result(mission, mission.Status == ContinualObjectiveStatus.Failed ? CodingTaskOutcome.Failed : CodingTaskOutcome.Pending,
                mission.LastFailure ?? "Coding mission remains active; repeat the same task key to recover its current result.");
        var capture = await workspaces.CaptureAsync(mission.Mission!.CodingRun!.Workspace!, ct);
        var error = ValidateFinalEvidence(mission, capture);
        if (error is not null)
        {
            var reopened = await store.TryUpdateCodingRunAsync(mission.Id, mission.Version,
                mission.Mission.CodingRun with { Phase = CodingRunPhase.Ready, Verification = null, Failure = error }, ct: ct);
            return Result(reopened ?? mission, reopened is null ? CodingTaskOutcome.Blocked : CodingTaskOutcome.Pending, error);
        }
        var accepted = mission.VerificationHistory.LastOrDefault();
        if (accepted is null || accepted.CriteriaDigest != MissionVerification.CriteriaDigest(mission)
            || accepted.Criteria.Any(c => c.Outcome != CriterionOutcome.Pass)
            || MissionVerification.Criteria(mission).Any(c => !accepted.Criteria.Any(v => v.CriterionId == c.Id)))
            return Result(mission, CodingTaskOutcome.Blocked, "Independent mission acceptance has not certified this assignment.");
        var proof = new CodingVerificationProof(capture.TreeDigest, MissionVerification.CriteriaDigest(mission),
            MissionVerification.EvidenceDigest(mission), mission.Mission.ConstraintVersion,
            mission.Authority.MissionResources?.Revision ?? 0, time.GetUtcNow(),
            MissionVerification.Criteria(mission).Where(c => c.Method != CriterionValidationMethod.Semantic).SelectMany(c => MissionVerification.Check(mission, c).EvidenceReferences).Distinct().ToArray())
            { Diff = capture.Diff, ChangedFiles = capture.ChangedFiles };
        var published = await store.TryUpdateCodingRunAsync(mission.Id, mission.Version,
            mission.Mission.CodingRun with { Phase = CodingRunPhase.Verified, Verification = proof }, ct: ct);
        return published is null ? Result(mission, CodingTaskOutcome.Pending, "Current coding evidence changed during publication.")
            : Result(published, CodingTaskOutcome.Verified, null);
    }

    public static string? ValidateFinalEvidence(ContinualObjectiveSnapshot mission, CodingWorkspaceCapture capture)
    {
        if (capture.BoundaryViolations.Count > 0) return "Workspace boundary changed: " + string.Join(", ", capture.BoundaryViolations);
        foreach (var criterion in MissionVerification.Criteria(mission).Where(c => c.Method != CriterionValidationMethod.Semantic))
        {
            var check = MissionVerification.Check(mission, criterion);
            if (check.Outcome != CriterionOutcome.Pass) return check.Reason;
            var receipts = mission.Receipts.Where(r => check.EvidenceReferences.Contains(MissionVerification.Reference(r))).ToArray();
            if (receipts.Length == 0 || receipts.Any(r => r.CodingTreeDigest != capture.TreeDigest))
                return "The final coding tree differs from its acceptance evidence; rerun the unchanged acceptance checks.";
        }
        return null;
    }

    private static string MapInput(string json, string repo, string workspace)
    {
        var node = JsonNode.Parse(json) as JsonObject ?? throw new ArgumentException("Expected selector object.");
        void Map(JsonNode? value)
        {
            if (value is JsonObject obj) foreach (var property in obj.ToArray())
            {
                if (property.Value is JsonValue text && text.TryGetValue<string>(out var s)
                    && (s == repo || s.StartsWith(repo + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                    obj[property.Key] = workspace + s[repo.Length..];
                else Map(property.Value);
            }
            else if (value is JsonArray array) foreach (var item in array) Map(item);
        }
        Map(node); return node.ToJsonString();
    }
    private static CodingTaskResult Rejected(string error, CodingTaskOutcome outcome = CodingTaskOutcome.Blocked) => new() { Outcome = outcome, Error = error };
    private static CodingTaskResult Result(ContinualObjectiveSnapshot mission, CodingTaskOutcome outcome, string? error)
    {
        var run = mission.Mission!.CodingRun!;
        return new() { Success = outcome == CodingTaskOutcome.Verified, Outcome = outcome, Error = error,
            RunId = run.RunId, MissionId = mission.Id, RequestHash = run.RequestHash, Workspace = run.Workspace,
            WorktreePath = run.Workspace?.WorkspacePath, BranchName = run.Workspace?.Branch ?? "",
            Verification = outcome == CodingTaskOutcome.Verified ? run.Verification : null,
            DiffStat = outcome == CodingTaskOutcome.Verified ? $"{run.Verification?.ChangedFiles.Count ?? 0} changed files" : "",
            Summary = outcome == CodingTaskOutcome.Verified ? "Verified local change; workspace retained without committing. Not applied: the running application still uses its existing binaries." : error ?? "Coding work is pending." };
    }
}
