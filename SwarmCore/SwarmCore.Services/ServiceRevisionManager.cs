namespace SwarmCore.Services;

/// <summary>
/// Pure version-state transitions for catalog services. Candidates never alter
/// live <see cref="ServiceDefinition.Config"/> until a test receipt passes and
/// activation happens atomically in one stored definition.
/// </summary>
public static class ServiceRevisionManager
{
    public static void EnsureInitialized(ServiceDefinition service)
    {
        ArgumentNullException.ThrowIfNull(service);
        service.Revisions ??= [];
        if (service.Revisions.Count == 0)
        {
            service.ActiveRevision = 1;
            service.Revisions.Add(new ServiceRevision
            {
                Version = 1,
                Config = Copy(service.Config),
                State = ServiceRevisionState.Active,
                CreatedBy = service.CreatedBy,
                CreatedUtc = service.CreatedUtc,
                ActivatedUtc = service.CreatedUtc,
                ChangeReason = "Initial cataloged version",
            });
            return;
        }

        if (service.ActiveRevision <= 0)
        {
            var active = service.Revisions
                .Where(revision => revision.State == ServiceRevisionState.Active)
                .OrderByDescending(revision => revision.Version)
                .FirstOrDefault()
                ?? service.Revisions.OrderBy(revision => revision.Version).First();
            service.ActiveRevision = active.Version;
            active.State = ServiceRevisionState.Active;
            service.Config = Copy(active.Config);
        }
    }

    public static ServiceRevision AddCandidate(
        ServiceDefinition service,
        IReadOnlyDictionary<string, string> config,
        string? changeReason,
        string createdBy = "agent")
    {
        EnsureInitialized(service);
        var version = service.Revisions.Max(revision => revision.Version) + 1;
        var candidate = new ServiceRevision
        {
            Version = version,
            Config = Copy(config),
            State = ServiceRevisionState.Candidate,
            CreatedBy = createdBy,
            ChangeReason = Bound(changeReason, 1_000),
            CreatedUtc = DateTime.UtcNow,
        };
        service.Revisions.Add(candidate);
        return candidate;
    }

    public static ServiceRevision? Find(ServiceDefinition service, int version)
    {
        EnsureInitialized(service);
        return service.Revisions.FirstOrDefault(revision => revision.Version == version);
    }

    public static ServiceRevision? LatestCandidate(ServiceDefinition service)
    {
        EnsureInitialized(service);
        return service.Revisions
            .Where(revision => revision.State == ServiceRevisionState.Candidate)
            .OrderByDescending(revision => revision.Version)
            .FirstOrDefault();
    }

    public static void RecordTest(ServiceRevision revision, ServiceCallResult result, string? expectation)
    {
        ArgumentNullException.ThrowIfNull(revision);
        revision.TestedUtc = DateTime.UtcNow;
        var expectationPassed = string.IsNullOrEmpty(expectation)
            || (result.Output?.Contains(expectation, StringComparison.OrdinalIgnoreCase) ?? false);
        var semanticFailure = ServiceResultSemantics.DetectFailure(result.Output);
        revision.TestPassed = result.Ok && expectationPassed && semanticFailure is null;
        var summary = !result.Ok
            ? $"Failed: {result.Error}"
            : semanticFailure is not null
                ? $"Failed: {semanticFailure}: {result.Output}"
                : !expectationPassed
                    ? $"Failed expectation; output did not contain '{expectation}': {result.Output}"
                    : $"Passed{(string.IsNullOrEmpty(expectation) ? "" : $"; output contained '{expectation}'")}: {result.Output}";
        revision.TestSummary = Bound(summary, 2_000);
        if (revision.TestPassed == false)
        {
            // Keep it available for inspection/retest, but make failure
            // explicit. A later revised source becomes a new immutable version.
            revision.State = ServiceRevisionState.Rejected;
        }
    }

    public static bool Activate(ServiceDefinition service, ServiceRevision revision, out string? error)
    {
        EnsureInitialized(service);
        if (!service.Revisions.Contains(revision))
        {
            error = "The revision does not belong to this service.";
            return false;
        }

        if (revision.TestPassed != true && revision.Version != service.ActiveRevision)
        {
            error = $"Revision {revision.Version} has no passing test receipt. Run services.test before activation.";
            return false;
        }

        foreach (var item in service.Revisions.Where(item => item.State == ServiceRevisionState.Active))
        {
            item.State = ServiceRevisionState.Superseded;
        }

        revision.State = ServiceRevisionState.Active;
        revision.ActivatedUtc = DateTime.UtcNow;
        service.ActiveRevision = revision.Version;
        service.Config = Copy(revision.Config);
        service.ConsecutiveFailures = 0;
        service.LastCallError = null;
        error = null;
        return true;
    }

    public static ServiceRevision? ResolveRollbackTarget(ServiceDefinition service, int? requestedVersion)
    {
        EnsureInitialized(service);
        if (requestedVersion is { } version)
        {
            return service.Revisions.FirstOrDefault(revision =>
                revision.Version == version
                && revision.Version != service.ActiveRevision
                && (revision.TestPassed == true || revision.ActivatedUtc is not null));
        }

        return service.Revisions
            .Where(revision => revision.Version != service.ActiveRevision
                               && (revision.TestPassed == true || revision.ActivatedUtc is not null))
            .OrderByDescending(revision => revision.ActivatedUtc ?? revision.TestedUtc ?? revision.CreatedUtc)
            .FirstOrDefault();
    }

    public static ServiceDefinition ForInvocation(ServiceDefinition service, ServiceRevision revision)
        => new()
        {
            Id = service.Id,
            Name = service.Name,
            Kind = service.Kind,
            Description = service.Description,
            WhenToUse = service.WhenToUse,
            Tags = [.. service.Tags],
            Config = Copy(revision.Config),
            CredentialRef = service.CredentialRef,
            SideEffect = service.SideEffect,
            Enabled = service.Enabled,
            CreatedBy = service.CreatedBy,
            CreatedUtc = service.CreatedUtc,
            UpdatedUtc = service.UpdatedUtc,
            ActiveRevision = revision.Version,
        };

    private static Dictionary<string, string> Copy(IReadOnlyDictionary<string, string>? config)
        => config is null
            ? new(StringComparer.OrdinalIgnoreCase)
            : new(config, StringComparer.OrdinalIgnoreCase);

    private static string? Bound(string? value, int max)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
