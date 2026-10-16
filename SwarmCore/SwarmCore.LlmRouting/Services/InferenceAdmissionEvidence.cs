using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;
using System.Collections.Frozen;
using SwarmCore.LlmRouting.ProviderPacks;
using SwarmCore.LlmRouting.Providers;

namespace SwarmCore.LlmRouting.Services;

/// <summary>Host-authored evidence projection, stored outside every model-visible workspace.</summary>
public sealed record InferenceAdmissionEvidence
{
    public int SchemaVersion { get; init; } = 2;
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public required string ExecutablePath { get; init; }
    public required string ExecutableSha256 { get; init; }
    public required string ProviderPackSha256 { get; init; }
    public required IReadOnlyList<string> InferenceArgumentTemplate { get; init; }
    public required string SourceEvidencePath { get; init; }
    public required string SourceEvidenceSha256 { get; init; }
    public required string SourceEvidenceStatus { get; init; }
    public DateTimeOffset VerifiedAtUtc { get; init; }
    public bool NativeToolSurfacesDisabled { get; init; }
    public bool InferenceVerified { get; init; }
    public string CliHomePath { get; init; } = "";
    public string CliHomeEnvironmentVariable { get; init; } = "";
    public IReadOnlyList<string> ConfigurationInventoryRoots { get; init; } = [];
    /// <summary>Reviewed non-secret settings/rules/plugins only; never auth.json or a secret-derived hash.</summary>
    public IReadOnlyDictionary<string, string> NonSecretConfigurationHashes { get; init; } = new Dictionary<string, string>();
    public bool AuthenticationFilePresent { get; init; }
    public string AuthenticationStorageMode { get; init; } = "";
    public string AuthenticationHealthEvidencePath { get; init; } = "";
    public string AuthenticationHealthEvidenceSha256 { get; init; } = "";
}

/// <summary>
/// Checks the actual process request, rather than a selected model label. It deliberately
/// excludes catalog/app-server discovery and changes to argv, binary, pack or source proof.
/// It is a bounded observed-admission contract, not a formal operating-system sandbox claim.
/// </summary>
public sealed class InferenceAdmissionValidator(string proofPath, string proofSha256,
    string provider, string model, string workingDirectory, TimeSpan? maximumAge = null) : IInferenceAdmission
{
    void IInferenceAdmission.Validate() => ValidateEvidence();
    public InferenceAdmissionIdentity Identity => new(provider,model,proofSha256);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public InferenceAdmissionEvidence ValidateEvidence()
    {
        if (!Path.IsPathFullyQualified(proofPath) || !MatchesHash(proofPath, proofSha256))
            throw Denied("Evidence projection is missing or changed.");
        var evidence = JsonSerializer.Deserialize<InferenceAdmissionEvidence>(File.ReadAllBytes(proofPath), Json)
            ?? throw Denied("Evidence projection is invalid.");
        var pack = ProviderPackCatalog.GetRequired(provider);
        if (evidence.SchemaVersion != 2 || evidence.Provider != provider || evidence.Model != model
            || evidence.SourceEvidenceStatus != "PASS_INFERENCE_ONLY_CONTAINMENT"
            || !evidence.NativeToolSurfacesDisabled || !evidence.InferenceVerified
            || !Path.IsPathFullyQualified(evidence.ExecutablePath)
            || !MatchesHash(evidence.ExecutablePath, evidence.ExecutableSha256)
            || !string.Equals(pack.ContentSha256, evidence.ProviderPackSha256, StringComparison.OrdinalIgnoreCase)
            || !Path.IsPathFullyQualified(evidence.SourceEvidencePath)
            || !MatchesHash(evidence.SourceEvidencePath, evidence.SourceEvidenceSha256)
            || evidence.VerifiedAtUtc > DateTimeOffset.UtcNow.AddMinutes(5)
            || evidence.VerifiedAtUtc < DateTimeOffset.UtcNow - (maximumAge ?? TimeSpan.FromDays(1)))
            throw Denied("Current route, executable, embedded provider pack or source proof has no admission.");
        using var source = JsonDocument.Parse(File.ReadAllBytes(evidence.SourceEvidencePath));
        if (!source.RootElement.TryGetProperty("status", out var status)
            || status.GetString() != evidence.SourceEvidenceStatus
            || !source.RootElement.TryGetProperty("admissibleInferenceOnly", out var admissible)
            || admissible.ValueKind != JsonValueKind.True)
            throw Denied("The actual native inspection did not admit inference-only execution.");
        var observed = source.RootElement;
        if (!observed.TryGetProperty("admissionProvider", out var observedProvider) || observedProvider.GetString() != provider
            || !observed.TryGetProperty("admissionModel", out var observedModel) || observedModel.GetString() != model
            || !observed.TryGetProperty("executable", out var observedExecutable)
            || !SamePath(observedExecutable.GetString() ?? "", evidence.ExecutablePath)
            || !observed.TryGetProperty("executableSha256", out var observedBinaryHash)
            || !string.Equals(observedBinaryHash.GetString(), evidence.ExecutableSha256, StringComparison.OrdinalIgnoreCase)
            || !observed.TryGetProperty("providerPackSha256", out var observedPackHash)
            || !string.Equals(observedPackHash.GetString(), evidence.ProviderPackSha256, StringComparison.OrdinalIgnoreCase)
            || !observed.TryGetProperty("exactArgv", out var argv) || argv.ValueKind != JsonValueKind.Array
            || !observed.TryGetProperty("workDirectory", out var observedDirectory)
            || !observed.TryGetProperty("outputFile", out var observedOutput)
            || !observed.TryGetProperty("createdAtUtc", out var observedTime)
            || !observedTime.TryGetDateTimeOffset(out var inspectedAt)
            || evidence.VerifiedAtUtc < inspectedAt || evidence.VerifiedAtUtc > inspectedAt.AddMinutes(5))
            throw Denied("Native source evidence does not bind this exact route, binary, pack and invocation.");
        var observedArguments = argv.EnumerateArray().Select(a => a.GetString()).Select(a =>
            a == observedDirectory.GetString() ? "{workingDirectory}"
            : a == observedOutput.GetString() ? "{outputPath}" : a).ToArray();
        if (!observedArguments.SequenceEqual(evidence.InferenceArgumentTemplate))
            throw Denied("Projection arguments differ from the invocation actually inspected.");
        if (pack.SubscriptionCli is not { } cli || evidence.InferenceArgumentTemplate.Count < cli.InferenceArguments.Count
            || !evidence.InferenceArgumentTemplate.Take(cli.InferenceArguments.Count).SequenceEqual(cli.InferenceArguments))
            throw Denied("The inspected invocation does not bind the loaded provider-pack controls.");
        var controlled = cli.ControlledLaunch ?? throw Denied("This provider has no controlled-launch metadata.");
        if (!evidence.InferenceArgumentTemplate.Skip(cli.InferenceArguments.Count)
            .Take(controlled.FileAuthenticationArguments.Count).SequenceEqual(controlled.FileAuthenticationArguments))
            throw Denied("The inspected invocation did not bind file-only authentication arguments.");
        // Only the working/output paths may vary between otherwise identical invocations.
        if (evidence.InferenceArgumentTemplate.Any(a => a.Contains('{')
            && a is not ("{workingDirectory}" or "{outputPath}")))
            throw Denied("Invocation contains an unreviewed variable argument.");
        InferenceLaunchEnvironmentContract.Validate(evidence, source.RootElement, workingDirectory,
            maximumAge ?? TimeSpan.FromDays(1));
        return evidence;
    }

    public void ValidateRequest(SubscriptionCliProcessRequest request)
    {
        var evidence = ValidateEvidence();
        if (request.ProviderName != provider || request.CatalogProtocol is not null
            || !SamePath(request.FileName, evidence.ExecutablePath)
            || !SamePath(request.WorkingDirectory, workingDirectory))
            throw Denied("The physical executable/provider/protocol is outside admission.");
        InferenceLaunchEnvironmentContract.ValidateSnapshot(evidence, request.ChildEnvironmentSnapshot);
        var cli = ProviderPackCatalog.GetRequired(provider).SubscriptionCli!;
        // Auth/readiness are exact, non-model probes. Model discovery is never admitted here.
        if (request.Operation == SubscriptionCliProcessOperation.LoginStatus
            && request.StandardInput is null
            && request.Arguments.SequenceEqual(cli.ControlledLaunch!.FileAuthenticationArguments.Concat(cli.LoginStatusArguments))) return;
        if (request.Operation == SubscriptionCliProcessOperation.ExecutableReadiness
            && request.StandardInput is null && request.Arguments.SequenceEqual(cli.HelpArguments)) return;
        if (request.Operation != SubscriptionCliProcessOperation.Inference || request.RequestedModel != model
            || request.StandardInput is null || !SamePath(request.WorkingDirectory, workingDirectory)
            || request.OutputPath is null || !Path.IsPathFullyQualified(request.OutputPath)
            || !SamePath(Path.GetDirectoryName(request.OutputPath)!, workingDirectory))
            throw Denied("The invocation is not the exact inference route in the owned scratch directory.");
        var expected = evidence.InferenceArgumentTemplate.Select(a => a switch
        {
            "{workingDirectory}" => request.WorkingDirectory,
            "{outputPath}" => request.OutputPath,
            _ => a,
        });
        if (!request.Arguments.SequenceEqual(expected))
            throw Denied("The physical inference arguments changed after inspection.");
    }

    public IReadOnlyDictionary<string, string?> CaptureEnvironment()
        => InferenceLaunchEnvironmentContract.Capture(ValidateEvidence());

    public IReadOnlyList<string> ExpandControlledArguments(SubscriptionCliProcessRequest request)
    {
        var cli = ProviderPackCatalog.GetRequired(provider).SubscriptionCli
            ?? throw Denied("This provider has no controlled launch arguments.");
        var required = cli.ControlledLaunch?.FileAuthenticationArguments
            ?? throw Denied("This provider has no reviewed file-authentication arguments.");
        if (request.Operation == SubscriptionCliProcessOperation.ExecutableReadiness) return request.Arguments.ToArray();
        var insertion = request.Operation == SubscriptionCliProcessOperation.LoginStatus ? 0 : cli.InferenceArguments.Count;
        if (request.Arguments.Skip(insertion).Take(required.Count).SequenceEqual(required)) return request.Arguments.ToArray();
        if (request.Arguments.Count < insertion) throw Denied("Controlled argument prefix is missing.");
        return request.Arguments.Take(insertion).Concat(required).Concat(request.Arguments.Skip(insertion)).ToArray();
    }

    public void ValidatePreparedLaunch(SubscriptionCliProcessRequest request, ProcessStartInfo info)
    {
        ValidateRequest(request);
        if (info.UseShellExecute || !SamePath(info.FileName, request.FileName)
            || !SamePath(info.WorkingDirectory, workingDirectory)
            || info.Arguments.Length != 0 || !info.ArgumentList.SequenceEqual(request.Arguments)
            || request.ChildEnvironmentSnapshot is null
            || info.Environment.Count != request.ChildEnvironmentSnapshot.Count
            || request.ChildEnvironmentSnapshot.Any(pair => !info.Environment.TryGetValue(pair.Key, out var actual) || actual != pair.Value))
            throw Denied("The final physical executable, arguments, cwd or child environment changed after admission.");
    }

    public static string HashFile(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static bool MatchesHash(string path, string expected) => File.Exists(path)
        && expected is { Length: 64 } && string.Equals(HashFile(path), expected, StringComparison.OrdinalIgnoreCase);
    private static bool SamePath(string a, string b) => Path.IsPathFullyQualified(a) && Path.IsPathFullyQualified(b)
        && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static UnauthorizedAccessException Denied(string reason) => new("Inference admission denied: " + reason);
}

/// <summary>Hosts opt into the physical launch guard; ordinary hosts retain their existing runner.</summary>
public sealed class AdmittedSubscriptionCliProcessRunner(ISubscriptionCliProcessRunner inner,
    InferenceAdmissionValidator admission) : ISubscriptionCliProcessRunner
{
    public async Task<SubscriptionCliProcessResult> RunAsync(SubscriptionCliProcessRequest request, CancellationToken ct = default)
    {
        // Freeze the caller's mutable argument list before validation and any await.
        // Capture once before any await. Only a reviewed OS environment survives;
        // provider/auth/proxy/certificate overrides are never inherited later.
        var environment = admission.CaptureEnvironment();
        var frozen = request with { Arguments = admission.ExpandControlledArguments(request), ChildEnvironmentSnapshot = environment,
            ValidateFinalLaunch = null };
        var launchRequest = frozen;
        frozen = frozen with { ValidateFinalLaunch = info => admission.ValidatePreparedLaunch(launchRequest, info) };
        await Task.Run(() => admission.ValidateRequest(frozen), ct).ConfigureAwait(false);
        return await inner.RunAsync(frozen, ct).ConfigureAwait(false);
    }
}
