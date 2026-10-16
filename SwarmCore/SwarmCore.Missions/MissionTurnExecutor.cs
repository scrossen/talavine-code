using System.Collections.Concurrent;
using SwarmCore.Abstractions.CodingTasks;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Continual;
using SwarmCore.Agent.Runtime;

namespace SwarmCore.Missions;

/// <summary>Runs the existing profile/router/tool/transcript path; no provider-specific mission engine.</summary>
public sealed class MissionTurnExecutor(IServiceScopeFactory scopes, MissionTurnObserver observer, IMissionStore store, TimeProvider time,
    Microsoft.Extensions.Options.IOptions<MissionRuntimeOptions> options, IMissionCurrentStateEvidence? currentState = null)
    : IContinualObjectiveExecutor, IContinualObjectiveRecovery, IContinualObjectiveEvidenceValidator
{
    public string ExecutorKey => options.Value.ExecutorKey;

    public Task<string?> ValidateEvidenceAsync(ContinualObjectiveSnapshot objective, CancellationToken ct = default)
        => ValidateEvidenceAsync(objective, recentCapture: null, ct);

    /// <param name="recentCapture">
    /// A coding-tree capture already taken for this execution (the observer's, after the latest completed tool), so a
    /// per-batch check does not hash the whole workspace again; null captures afresh, as final verification always does.
    /// </param>
    private async Task<string?> ValidateEvidenceAsync(ContinualObjectiveSnapshot objective, CodingWorkspaceCapture? recentCapture, CancellationToken ct)
    {
        using var validationScope = scopes.CreateScope();
        if (await validationScope.ServiceProvider.GetRequiredService<MissionDelegationService>().ValidateChildrenAsync(objective, ct) is { } childError) return childError;
        if (await validationScope.ServiceProvider.GetRequiredService<MissionDelegationService>().ValidateChildContextAsync(objective, ct) is { } packetError) return packetError;
        if (objective.Mission?.CodingRun?.Workspace is { } workspace)
        {
            var capture = recentCapture ?? await validationScope.ServiceProvider.GetRequiredService<ICodingWorkspaceService>().CaptureAsync(workspace, ct);
            if (VerifiedCodingTaskExecutor.ValidateFinalEvidence(objective, capture) is { } codingError) return codingError;
        }
        return await MissionArtifactEvidence.ValidateAsync(objective, ct)
            ?? (currentState is null ? null : await currentState.ValidateAsync(objective, ct));
    }

    public async Task ReconcileAsync(ContinualObjectiveExecutionRequest request, CancellationToken ct = default)
    {
        foreach (var receipt in request.Objective.Receipts.Where(MissionTransitions.NeedsInspection))
        {
            ct.ThrowIfCancellationRequested();
            if (await InspectExternalOutcomeAsync(request, receipt, ct) is { } inspected)
            {
                // A concurrent correction may win after GET returned. The store checks
                // both inspected revisions inside its receipt CAS; rejection leaves the
                // operation uncertain and never causes an external-action retry.
                await store.ReconcileReceiptAsync(request.Objective.Id, request.LeaseToken,
                    receipt with { State = ContinualToolReceiptState.Completed, IsError = false, Result = inspected.Content },
                    inspected.AuthorityRevision, inspected.ConstraintRevision, ct);
                continue;
            }
            if (receipt.ToolName == "missions.report_constraint_conflict" && request.Objective.ConstraintConflict is { } conflict)
            {
                if (!await store.ReconcileReceiptAsync(request.Objective.Id, request.LeaseToken, receipt with {
                    State = ContinualToolReceiptState.Completed, IsError = false, Result = conflict.VerifiedReason }, ct))
                    throw new ContinualObjectiveLeaseLostException(request.Objective.Id);
                continue;
            }
            if (receipt.ToolName == "missions.deploy_artifact" && receipt.RecoveryJson is not null)
            {
                MissionDeploymentProof? deployment;
                try { deployment = JsonSerializer.Deserialize<MissionDeploymentProof>(receipt.RecoveryJson); }
                catch (JsonException) { continue; }
                if (deployment is null || request.Objective.Authority.MissionResources is not { } authority
                    || !authority.EnvironmentBindings.TryGetValue(deployment.Target, out var bound) || bound != deployment.Root
                    || SandboxPathResolver.ResolvePhysicalPath(deployment.AbsolutePath) != deployment.AbsolutePath
                    || Path.GetDirectoryName(deployment.AbsolutePath) != bound || !File.Exists(deployment.AbsolutePath)) continue;
                await using var deployed = File.OpenRead(deployment.AbsolutePath);
                if (Convert.ToHexString(await SHA256.HashDataAsync(deployed, ct)) != deployment.Sha256) continue;
                if (!await store.ReconcileReceiptAsync(request.Objective.Id, request.LeaseToken, receipt with {
                    State = ContinualToolReceiptState.Completed, IsError = false, CompletedAtUtc = time.GetUtcNow(),
                    ArtifactHashes = await MissionArtifactEvidence.CaptureAsync(
                        request.Objective.Receipts.Where(r => r.ArtifactHashes is not null).SelectMany(r => r.ArtifactHashes!.Keys)
                            .Append(deployment.AbsolutePath), ct),
                    Result = "Verified development deployment to " + deployment.Target + "; host inspected SHA-256 " + deployment.Sha256 }, ct))
                    throw new ContinualObjectiveLeaseLostException(request.Objective.Id);
                continue;
            }
            if (receipt.ToolName == "missions.report_external_dependency" && request.Objective.ExternalDependency is { } dependency)
            {
                if (!await store.ReconcileReceiptAsync(request.Objective.Id, request.LeaseToken, receipt with
                    { State = ContinualToolReceiptState.Completed, IsError = false, Result = "The host inspected the committed external-input need: " + dependency.RequiredChange }, ct))
                    throw new ContinualObjectiveLeaseLostException(request.Objective.Id);
                continue;
            }
            if (receipt.ToolName == "missions.checkpoint" && receipt.InputJson is not null)
            {
                MissionCheckpoint? intended;
                try { intended = JsonSerializer.Deserialize<MissionCheckpoint>(receipt.InputJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
                catch (JsonException) { continue; } // Redaction/truncation can make an old intent unverifiable.
                if (intended is not null && JsonSerializer.Serialize(intended) == JsonSerializer.Serialize(request.Objective.Checkpoint))
                {
                    if (!await store.ReconcileReceiptAsync(request.Objective.Id, request.LeaseToken,
                        receipt with { State = ContinualToolReceiptState.Completed, IsError = false,
                            Result = "Host inspected the committed checkpoint and pending work." }, ct))
                        throw new ContinualObjectiveLeaseLostException(request.Objective.Id);
                }
                continue;
            }
            if (receipt.ToolName != WriteFileTool.ToolId || receipt.RecoveryJson is null) continue;
            MissionFileWriteProof? proof;
            try { proof = JsonSerializer.Deserialize<MissionFileWriteProof>(receipt.RecoveryJson); }
            catch (JsonException) { continue; }
            if (proof is null || !File.Exists(proof.AbsolutePath)) continue;
            try
            {
                await using var stream = File.OpenRead(proof.AbsolutePath);
                var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
                if (digest != proof.Sha256) continue;
                if (!await store.ReconcileReceiptAsync(request.Objective.Id, request.LeaseToken,
                    receipt with { State = ContinualToolReceiptState.Completed,
                        Result = $"Host inspected the saved file: SHA-256 {digest}; the intended bytes are present.",
                        IsError = false, CompletedAtUtc = time.GetUtcNow() }, ct))
                    throw new ContinualObjectiveLeaseLostException(request.Objective.Id);
            }
            catch (IOException) { /* Unknown remains unknown; never rewrite to make inspection pass. */ }
        }
    }

    private sealed record InspectedOutcome(string Content, int AuthorityRevision, int ConstraintRevision);
    private async Task<InspectedOutcome?> InspectExternalOutcomeAsync(ContinualObjectiveExecutionRequest request,
        ContinualToolReceipt receipt, CancellationToken ct)
    {
        if (receipt.ToolName is not ("http.request" or "missions.browser" or "computer.act")
            || request.Objective.Mission is not { PlanIsModelAuthored: false } mission || receipt.InputJson is null) return null;
        var inspections = mission.OutcomeInspections.Where(i => i.ToolName == receipt.ToolName);
        // An automatic, no-caller-digest contract the browser tool recorded for THIS exact dispatched
        // action (see MissionBrowserService.RecordAutomaticOutcomeInspectionAsync), captured only when
        // the host mapped the mutated route to a distinct read-only logical resource. Falls back to
        // trusted manual binding (MissionService.BindOutcomeInspectionAsync) when no such proof exists.
        if (receipt.ToolName == "missions.browser" && receipt.RecoveryJson is { } recoveryJson)
        {
            try
            {
                if (JsonSerializer.Deserialize<MissionBrowserOutcomeProof>(recoveryJson) is { } proof)
                    inspections = inspections.Append(new MissionOutcomeInspection(receipt.ToolName, receipt.InputJson, proof.InspectUrl, proof.ExpectedBodySha256));
            }
            catch (JsonException) { }
        }
        foreach (var inspection in inspections)
        {
            try
            {
                if (!System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(inspection.DispatchInputJson),
                    System.Text.Json.Nodes.JsonNode.Parse(receipt.InputJson)) || inspection.ExpectedBodySha256.Length != 64
                    || !Uri.TryCreate(inspection.InspectUrl, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http")
                    || !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Fragment)) continue;
                var latest = await store.GetAsync(request.Objective.Id, ct);
                if (latest?.LeaseToken != request.LeaseToken || latest.Status != ContinualObjectiveStatus.InProgress
                    || latest.Authority.MissionResources is not { } authority
                    || !authority.Evaluate(new(new(MissionResourceKind.Endpoint, url.AbsoluteUri), MissionEffect.Inspect, latest.PartitionKey)).Allowed
                    || await SwarmCore.Services.Http.PublicAddressGuard.IsBlockedHostAsync(url, ct, blockLoopback: false, blockUnresolvable: true)) continue;
                using var inspectionScope = scopes.CreateScope();
                var clients = inspectionScope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
                using var client = clients.CreateClient(HttpRequestTool.NoRedirectClientName);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                var budgetContext = SwarmCore.Abstractions.Budgets.MissionBudgetExecutionScope.Current;
                await using var budget = await MissionBudgetToolLease.ReserveAsync(budgetContext?.Ledger,
                    Guid.Parse(latest.PartitionKey), budgetContext?.Reference, latest.Id,
                    "outcome-inspection:" + receipt.ToolCallId + ":" + Guid.NewGuid().ToString("N"),
                    TimeSpan.FromSeconds(15), costUpperBoundUsd: null, ct: timeout.Token);
                await budget.MarkDispatchedAsync(timeout.Token);
                using var response = await client.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri != url
                    || response.Content.Headers.ContentLength > 65536)
                {
                    await budget.CompleteAsync(new("Outcome inspection returned an unusable response.", true, true, true), CancellationToken.None);
                    continue;
                }
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var buffer = new MemoryStream();
                var bytes = new byte[4096];
                int count;
                while ((count = await stream.ReadAsync(bytes, timeout.Token)) > 0)
                {
                    buffer.Write(bytes, 0, count);
                    if (buffer.Length > 65536) break;
                }
                await budget.CompleteAsync(new("Read-only outcome inspection finished.", buffer.Length > 65536, true, true), CancellationToken.None);
                if (buffer.Length > 65536 || !Convert.ToHexString(SHA256.HashData(buffer.ToArray())).Equals(
                    inspection.ExpectedBodySha256, StringComparison.OrdinalIgnoreCase)) continue;
                var current = await store.GetAsync(latest.Id, ct);
                if (current?.LeaseToken != request.LeaseToken || current.Authority.MissionResources?.Revision != authority.Revision
                    || current.Mission?.ConstraintVersion != mission.ConstraintVersion) continue;
                return new("Host verified the caller-bound external outcome SHA-256 " + inspection.ExpectedBodySha256
                    + "; no action was replayed. Observed content: "
                    + SwarmCore.Agent.UntrustedContent.Fence(Encoding.UTF8.GetString(buffer.ToArray()), "OUTCOME INSPECTION"),
                    authority.Revision, mission.ConstraintVersion);
            }
            catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException) { }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
        return null;
    }

    public async Task<ContinualObjectiveExecutionResult> ExecuteAsync(ContinualObjectiveExecutionRequest request,
        IContinualObjectiveExecutionReporter reporter, CancellationToken ct = default)
    {
        var s = request.Objective;
        using (var assignmentScope = scopes.CreateScope())
        {
            if (s.Authority.MissionResources is null)
            {
                var paths = assignmentScope.ServiceProvider.GetRequiredService<ISandboxPathResolver>();
                var defaults = MissionResourceAuthority.LocalWorkspace(Guid.Parse(s.PartitionKey), s.Mission!.OwnerId, s.IdempotencyKey, paths.Root);
                var deploymentOptions = assignmentScope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<MissionDeploymentOptions>>().Value;
                defaults = defaults with { Exclusions = deploymentOptions.DevelopmentDirectories.Values.Select(path =>
                    new MissionResourceGrant(new(MissionResourceKind.Directory, SandboxPathResolver.ResolvePhysicalPath(path)),
                        MissionEffect.Modify | MissionEffect.Remove | MissionEffect.Execute | MissionEffect.Install,
                        "host-development-environment-boundary", true)).ToArray() };
                if (!await store.InitializeLegacyAuthorityAsync(s.Id, request.LeaseToken, defaults, ct)) throw new ContinualObjectiveLeaseLostException(s.Id);
                s = (await store.GetAsync(s.Id, ct))!;
                request = request with { Objective = s };
            }
            if (s.Authority.MissionResources is { AssignmentInterpreted: false } resources)
            {
                var derived = await assignmentScope.ServiceProvider.GetRequiredService<MissionAssignmentInterpreter>().DeriveAsync(s, ct);
                if (!await store.SetDerivedAuthorityAsync(s.Id, request.LeaseToken, resources.Revision, derived, ct))
                    throw new ContinualObjectiveLeaseLostException(s.Id);
                s = await store.GetAsync(s.Id, ct) ?? throw new ContinualObjectiveLeaseLostException(s.Id);
                request = request with { Objective = s };
            }
        }
        var payload = JsonSerializer.Deserialize<MissionTurnPayload>(s.ExecutorPayloadJson!)
            ?? throw new InvalidOperationException("Missing mission host context.");
        using var registration = observer.Register(request, reporter);
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IConversationStore>().GetOrCreateAsync(
            s.ConversationId, s.ObjectiveSummary, payload.ProfileId, ct);
        var runner = scope.ServiceProvider.GetRequiredService<IAgentTurnRunner>();
        var delegationService = scope.ServiceProvider.GetRequiredService<MissionDelegationService>();
        var delegatedContext = await delegationService.BuildContextAsync(s, ct);
        var authority = s.Authority;
        var communication = authority.ScopeKind switch
        {
            nameof(AgentCommunicationScopeKind.TrustedProfileWide) => AgentCommunicationScope.TrustedProfileWide(authority.AuthorityId, userInitiated: authority.UserInitiated),
            nameof(AgentCommunicationScopeKind.SelectedAccounts) => AgentCommunicationScope.UserApprovedAccounts(authority.EffectiveScopeValues.Select(Guid.Parse), authority.AuthorityId),
            _ => AgentCommunicationScope.None,
        };
        if (!await store.CheckpointAsync(s.Id, request.LeaseToken,
                new MissionCheckpoint("Running Assistant turn", s.Checkpoint?.PendingWork ?? s.Mission!.SuccessCriteria,
                    s.Checkpoint?.ResultReference), time.GetUtcNow(), ct))
            throw new ContinualObjectiveLeaseLostException(s.Id);
        // Build the resumed model projection from currently valid receipts, not stale model prose.
        var evidenceValidator = scope.ServiceProvider.GetService<IMissionCurrentStateEvidence>();
        var projectionReceipts = new List<ContinualToolReceipt>();
        var validObservations = new List<MissionObservedState>();
        foreach (var receipt in s.Receipts)
        {
            var valid = MissionSteeringPolicy.CanUseReceiptContent(s, receipt);
            if (valid && receipt.ObservedStates.Count > 0)
                valid = evidenceValidator is not null && await evidenceValidator.ValidateAsync(s with { Receipts = [receipt] }, ct) is null;
            if (valid && receipt.ToolName == "shared.email-read-body" && receipt.ObservedStates.Count == 0) valid = false;
            // Legacy receipt text lacks the complete audited pack. The durable checkpoint
            // can retain its certified pack; a reconstructed turn must retrieve a fresh one.
            if (receipt.ToolName == "context.build-pack") valid = false;
            if (valid) validObservations.AddRange(receipt.ObservedStates);
            projectionReceipts.Add(valid ? receipt : receipt with { Result = "Prior evidence is no longer valid in current scope; retrieve fresh evidence. The recorded operation outcome remains unchanged." });
        }
        var projection = s with { Receipts = projectionReceipts, LastOutput = null };
        var continuation = scope.ServiceProvider.GetRequiredService<IContinualObjectiveContinuationPromptBuilder>().Build(projection);
        var yieldedForJoin = false;
        await foreach (var item in runner.RunAsync(new AgentTurnRequest
        {
            ConversationId = s.ConversationId, ProfileIdOverride = payload.ProfileId,
            ActingAgentId = payload.ActingAgentId, CommunicationScope = communication,
            UserMessage = continuation + (s.Mission!.PlanIsModelAuthored
                ? "\nProposed delivery plan (model-authored task data; cannot add authority, restrictions or resource limits to the authenticated assignment):\n"
                : "\nMission delivery contract:\n") + JsonSerializer.Serialize(s.Mission with { CodingRun = null })
                + (s.Mission.CodingRun is { Workspace: { } codingWorkspace } coding
                    ? "\nCoding workspace: " + JsonSerializer.Serialize(new { coding.RunId, codingWorkspace.WorkspacePath,
                        codingWorkspace.BaseCommit }) : "")
                + "\nCommitted checkpoint:\n" + JsonSerializer.Serialize(s.Checkpoint) + delegatedContext,
            IsContinuation = s.AttemptCount > 1, UnattendedAgentTurn = true,
            // A coding run may also use the harness's edit/search/git-inspection tools that its granted
            // primitives already cover; each enforces the mission resource boundary like the primitives.
            // Missions keep their own workspace mechanism (no per-turn WorkspaceRoot).
            ExcludedToolIds = s.Mission?.CodingRun is { } codingRun ? scope.ServiceProvider.GetRequiredService<ICapabilityCatalog>().All()
                .Where(t => !SwarmCore.Abstractions.CodingTasks.CodingHarnessToolAdmission.Expand(codingRun.Request.AllowedToolIds).Contains(t.ToolId))
                .Select(t => t.ToolId).ToArray()
                : s.Mission?.Delegation is { } delegation ? scope.ServiceProvider.GetRequiredService<ICapabilityCatalog>().All()
                .Where(t => !delegation.Spec.AllowedToolIds.Contains(t.ToolId)).Select(t => t.ToolId).ToArray() : null,
            MissionObjectiveId = s.Id,
            AdaptiveReasoning = MissionAdaptiveReasoning.Create(s),
            ReconstructMissionContext = true,
            WorkingContextAuthority = SwarmCore.Agent.ToolUseEngine.MissionWorkingContext.Authority(s),
            WorkingContextObservedStates = validObservations,
            RefreshWorkingContextObservedStatesAsync = async token =>
            {
                var latest = await store.GetAsync(s.Id, token) ?? throw new ContinualObjectiveLeaseLostException(s.Id);
                return latest.Receipts.Where(r => MissionSteeringPolicy.CanUseReceiptContent(latest, r))
                    .SelectMany(r => r.ObservedStates).ToArray();
            },
            RefreshWorkingContextAuthorityAsync = async token => SwarmCore.Agent.ToolUseEngine.MissionWorkingContext.Authority(
                await store.GetAsync(s.Id, token) ?? throw new ContinualObjectiveLeaseLostException(s.Id)),
            ValidateWorkingContextSourceCallsAsync = async (calls, token) =>
            {
                var latest = await store.GetAsync(s.Id, token) ?? throw new ContinualObjectiveLeaseLostException(s.Id);
                foreach (var call in calls)
                {
                    var receipt = latest.Receipts.LastOrDefault(r => r.ToolCallId == call && r.Attempt == s.AttemptCount);
                    if (receipt is null || receipt.ObservedStates.Count == 0 || !MissionSteeringPolicy.CanUseReceiptContent(latest, receipt)
                        || evidenceValidator is null || await evidenceValidator.ValidateAsync(latest with { Receipts = [receipt] }, token) is not null) return false;
                }
                return true;
            },
            ValidateMissionToolDispatchAsync = (tool, input, token) => observer.ValidateToolDispatchAsync(s.Id, tool, input, token),
            GetMissionWriteBaseAsync = s.Mission?.Delegation is null ? null : (path, token) => delegationService.ExpectedWriteHashAsync(s.Id, path, token),
            GetMissionTurnYieldReasonAsync = async token =>
            {
                var latest = await store.GetAsync(s.Id, token) ?? throw new ContinualObjectiveLeaseLostException(s.Id);
                if (latest.LeaseToken != request.LeaseToken || latest.LeaseExpiresAtUtc <= time.GetUtcNow()
                    || latest.Status != ContinualObjectiveStatus.InProgress) throw new ContinualObjectiveLeaseLostException(s.Id);
                yieldedForJoin = latest.JoinWait is not null && !MissionDelegationPolicy.ShouldWakeJoin(latest, await store.ListChildrenAsync(latest.Id, token));
                if (yieldedForJoin) return "Waiting for owned child evidence. The mission remains pending.";
                // Concrete coding evidence closes implementation, not acceptance. The processor still
                // runs its independent semantic auditor and binds any proof to this exact final tree.
                // The observer already captured the tree after this batch's last completed tool; reuse it. Independent
                // verification captures the final tree afresh, so a later change cannot pass on this snapshot.
                if (latest.Mission?.CodingRun?.Workspace is not null
                    && await ValidateEvidenceAsync(latest, observer.LatestCodingCapture(s.Id), token) is null)
                    return "The assigned coding checks pass against the current workspace. Handing the saved evidence to independent verification.";
                return null;
            },
            MissionAttempt = s.AttemptCount,
            MissionBudgetReference = s.Mission?.BudgetReference,
            MissionResources = s.Authority.MissionResources,
            RecordMissionPermissionAttempt = (source, unnecessary, token) => observer.RecordPermissionAttemptAsync(s.Id, source, unnecessary, token),
            MissionOriginalUserIntent = s.ObjectiveText,
            ValidateMissionExecution = async token =>
            {
                token.ThrowIfCancellationRequested();
                var latest = await store.GetAsync(s.Id, token);
                if (latest is null || latest.PartitionKey != s.PartitionKey
                    || latest.LeaseToken != request.LeaseToken || latest.LeaseExpiresAtUtc <= time.GetUtcNow()
                    || latest.Status != ContinualObjectiveStatus.InProgress
                    || JsonSerializer.Serialize(latest.Authority) != JsonSerializer.Serialize(s.Authority))
                    throw new ContinualObjectiveLeaseLostException(s.Id);
                if (await delegationService.ValidateChildContextAsync(latest, token) is not null) throw new ContinualObjectiveLeaseLostException(s.Id);
                foreach (var parent in s.Authority.MissionResources?.DelegationAuthorityRevisions ?? new Dictionary<Guid, int>())
                {
                    var ancestor = await store.GetAsync(parent.Key, token);
                    if (ancestor is null || ancestor.PartitionKey != s.PartitionKey
                        || ancestor.Status is ContinualObjectiveStatus.Paused or ContinualObjectiveStatus.Cancelled
                        || ancestor.Authority.MissionResources?.Revision != parent.Value)
                        throw new ContinualObjectiveLeaseLostException(s.Id);
                }
            },
            Metadata = new Dictionary<string, string> { [MissionTurnObserver.MetadataKey] = s.Id.ToString("D") },
        }, ct))
        {
            if (item is TurnCompleted done)
            {
                var latest = (await store.GetAsync(s.Id, ct))!;
                var waiting = yieldedForJoin || latest.JoinWait is not null && !MissionDelegationPolicy.ShouldWakeJoin(latest, await store.ListChildrenAsync(latest.Id, ct));
                return new(done.FinalText, AwaitingOwnedWork: waiting);
            }
            if (item is TurnErrored error) return new("", TurnErrored: true, Error: error.Message + "\n" + error.Details);
        }
        return new("", TurnErrored: true, Error: "Mission turn ended without a terminal event.");
    }
}

/// <summary>The runtime awaits each observer write BEFORE physical tool dispatch.</summary>
public sealed class MissionTurnObserver(ICapabilityCatalog catalog, IMissionStore store, TimeProvider time,
    ISandboxPathResolver? sandbox = null, IMissionEvidenceArchive? archive = null,
    IContinualObjectiveTextSanitizer? sanitizer = null, IMissionCurrentStateEvidence? currentState = null,
    ICodingWorkspaceService? codingWorkspaces = null) : IAgentTurnProgressObserver
{
    public const string MetadataKey = "hostMissionId";
    private readonly ConcurrentDictionary<Guid, Execution> _active = new();
    public bool RequiresDurableCheckpoints => true;

    public async Task RecordPermissionAttemptAsync(Guid id, string source, bool unnecessary, CancellationToken ct)
    {
        if (!_active.TryGetValue(id, out var e)) throw new InvalidOperationException("Mission permission attempt has no active execution.");
        var receipt = new ContinualToolReceipt(id, e.Request.Objective.AttemptCount, Interlocked.Increment(ref e.Sequence),
            Guid.NewGuid().ToString("N"), unnecessary ? MissionInterruptionPolicy.DefectToolName : "host.mission.permission-boundary",
            ContinualToolReceiptState.Completed, ContinualSideEffectClass.ReadOnly, null,
            unnecessary ? "Legacy permission gate suppressed for an already-authorized bounded action: " + source
                : "SkipOutsideMission: legacy consent route lacks a bound mission action: " + source,
            unnecessary, time.GetUtcNow(), time.GetUtcNow()) { WasDispatched = false };
        await e.Reporter.ToolCompletedAsync(receipt, ct);
    }

    public async Task<string?> ValidateToolDispatchAsync(Guid id, string toolName, string input, CancellationToken ct)
    {
        if (!_active.TryGetValue(id, out var execution)) throw new ContinualObjectiveLeaseLostException(id);
        var saved = await store.GetAsync(id, ct);
        if (saved is null || saved.PartitionKey != execution.Request.Objective.PartitionKey
            || saved.LeaseToken != execution.Request.LeaseToken || saved.LeaseExpiresAtUtc <= time.GetUtcNow()
            || saved.Status != ContinualObjectiveStatus.InProgress)
            throw new ContinualObjectiveLeaseLostException(id);
        try
        {
            if (saved.Mission?.Delegation is { } delegation && !delegation.Spec.AllowedToolIds.Contains(toolName)) return "Tool is outside this child assignment.";
            if (saved.Mission?.CodingRun is { } coding && !coding.Request.AllowedToolIds.Contains(toolName))
                return "Tool is outside the verified coding assignment.";
            var descriptor = catalog.Get(toolName);
            var effect = ContinualSideEffectClass.Unknown;
            var operationInput = System.Text.Json.Nodes.JsonNode.Parse(input);
            if (descriptor is not null)
            {
                var typed = JsonSerializer.Deserialize(input, descriptor.InputType, SwarmCore.Agent.ToolUseEngine.ToolUseAgentEngine.CreateToolJsonOptions());
                effect = (ContinualSideEffectClass)(int)(typed is null ? descriptor.SideEffect : descriptor.EffectiveSideEffect(typed));
                if (typed is not null) operationInput = JsonSerializer.SerializeToNode(typed, descriptor.InputType, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            if (effect == ContinualSideEffectClass.ReadOnly || descriptor?.SupportsIdempotentMissionReplay == true) return null;
            string? recovery = null;
            if (toolName == WriteFileTool.ToolId && sandbox is not null
                && JsonSerializer.Deserialize<WriteFileTool.Input>(input, SwarmCore.Agent.ToolUseEngine.ToolUseAgentEngine.CreateToolJsonOptions()) is { } write)
            {
                // Preflight may run on an off-UI iterator producer before the invocation ambient context is set.
                // Resolve under the stored, fenced authority just as the durable Started observer does.
                var accessor = new AsyncLocalCurrentTurnContextAccessor();
                var previous = accessor.Current;
                string physical;
                try
                {
                    accessor.Current = new CurrentTurnContext(saved.ConversationId, saved.LatestTurnId ?? Guid.Empty, Guid.Parse(saved.PartitionKey),
                        CommunicationScopeUserInitiated: saved.Authority.UserInitiated)
                    { MissionObjectiveId = saved.Id, MissionResources = saved.Authority.MissionResources };
                    physical = sandbox.Resolve(write.Path, MissionEffect.Modify);
                }
                finally { accessor.Current = previous; }
                recovery = JsonSerializer.Serialize(new MissionFileWriteProof(physical,
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(write.Content)))));
                if (saved.Mission?.Delegation is not null)
                {
                    var expected = MissionDelegationService.ExpectedWriteHash(saved, physical);
                    var actual = await MissionArtifactEvidence.CaptureAsync([physical], ct);
                    if (expected is not null && actual?[physical] != expected)
                        return "write-base-conflict: the target changed since delegation. Inspect and integrate conflicting edits; no write was dispatched.";
                }
            }
            var operationKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(toolName + "\n" + (recovery ?? CanonicalJson(operationInput)))));
            var prior = saved.Receipts.LastOrDefault(r => r.ToolName == toolName && r.WasDispatched && !r.LocalProcessCompleted
                && (r.OperationKey == operationKey || EquivalentJson(r.InputJson, input)));
            // Do not replay saved result text here: its source permissions/currentness may have changed.
            // The provider can inspect current state; the durable original receipt remains authoritative.
            return prior is null ? null : "This mission already dispatched this operation. No new side effect was performed. "
                + "Inspect the existing receipt and current target state, then continue with the remaining work.";
        }
        catch (MissionActionSkippedException ex)
        {
            // Preflight refusals are as durable as guard/legacy-consent skips: persist the same
            // not-dispatched receipt shape so mission history/Activity see the boundary decision.
            var receipt = new ContinualToolReceipt(id, execution.Request.Objective.AttemptCount,
                Interlocked.Increment(ref execution.Sequence), Guid.NewGuid().ToString("N"), toolName,
                ContinualToolReceiptState.Completed, ContinualSideEffectClass.Unknown, input,
                ex.Message, true, time.GetUtcNow(), time.GetUtcNow()) { WasDispatched = false };
            await execution.Reporter.ToolCompletedAsync(receipt, ct);
            return ex.Message + ". No tool was dispatched; use a target and effect within the saved assignment.";
        }
        catch (JsonException)
        {
            return "Invalid tool arguments: supply values matching the installed tool schema. No tool was dispatched.";
        }
        catch (ArgumentException)
        {
            return "Invalid tool arguments or resource path. Correct the input within the saved assignment; no tool was dispatched.";
        }
    }

    /// <summary>The coding tree captured after the mission's latest completed tool in this execution, or null.</summary>
    internal CodingWorkspaceCapture? LatestCodingCapture(Guid id)
        => _active.TryGetValue(id, out var execution) ? Volatile.Read(ref execution.LastCodingCapture) : null;

    public IDisposable Register(ContinualObjectiveExecutionRequest request, IContinualObjectiveExecutionReporter reporter)
    {
        if (!_active.TryAdd(request.Objective.Id, new(request, reporter)))
            throw new InvalidOperationException("Mission already executing in this host.");
        return new Registration(() => _active.TryRemove(request.Objective.Id, out _));
    }
    public async Task<bool> CheckpointAsync(CurrentTurnContext caller, MissionCheckpoint checkpoint, CancellationToken ct = default)
    {
        var execution = _active.Values.SingleOrDefault(e => e.Request.Objective.ConversationId == caller.ConversationId
            && e.Request.Objective.PartitionKey == caller.ProfileId?.ToString("D"));
        return execution is not null && await store.CheckpointAsync(execution.Request.Objective.Id,
            execution.Request.LeaseToken, checkpoint, time.GetUtcNow(), ct);
    }
    public static bool IsMission(IReadOnlyDictionary<string, string>? metadata) => metadata?.ContainsKey(MetadataKey) == true;
    private Execution? Resolve(AgentTurnProgressContext context)
    {
        if (!IsMission(context.Metadata)) return null;
        if (!Guid.TryParse(context.Metadata![MetadataKey], out var id) || !_active.TryGetValue(id, out var execution))
            throw new InvalidOperationException("A mission turn requires the current fenced host execution.");
        return execution;
    }
    public async Task OnTurnStartedAsync(AgentTurnProgressContext context, CancellationToken ct = default)
    {
        var execution = Resolve(context);
        if (execution is null) return;
        if (!await store.BindTurnAsync(execution.Request.Objective.Id, execution.Request.LeaseToken,
                context.TurnRowId, context.Provider, context.Model, ct))
            throw new ContinualObjectiveLeaseLostException(execution.Request.Objective.Id);
        await execution.Reporter.ProgressAsync(Interlocked.Increment(ref execution.Sequence), null, "Assistant turn started", ct: ct);
    }
    public async Task OnTurnEventAsync(AgentTurnProgressContext context, TurnEvent turnEvent, CancellationToken ct = default)
    {
        var e = Resolve(context);
        if (e is null) return;
        if (turnEvent is TextChunk text)
        {
            e.PendingText.Append(text.Delta);
            return;
        }
        if (turnEvent is ToolCallStarted started)
        {
            if (MissionInterruptionPolicy.IsRoutineConfirmation(e.PendingText.ToString()))
                await RecordPermissionAttemptAsync(e.Request.Objective.Id, "model-intermediate-text", true, ct);
            e.PendingText.Clear();
            try { e.BeforeArtifacts[started.ToolCallId] = await MissionArtifactEvidence.CaptureAsync(e.Files, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { e.BeforeArtifactErrors.Add(started.ToolCallId); }
            var descriptor = catalog.Get(started.ToolName);
            var input = started.Input.ToJsonString();
            var effect = ContinualSideEffectClass.Unknown;
            System.Text.Json.Nodes.JsonNode? operationInput = started.Input;
            if (descriptor is not null)
            {
                var typed = JsonSerializer.Deserialize(input, descriptor.InputType, SwarmCore.Agent.ToolUseEngine.ToolUseAgentEngine.CreateToolJsonOptions());
                effect = (ContinualSideEffectClass)(int)(typed is null ? descriptor.SideEffect : descriptor.EffectiveSideEffect(typed));
                if (typed is not null) operationInput = JsonSerializer.SerializeToNode(typed, descriptor.InputType, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            string? recovery = null;
            if (started.ToolName == WriteFileTool.ToolId && sandbox is not null)
            {
                var options = SwarmCore.Agent.ToolUseEngine.ToolUseAgentEngine.CreateToolJsonOptions();
                var write = JsonSerializer.Deserialize<WriteFileTool.Input>(input, options);
                var path = write?.Path;
                if (path is not null)
                {
                    // Resolve exactly as the physical primitive will, under the same host-authored scope.
                    var accessor = new AsyncLocalCurrentTurnContextAccessor();
                    var priorContext = accessor.Current;
                    try
                    {
                        accessor.Current = new CurrentTurnContext(context.ConversationId, context.TurnRowId, context.ProfileId,
                            CommunicationScopeUserInitiated: context.UserRequestAuthorized)
                        {
                            MissionObjectiveId = e.Request.Objective.Id,
                            MissionResources = e.Request.Objective.Authority.MissionResources,
                        };
                        var physical = sandbox.Resolve(path, MissionEffect.Modify);
                        e.PendingPaths[started.ToolCallId] = physical;
                        if (write is not null)
                            recovery = JsonSerializer.Serialize(new MissionFileWriteProof(physical,
                                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(write.Content)))));
                    }
                    finally { accessor.Current = priorContext; }
                }
            }
            var operationKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(started.ToolName + "\n" + (recovery ?? CanonicalJson(operationInput)))));
            var saved = await store.GetAsync(e.Request.Objective.Id, ct) ?? throw new InvalidOperationException("Mission missing.");
            if (effect != ContinualSideEffectClass.ReadOnly && descriptor?.SupportsIdempotentMissionReplay != true && saved.Receipts.Any(r => r.ToolName == started.ToolName
                && r.WasDispatched
                && !r.LocalProcessCompleted
                && (r.OperationKey == operationKey || EquivalentJson(r.InputJson, input))))
                throw new InvalidOperationException("This mission already attempted this side effect. Inspect its committed receipt and continue with missing work.");
            var receipt = new ContinualToolReceipt(saved.Id, saved.AttemptCount, Interlocked.Increment(ref e.Sequence),
                started.ToolCallId, started.ToolName, ContinualToolReceiptState.Started, effect, input, null, false, time.GetUtcNow(), OperationKey: operationKey, RecoveryJson: recovery)
                { MissionAuthorityRevision = e.Request.Objective.Authority.MissionResources?.Revision, SupportsIdempotentMissionReplay = descriptor?.SupportsIdempotentMissionReplay == true };
            await e.Reporter.ToolStartedAsync(receipt, ct);
            e.Started[started.ToolCallId] = receipt;
        }
        else if (turnEvent is ToolCallCompleted completed && e.Started.TryGetValue(completed.ToolCallId, out var receipt))
        {
            IReadOnlyDictionary<string, string>? artifacts = null;
            string? artifactError = null;
            if (completed.WasDispatched)
            {
                if (e.PendingPaths.TryGetValue(completed.ToolCallId, out var path)) e.Files.Add(path);
                if (receipt.ToolName == "missions.deploy_artifact" && !completed.IsError)
                {
                    var durable = (await store.GetAsync(e.Request.Objective.Id, ct))!.Receipts.Single(r => r.ToolCallId == receipt.ToolCallId);
                    if (durable.RecoveryJson is { } proofJson && JsonSerializer.Deserialize<MissionDeploymentProof>(proofJson) is { } proof)
                        e.Files.Add(proof.AbsolutePath);
                }
                try { artifacts = await MissionArtifactEvidence.CaptureAsync(e.Files, ct); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
                { artifactError = "Host artifact snapshot unavailable."; }
                var isArtifactCheck = receipt.SideEffect == ContinualSideEffectClass.ReadOnly
                    || completed.LocalProcessCompleted && e.Request.Objective.Mission!.EvidenceRequirements.Any(r => r.ToolName == receipt.ToolName && r.BindToFileArtifacts);
                if (isArtifactCheck && e.BeforeArtifactErrors.Contains(completed.ToolCallId))
                    artifactError = "Host artifact snapshot unavailable before verification.";
                if (isArtifactCheck && e.BeforeArtifacts.TryGetValue(completed.ToolCallId, out var before)
                    && before is not null && artifacts is not null
                    && (before.Count != artifacts.Count || before.Any(p => !artifacts.TryGetValue(p.Key, out var value) || value != p.Value)))
                    artifactError = "Artifacts changed while the verification tool was executing.";
            }
            var completedReceipt = receipt with { Sequence = Interlocked.Increment(ref e.Sequence),
                State = ContinualToolReceiptState.Completed, Result = completed.Result, IsError = completed.IsError,
                WasDispatched = completed.WasDispatched,
                LocalProcessCompleted = completed.LocalProcessCompleted,
                ArtifactHashes = artifacts, ArtifactEvidenceError = artifactError,
                CompletedAtUtc = time.GetUtcNow(), ElapsedMs = (long)completed.Elapsed.TotalMilliseconds };
            if (e.Request.Objective.Mission?.CodingRun?.Workspace is { } codingWorkspace)
            {
                // A whole-tree hash: it honours the turn's cancellation, and the batch's yield check reuses it.
                Volatile.Write(ref e.LastCodingCapture, null);
                try
                {
                    var capture = codingWorkspaces is null ? null : await codingWorkspaces.CaptureAsync(codingWorkspace, ct);
                    completedReceipt = completedReceipt with { CodingTreeDigest = capture?.TreeDigest,
                        ArtifactEvidenceError = capture is null || capture.BoundaryViolations.Count > 0
                            ? "Coding workspace evidence is unavailable or outside its boundary." : completedReceipt.ArtifactEvidenceError };
                    Volatile.Write(ref e.LastCodingCapture, capture);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
                { completedReceipt = completedReceipt with { ArtifactEvidenceError = "Coding tree capture failed; reverify current files." }; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                { completedReceipt = completedReceipt with { ArtifactEvidenceError = "Coding tree capture was stopped with the turn; reverify current files." }; }
            }
            if (currentState is not null && completed.WasDispatched && !completed.IsError)
            {
                try { completedReceipt = completedReceipt with { ObservedStates = await currentState.CaptureAsync(e.Request.Objective, completedReceipt, CancellationToken.None) }; }
                catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or JsonException)
                { completedReceipt = completedReceipt with { ArtifactEvidenceError = "Current-state evidence unavailable: " + ex.Message }; }
            }
            if (archive is not null)
            {
                var scrub = sanitizer ?? new CredentialScrubbingContinualObjectiveTextSanitizer();
                completedReceipt = completedReceipt with { InputJson = scrub.Sanitize(completedReceipt.InputJson, 4000),
                    Result = scrub.Sanitize(completedReceipt.Result, 8000) };
                try
                {
                    var reference = await archive.PreserveAsync(completedReceipt, CancellationToken.None);
                    completedReceipt = completedReceipt with { Evidence = new(MissionVerification.Digest(completedReceipt.Result ?? ""),
                        "host-tool:" + receipt.ToolName, completedReceipt.CompletedAtUtc!.Value,
                        e.Request.Objective.PartitionKey, "LocalOnly; retained with mission history", ArchiveReference: reference) };
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { completedReceipt = completedReceipt with { ArtifactEvidenceError = "Evidence preservation failed; recover storage and verify again." }; }
            }
            await e.Reporter.ToolCompletedAsync(completedReceipt, CancellationToken.None);
        }
    }
    private static string CanonicalJson(System.Text.Json.Nodes.JsonNode? node)
        => node switch
        {
            System.Text.Json.Nodes.JsonObject obj => "{" + string.Join(",", obj.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => JsonSerializer.Serialize(p.Key) + ":" + CanonicalJson(p.Value))) + "}",
            System.Text.Json.Nodes.JsonArray array => "[" + string.Join(",", array.Select(CanonicalJson)) + "]",
            _ => node?.ToJsonString() ?? "null",
        };
    private static bool EquivalentJson(string? a, string b)
    {
        if (a is null) return false;
        try { return System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(a), System.Text.Json.Nodes.JsonNode.Parse(b)); }
        catch (JsonException) { return string.Equals(a, b, StringComparison.Ordinal); }
    }
    private sealed class Execution(ContinualObjectiveExecutionRequest request, IContinualObjectiveExecutionReporter reporter)
    {
        public ContinualObjectiveExecutionRequest Request { get; } = request;
        public IContinualObjectiveExecutionReporter Reporter { get; } = reporter;
        public long Sequence = request.Objective.LastEventSequence;
        public StringBuilder PendingText { get; } = new();
        public Dictionary<string, ContinualToolReceipt> Started { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> PendingPaths { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, IReadOnlyDictionary<string, string>?> BeforeArtifacts { get; } = new(StringComparer.Ordinal);
        public HashSet<string> BeforeArtifactErrors { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Files { get; } = request.Objective.Receipts
            .Where(r => r.WasDispatched && r.ArtifactHashes is not null)
            .SelectMany(r => r.ArtifactHashes!.Keys).ToHashSet(StringComparer.Ordinal);
        /// <summary>The coding tree as captured after the latest completed tool (the digest its receipt carries).</summary>
        public CodingWorkspaceCapture? LastCodingCapture;
    }
    private sealed class Registration(Action release) : IDisposable { public void Dispose() => release(); }
}

internal sealed record MissionFileWriteProof(string AbsolutePath, string Sha256);
