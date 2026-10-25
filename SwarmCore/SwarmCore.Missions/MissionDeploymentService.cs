using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using SwarmCore.Agent.Continual;
using SwarmCore.Agent.Runtime;

namespace SwarmCore.Missions;

/// <summary>Reviewed host development environments. These settings contain paths, never credentials.</summary>
public sealed class MissionDeploymentOptions
{
    public Dictionary<string, string> DevelopmentDirectories { get; set; } = new(StringComparer.Ordinal);
}

public sealed record MissionDeployInput(string ArtifactPath, string? TargetName = null);
public sealed record MissionDeploymentProof(string Target, string Root, string AbsolutePath, string Sha256);
public sealed record MissionDeployResult(bool Deployed, string Detail, string? Sha256 = null) : SwarmCore.Agent.Catalog.IToolExecutionEvidence
{
    [System.Text.Json.Serialization.JsonIgnore] public bool Succeeded => Deployed;
    [System.Text.Json.Serialization.JsonIgnore] public bool LocalProcessCompleted => false;
    [System.Text.Json.Serialization.JsonIgnore] public bool NoPhysicalAction => !Deployed;
}

/// <summary>Concrete file-based development deployment adapter. No shell or general production-data authority.</summary>
public sealed class MissionDeploymentService(IMissionStore store, ISandboxPathResolver paths, IOptions<MissionDeploymentOptions> options)
{
    public async Task<MissionDeployResult> DeployAsync(CurrentTurnContext caller, MissionDeployInput input, CancellationToken ct)
    {
        if (caller.MissionObjectiveId is not { } id || caller.ProfileId is not { } profile)
            throw new MissionActionSkippedException("Deployment requires an assigned mission.");
        var mission = await store.GetAsync(id, ct) ?? throw new MissionActionSkippedException("Mission missing.");
        // The engine persists this invocation's intent before entering the adapter.
        // It is not an earlier uncertain operation and must not invalidate preparation.
        if (SwarmCore.Agent.Catalog.ToolProgressContext.CurrentToolCallId is { } callId)
            mission = mission with { Receipts = mission.Receipts.Where(r => r.ToolCallId != callId).ToArray() };
        var authority = mission.Authority.MissionResources;
        if (mission.PartitionKey != profile.ToString("D") || authority is null) throw new MissionActionSkippedException("partition-mismatch");
        var candidates = options.Value.DevelopmentDirectories.Keys.Where(name =>
            (authority with { Grants = [.. authority.Grants, new(new(MissionResourceKind.Environment, name), MissionEffect.Deploy, "candidate-exclusion-check", true)] })
                .Evaluate(new(new(MissionResourceKind.Environment, name), MissionEffect.Deploy, mission.PartitionKey)).Allowed)
            .Order(StringComparer.Ordinal).ToArray();
        var authorized = candidates.Where(name => authority.Evaluate(new(new(MissionResourceKind.Environment, name), MissionEffect.Deploy, mission.PartitionKey)).Allowed).ToArray();
        var target = input.TargetName ?? (authorized.Length == 1 ? authorized[0] : candidates.Length == 1 ? candidates[0] : null);
        if (authority.DeploymentRequestEvidence is null && authorized.Length == 0)
            throw new MissionActionSkippedException("Coding does not imply deployment.");
        var artifact = paths.Resolve(input.ArtifactPath);
        var preparation = mission with { Mission = mission.Mission! with
            { EvidenceRequirements = mission.Mission!.EvidenceRequirements.Where(e => e.ToolName != "missions.deploy_artifact").ToArray() } };
        if (preparation.Mission.EvidenceRequirements.Count == 0 || MissionTransitions.MissingEvidence(preparation) is { })
            return new(false, "Finish preparation and verify the artifact before deployment or target clarification.");
        if (await MissionArtifactEvidence.ValidateAsync(mission, ct) is { } stale) return new(false, stale);
        if (!File.Exists(artifact)) return new(false, "Inspect and build the requested deployment artifact first.");
        if (authorized.Length == 0 && candidates.Length > 1)
        {
            var clarification = new MissionTargetClarification("The artifact is prepared and checked. Which development environment should receive it?",
                candidates, authority.Revision);
            if (!await store.SaveTargetClarificationAsync(id, mission.LeaseToken!, clarification, ct))
                throw new ContinualObjectiveLeaseLostException(id);
            return new(false, clarification.Question);
        }
        if (target is null || !candidates.Contains(target, StringComparer.Ordinal)
            || (authorized.Length > 0 && !authorized.Contains(target, StringComparer.Ordinal)))
            throw new MissionActionSkippedException("The proposed target is outside the assigned development environment.");
        // A sole configured development environment resolves an otherwise unnamed target.
        // Explicit exclusions still win over both inferred and resolved deployment authority.
        var effective = authority;
        if (authorized.Length == 0 && candidates.Length == 1)
            effective = authority with { Grants = [.. authority.Grants, new(new(MissionResourceKind.Environment, target), MissionEffect.Deploy, authority.DeploymentRequestEvidence!, true)] };
        if (!effective.Evaluate(new(new(MissionResourceKind.Environment, target), MissionEffect.Deploy, mission.PartitionKey)).Allowed)
            throw new MissionActionSkippedException("explicit-deployment-exclusion");
        await MissionPrimitiveFence.ValidateAsync(ct);
        var root = SandboxPathResolver.ResolvePhysicalPath(options.Value.DevelopmentDirectories[target]);
        if (!authority.EnvironmentBindings.TryGetValue(target, out var boundRoot) || boundRoot != root)
            throw new MissionActionSkippedException("The configured environment changed after assignment; inspect its binding before deployment.");
        var destination = SandboxPathResolver.ResolvePhysicalPath(Path.Combine(root, Path.GetFileName(artifact)));
        if (Path.GetDirectoryName(destination) != root) throw new MissionActionSkippedException("Deployment destination escaped its configured directory.");
        var bytes = await File.ReadAllBytesAsync(artifact, ct);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (await MissionArtifactEvidence.ValidateAsync(mission, ct) is { } changed) return new(false, changed);
        var operation = SwarmCore.Agent.Catalog.ToolProgressContext.CurrentToolCallId
            ?? throw new MissionActionSkippedException("Deployment requires a durable operation intent.");
        if (!await store.AttachRecoveryProofAsync(id, mission.LeaseToken!, operation,
            System.Text.Json.JsonSerializer.Serialize(new MissionDeploymentProof(target, root, destination, hash)), ct))
            throw new ContinualObjectiveLeaseLostException(id);
        await MissionPrimitiveFence.ValidateAsync(ct);
        var temporary = Path.Combine(root, ".mission-" + id.ToString("N") + "-" + operation + ".tmp");
        try
        {
            // No task-resource mutation before all preparation checks and the durable intent.
            Directory.CreateDirectory(root);
            await File.WriteAllBytesAsync(temporary, bytes, ct);
            await MissionPrimitiveFence.ValidateAsync(ct);
            File.Move(temporary, destination, overwrite: true);
        }
        catch (MissionActionSkippedException ex)
        {
            // The directory or temporary file may already exist. A revoked fence cannot
            // retroactively turn this invocation into a never-dispatched action.
            throw new IOException("Deployment stopped after local preparation started; inspect its saved intent before retrying.", ex);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        var actual = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(destination, ct)));
        if (actual != hash) throw new IOException("Deployed artifact verification failed.");
        return new(true, "Verified development deployment to " + target, actual);
    }
}
