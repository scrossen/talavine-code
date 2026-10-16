namespace SwarmCore.Knowledge;

public static class KnowledgeArtifactNormalizer
{
    public static KnowledgeArtifact Normalize(KnowledgeArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(artifact.Partition);
        ArgumentNullException.ThrowIfNull(artifact.EvidenceLinks);
        ArgumentNullException.ThrowIfNull(artifact.Approvals);
        ArgumentNullException.ThrowIfNull(artifact.Relations);
        KnowledgeContractValidation.RequireDefined(artifact.Kind, nameof(artifact.Kind));
        KnowledgeContractValidation.RequireDefined(artifact.Lifecycle, nameof(artifact.Lifecycle));
        if (!double.IsFinite(artifact.Confidence) || artifact.Confidence is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(artifact.Confidence));
        KnowledgeContractValidation.RequireTimestamp(artifact.CreatedAt, nameof(artifact.CreatedAt));
        if (artifact.EffectiveAt.HasValue && artifact.EffectiveAt.Value == default)
            throw new ArgumentException("Effective timestamp cannot be the default value.", nameof(artifact.EffectiveAt));
        if (artifact.UpdatedAt.HasValue && artifact.UpdatedAt.Value == default)
            throw new ArgumentException("Updated timestamp cannot be the default value.", nameof(artifact.UpdatedAt));
        if (artifact.ExpiresAt.HasValue && artifact.ExpiresAt.Value == default)
            throw new ArgumentException("Expiry timestamp cannot be the default value.", nameof(artifact.ExpiresAt));
        if (artifact.UpdatedAt.HasValue && artifact.UpdatedAt < artifact.CreatedAt)
            throw new ArgumentException("Artifact update cannot precede creation.", nameof(artifact.UpdatedAt));
        if (artifact.ExpiresAt.HasValue
            && artifact.ExpiresAt <= (artifact.EffectiveAt ?? artifact.CreatedAt))
        {
            throw new ArgumentException("Artifact expiry must follow its effective or creation time.", nameof(artifact.ExpiresAt));
        }
        if (artifact.EvidenceLinks.Count > KnowledgeContractLimits.EvidenceLinksPerArtifact)
            throw new ArgumentException("Artifact has too many evidence links.", nameof(artifact.EvidenceLinks));
        if (artifact.Approvals.Count > KnowledgeContractLimits.ApprovalsPerArtifact)
            throw new ArgumentException("Artifact has too many approvals.", nameof(artifact.Approvals));
        if (artifact.Relations.Count > KnowledgeContractLimits.RelationsPerArtifact)
            throw new ArgumentException("Artifact has too many relations.", nameof(artifact.Relations));

        var canonicalKey = KnowledgeContractValidation.RequiredTrimmed(
            artifact.CanonicalKey,
            nameof(artifact.CanonicalKey)).ToLowerInvariant();
        var title = KnowledgeContractValidation.RequiredTrimmed(artifact.Title, nameof(artifact.Title));
        var statement = KnowledgeContractValidation.RequiredTrimmed(artifact.Statement, nameof(artifact.Statement));
        var domain = KnowledgeContractValidation.NullIfWhiteSpace(artifact.Domain)?.ToLowerInvariant();
        KnowledgeContractValidation.RequireBounded(
            canonicalKey,
            KnowledgeContractLimits.ArtifactCanonicalKeyLength,
            nameof(artifact.CanonicalKey));
        KnowledgeContractValidation.RequireBounded(
            title,
            KnowledgeContractLimits.ArtifactTitleLength,
            nameof(artifact.Title));
        KnowledgeContractValidation.RequireBounded(
            statement,
            KnowledgeContractLimits.ArtifactStatementLength,
            nameof(artifact.Statement));
        KnowledgeContractValidation.RequireOptionalBounded(
            domain,
            KnowledgeContractLimits.ArtifactDomainLength,
            nameof(artifact.Domain));
        var partition = KnowledgePartition.Create(artifact.Partition.Kind, artifact.Partition.Id);
        var deterministicId = KnowledgeArtifactIdentity.CreateDeterministicId(
            partition,
            artifact.Kind,
            canonicalKey);
        if (artifact.Id != Guid.Empty && artifact.Id != deterministicId)
            throw new ArgumentException(
                "Artifact id does not match its canonical identity.",
                nameof(artifact.Id));

        var evidenceLinks = artifact.EvidenceLinks
            .Select(NormalizeEvidenceLink)
            .GroupBy(static link => (link.EvidenceId, link.Role))
            .Select(static group => group.OrderByDescending(link => link.Weight).First())
            .OrderBy(static link => link.EvidenceId)
            .ThenBy(static link => link.Role)
            .ToArray();
        var duplicateApprovalId = artifact.Approvals
            .GroupBy(static approval => approval.Id)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicateApprovalId is not null)
            throw new ArgumentException(
                $"Approval event id {duplicateApprovalId.Key} occurs more than once.",
                nameof(artifact.Approvals));
        var approvals = artifact.Approvals
            .Select(NormalizeApproval)
            .OrderBy(static approval => approval.ApprovalKey, StringComparer.Ordinal)
            .ThenBy(static approval => approval.DecidedAt)
            .ThenBy(static approval => approval.Id)
            .ToArray();
        var duplicateApprovalCoordinate = approvals
            .GroupBy(static approval => (approval.ApprovalKey, approval.DecidedAt))
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicateApprovalCoordinate is not null)
            throw new ArgumentException(
                "An approval stream cannot contain two events at the same timestamp.",
                nameof(artifact.Approvals));
        var relations = artifact.Relations
            .Where(relation => relation.TargetArtifactId != deterministicId)
            .Select(NormalizeArtifactRelation)
            .Distinct()
            .OrderBy(static relation => relation.TargetArtifactId)
            .ThenBy(static relation => relation.RelationType)
            .ToArray();

        return artifact with
        {
            Id = deterministicId,
            Partition = partition,
            CanonicalKey = canonicalKey,
            Title = title,
            Statement = statement,
            Domain = domain,
            EvidenceSupport = KnowledgeEvidenceSupportStatus.Unassessed,
            EffectiveAt = artifact.EffectiveAt?.ToUniversalTime(),
            ExpiresAt = artifact.ExpiresAt?.ToUniversalTime(),
            CreatedAt = artifact.CreatedAt.ToUniversalTime(),
            UpdatedAt = artifact.UpdatedAt?.ToUniversalTime(),
            Decision = NormalizeDecisionFacet(artifact.Kind, artifact.Decision),
            Outcome = NormalizeOutcomeFacet(artifact.Kind, artifact.Outcome),
            Pattern = NormalizePatternFacet(artifact.Kind, artifact.Pattern),
            Commitment = NormalizeCommitmentFacet(artifact.Kind, artifact.Commitment),
            EvidenceLinks = evidenceLinks,
            Approvals = approvals,
            Relations = relations
        };
    }

    public static KnowledgeArtifactQuery NormalizeQuery(KnowledgeArtifactQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.Partition);
        ArgumentNullException.ThrowIfNull(query.Kinds);
        ArgumentNullException.ThrowIfNull(query.Lifecycles);
        ArgumentNullException.ThrowIfNull(query.SupportStatuses);
        foreach (var kind in query.Kinds)
            KnowledgeContractValidation.RequireDefined(kind, nameof(query.Kinds));
        foreach (var lifecycle in query.Lifecycles)
            KnowledgeContractValidation.RequireDefined(lifecycle, nameof(query.Lifecycles));
        foreach (var supportStatus in query.SupportStatuses)
            KnowledgeContractValidation.RequireDefined(supportStatus, nameof(query.SupportStatuses));
        if (query.Offset < 0)
            throw new ArgumentOutOfRangeException(nameof(query.Offset));
        if (query.Limit is < 1 or > KnowledgeContractLimits.ArtifactQueryLimit)
            throw new ArgumentOutOfRangeException(nameof(query.Limit));
        if (query.EffectiveFrom.HasValue
            && query.EffectiveUntil.HasValue
            && query.EffectiveUntil < query.EffectiveFrom)
        {
            throw new ArgumentException("Query end time cannot precede its start time.");
        }

        var searchText = KnowledgeContractValidation.NullIfWhiteSpace(query.SearchText);
        var domain = KnowledgeContractValidation.NullIfWhiteSpace(query.Domain)?.ToLowerInvariant();
        KnowledgeContractValidation.RequireOptionalBounded(
            searchText,
            KnowledgeContractLimits.ArtifactSearchTextLength,
            nameof(query.SearchText));
        KnowledgeContractValidation.RequireOptionalBounded(
            domain,
            KnowledgeContractLimits.ArtifactDomainLength,
            nameof(query.Domain));
        return query with
        {
            Partition = KnowledgePartition.Create(query.Partition.Kind, query.Partition.Id),
            SearchText = searchText,
            Domain = domain,
            EffectiveFrom = query.EffectiveFrom?.ToUniversalTime(),
            EffectiveUntil = query.EffectiveUntil?.ToUniversalTime(),
            Kinds = new HashSet<KnowledgeArtifactKind>(query.Kinds),
            Lifecycles = new HashSet<KnowledgeArtifactLifecycle>(query.Lifecycles),
            SupportStatuses = new HashSet<KnowledgeEvidenceSupportStatus>(query.SupportStatuses)
        };
    }

    private static KnowledgeEvidenceLink NormalizeEvidenceLink(KnowledgeEvidenceLink link)
    {
        if (link.EvidenceId == Guid.Empty)
            throw new ArgumentException("Evidence link id cannot be empty.");
        KnowledgeContractValidation.RequireDefined(link.Role, nameof(link.Role));
        if (!double.IsFinite(link.Weight) || link.Weight is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(link.Weight));
        return link;
    }

    public static KnowledgeOrganizationalApproval NormalizeApproval(
        KnowledgeOrganizationalApproval approval)
    {
        if (approval.Id == Guid.Empty)
            throw new ArgumentException("Approval id cannot be empty.");
        KnowledgeContractValidation.RequireDefined(approval.Status, nameof(approval.Status));
        KnowledgeContractValidation.RequireTimestamp(approval.DecidedAt, nameof(approval.DecidedAt));
        var approvalKey = KnowledgeContractValidation.RequiredTrimmed(
            approval.ApprovalKey,
            nameof(approval.ApprovalKey)).ToLowerInvariant();
        var approver = KnowledgeContractValidation.RequiredTrimmed(
            approval.ApproverCanonicalKey,
            nameof(approval.ApproverCanonicalKey)).ToLowerInvariant();
        var scope = KnowledgeContractValidation.NullIfWhiteSpace(approval.AuthorityScope);
        KnowledgeContractValidation.RequireBounded(
            approvalKey,
            KnowledgeContractLimits.ApprovalKeyLength,
            nameof(approval.ApprovalKey));
        KnowledgeContractValidation.RequireBounded(
            approver,
            KnowledgeContractLimits.ArtifactActorKeyLength,
            nameof(approval.ApproverCanonicalKey));
        KnowledgeContractValidation.RequireOptionalBounded(
            scope,
            KnowledgeContractLimits.ArtifactAuthorityScopeLength,
            nameof(approval.AuthorityScope));
        if (approval.ExpiresAt.HasValue && approval.ExpiresAt <= approval.DecidedAt)
            throw new ArgumentException("Approval expiry must follow its decision time.");
        if (approval.SourceEvidenceId == Guid.Empty)
            throw new ArgumentException("Approval evidence id cannot be empty.");

        return approval with
        {
            ApprovalKey = approvalKey,
            ApproverCanonicalKey = approver,
            AuthorityScope = scope,
            DecidedAt = approval.DecidedAt.ToUniversalTime(),
            ExpiresAt = approval.ExpiresAt?.ToUniversalTime()
        };
    }

    private static KnowledgeArtifactRelation NormalizeArtifactRelation(KnowledgeArtifactRelation relation)
    {
        if (relation.TargetArtifactId == Guid.Empty)
            throw new ArgumentException("Related artifact id cannot be empty.");
        KnowledgeContractValidation.RequireDefined(relation.RelationType, nameof(relation.RelationType));
        return relation;
    }

    private static KnowledgeDecisionFacet? NormalizeDecisionFacet(
        KnowledgeArtifactKind kind,
        KnowledgeDecisionFacet? facet)
    {
        var expectsFacet = kind is KnowledgeArtifactKind.Decision
            or KnowledgeArtifactKind.Policy
            or KnowledgeArtifactKind.Exception;
        if (expectsFacet && facet is null)
            throw new ArgumentException($"Artifact kind {kind} requires decision details.", nameof(facet));
        if (!expectsFacet && facet is not null)
            throw new ArgumentException($"Artifact kind {kind} cannot carry decision details.", nameof(facet));
        if (facet is null)
            return null;
        KnowledgeContractValidation.RequireTimestamp(facet.DecidedAt, nameof(facet.DecidedAt));
        ArgumentNullException.ThrowIfNull(facet.Constraints);
        ArgumentNullException.ThrowIfNull(facet.Alternatives);
        ArgumentNullException.ThrowIfNull(facet.Assumptions);
        ArgumentNullException.ThrowIfNull(facet.Risks);
        ArgumentNullException.ThrowIfNull(facet.StakeholderCanonicalKeys);
        if (facet.Urgency.HasValue)
            KnowledgeContractValidation.RequireDefined(facet.Urgency.Value, nameof(facet.Urgency));
        var maker = NormalizeActor(facet.DecisionMakerCanonicalKey, nameof(facet.DecisionMakerCanonicalKey));
        var category = KnowledgeContractValidation.RequiredTrimmed(facet.Category, nameof(facet.Category))
            .ToLowerInvariant();
        KnowledgeContractValidation.RequireBounded(
            category,
            KnowledgeContractLimits.ArtifactDomainLength,
            nameof(facet.Category));
        var role = NormalizeFacetText(facet.DecisionMakerRole, nameof(facet.DecisionMakerRole));
        var reasoning = NormalizeFacetText(facet.Reasoning, nameof(facet.Reasoning));
        var constraints = NormalizeStringList(facet.Constraints, nameof(facet.Constraints));
        var assumptions = NormalizeStringList(facet.Assumptions, nameof(facet.Assumptions));
        var risks = NormalizeStringList(facet.Risks, nameof(facet.Risks));
        var stakeholders = NormalizeActorList(
            facet.StakeholderCanonicalKeys,
            nameof(facet.StakeholderCanonicalKeys));
        if (facet.Alternatives.Count > KnowledgeContractLimits.ArtifactAlternatives)
            throw new ArgumentException("Decision has too many alternatives.", nameof(facet.Alternatives));
        var alternatives = facet.Alternatives.Select((alternative, index) =>
            new KnowledgeDecisionAlternative(
                NormalizeRequiredFacetText(alternative.Description, $"Alternatives[{index}].Description"),
                NormalizeFacetText(alternative.WhyRejected, $"Alternatives[{index}].WhyRejected"),
                NormalizeFacetText(alternative.Pros, $"Alternatives[{index}].Pros"),
                NormalizeFacetText(alternative.Cons, $"Alternatives[{index}].Cons"))).ToArray();
        return facet with
        {
            DecidedAt = facet.DecidedAt.ToUniversalTime(),
            Category = category,
            DecisionMakerCanonicalKey = maker,
            DecisionMakerRole = role,
            Reasoning = reasoning,
            Constraints = constraints,
            Alternatives = alternatives,
            Assumptions = assumptions,
            Risks = risks,
            StakeholderCanonicalKeys = stakeholders,
            ExternalFactors = NormalizeFacetText(facet.ExternalFactors, nameof(facet.ExternalFactors)),
            BusinessContext = NormalizeFacetText(facet.BusinessContext, nameof(facet.BusinessContext)),
            ReversalCost = NormalizeFacetText(facet.ReversalCost, nameof(facet.ReversalCost))
        };
    }

    private static KnowledgeOutcomeFacet? NormalizeOutcomeFacet(
        KnowledgeArtifactKind kind,
        KnowledgeOutcomeFacet? facet)
    {
        if (kind == KnowledgeArtifactKind.Outcome && facet is null)
            throw new ArgumentException("Outcome artifacts require outcome details.", nameof(facet));
        if (kind != KnowledgeArtifactKind.Outcome && facet is not null)
            throw new ArgumentException($"Artifact kind {kind} cannot carry outcome details.", nameof(facet));
        if (facet is null)
            return null;
        KnowledgeContractValidation.RequireTimestamp(facet.OccurredAt, nameof(facet.OccurredAt));
        ArgumentNullException.ThrowIfNull(facet.UnintendedConsequences);
        ArgumentNullException.ThrowIfNull(facet.LessonsLearned);
        KnowledgeContractValidation.RequireDefined(facet.Type, nameof(facet.Type));
        KnowledgeContractValidation.RequireDefined(facet.Recommendation, nameof(facet.Recommendation));
        return facet with
        {
            OccurredAt = facet.OccurredAt.ToUniversalTime(),
            UnintendedConsequences = NormalizeStringList(
                facet.UnintendedConsequences,
                nameof(facet.UnintendedConsequences)),
            QuantifiableImpact = NormalizeFacetText(
                facet.QuantifiableImpact,
                nameof(facet.QuantifiableImpact)),
            LessonsLearned = NormalizeStringList(facet.LessonsLearned, nameof(facet.LessonsLearned)),
            ReportedByCanonicalKey = string.IsNullOrWhiteSpace(facet.ReportedByCanonicalKey)
                ? null
                : NormalizeActor(facet.ReportedByCanonicalKey, nameof(facet.ReportedByCanonicalKey))
        };
    }

    private static KnowledgePatternFacet? NormalizePatternFacet(
        KnowledgeArtifactKind kind,
        KnowledgePatternFacet? facet)
    {
        if (kind == KnowledgeArtifactKind.Pattern && facet is null)
            throw new ArgumentException("Pattern artifacts require pattern details.", nameof(facet));
        if (kind != KnowledgeArtifactKind.Pattern && facet is not null)
            throw new ArgumentException($"Artifact kind {kind} cannot carry pattern details.", nameof(facet));
        if (facet is null)
            return null;
        ArgumentNullException.ThrowIfNull(facet.Caveats);
        if (facet.OccurrenceCount < 0)
            throw new ArgumentOutOfRangeException(nameof(facet.OccurrenceCount));
        ValidateOptionalUnitInterval(facet.SuccessRate, nameof(facet.SuccessRate));
        ValidateOptionalUnitInterval(facet.AverageImpact, nameof(facet.AverageImpact));
        return facet with
        {
            ApplicableWhen = NormalizeRequiredFacetText(facet.ApplicableWhen, nameof(facet.ApplicableWhen)),
            RecommendedAction = NormalizeRequiredFacetText(
                facet.RecommendedAction,
                nameof(facet.RecommendedAction)),
            HandlingGuidance = NormalizeFacetText(facet.HandlingGuidance, nameof(facet.HandlingGuidance)),
            Caveats = NormalizeStringList(facet.Caveats, nameof(facet.Caveats))
        };
    }

    private static KnowledgeCommitmentFacet? NormalizeCommitmentFacet(
        KnowledgeArtifactKind kind,
        KnowledgeCommitmentFacet? facet)
    {
        if (kind == KnowledgeArtifactKind.Commitment && facet is null)
            throw new ArgumentException("Commitment artifacts require commitment details.", nameof(facet));
        if (kind != KnowledgeArtifactKind.Commitment && facet is not null)
            throw new ArgumentException($"Artifact kind {kind} cannot carry commitment details.", nameof(facet));
        if (facet is null)
            return null;
        ArgumentNullException.ThrowIfNull(facet.Updates);
        ArgumentNullException.ThrowIfNull(facet.Blockers);
        KnowledgeContractValidation.RequireDefined(facet.Status, nameof(facet.Status));
        KnowledgeContractValidation.RequireDefined(facet.Priority, nameof(facet.Priority));
        if (facet.OriginatingDecisionArtifactId == Guid.Empty)
            throw new ArgumentException("Originating decision id cannot be empty.");
        if ((facet.DueAt.HasValue && facet.DueAt.Value == default)
            || (facet.CompletedAt.HasValue && facet.CompletedAt.Value == default))
            throw new ArgumentException("Commitment timestamps cannot use the default value.");
        if (facet.Updates.Count > KnowledgeContractLimits.ArtifactUpdates)
            throw new ArgumentException("Commitment has too many updates.", nameof(facet.Updates));
        var updates = facet.Updates.Select((update, index) =>
        {
            KnowledgeContractValidation.RequireTimestamp(update.Timestamp, $"Updates[{index}].Timestamp");
            if (update.StatusChange.HasValue)
                KnowledgeContractValidation.RequireDefined(update.StatusChange.Value, $"Updates[{index}].StatusChange");
            return update with
            {
                Timestamp = update.Timestamp.ToUniversalTime(),
                UpdatedByCanonicalKey = NormalizeActor(
                    update.UpdatedByCanonicalKey,
                    $"Updates[{index}].UpdatedByCanonicalKey"),
                Content = NormalizeRequiredFacetText(update.Content, $"Updates[{index}].Content")
            };
        }).OrderBy(static update => update.Timestamp).ToArray();
        return facet with
        {
            AssignedToCanonicalKey = NormalizeActor(
                facet.AssignedToCanonicalKey,
                nameof(facet.AssignedToCanonicalKey)),
            AssignedByCanonicalKey = string.IsNullOrWhiteSpace(facet.AssignedByCanonicalKey)
                ? null
                : NormalizeActor(facet.AssignedByCanonicalKey, nameof(facet.AssignedByCanonicalKey)),
            DueAt = facet.DueAt?.ToUniversalTime(),
            CompletedAt = facet.CompletedAt?.ToUniversalTime(),
            Updates = updates,
            Blockers = NormalizeStringList(facet.Blockers, nameof(facet.Blockers))
        };
    }

    private static string NormalizeActor(string value, string parameterName)
    {
        var normalized = KnowledgeContractValidation.RequiredTrimmed(value, parameterName).ToLowerInvariant();
        KnowledgeContractValidation.RequireBounded(
            normalized,
            KnowledgeContractLimits.ArtifactActorKeyLength,
            parameterName);
        return normalized;
    }

    private static IReadOnlyList<string> NormalizeActorList(
        IReadOnlyList<string> values,
        string parameterName)
    {
        if (values.Count > KnowledgeContractLimits.ArtifactFacetListItems)
            throw new ArgumentException("Facet list has too many items.", parameterName);
        return values.Select((value, index) => NormalizeActor(value, $"{parameterName}[{index}]"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string> NormalizeStringList(
        IReadOnlyList<string> values,
        string parameterName)
    {
        if (values.Count > KnowledgeContractLimits.ArtifactFacetListItems)
            throw new ArgumentException("Facet list has too many items.", parameterName);
        return values.Select((value, index) =>
                NormalizeRequiredFacetItem(value, $"{parameterName}[{index}]"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string NormalizeRequiredFacetItem(string value, string parameterName)
    {
        var normalized = KnowledgeContractValidation.RequiredTrimmed(value, parameterName);
        KnowledgeContractValidation.RequireBounded(
            normalized,
            KnowledgeContractLimits.ArtifactFacetItemLength,
            parameterName);
        return normalized;
    }

    private static string NormalizeRequiredFacetText(string value, string parameterName)
    {
        var normalized = KnowledgeContractValidation.RequiredTrimmed(value, parameterName);
        KnowledgeContractValidation.RequireBounded(
            normalized,
            KnowledgeContractLimits.ArtifactFacetTextLength,
            parameterName);
        return normalized;
    }

    private static string? NormalizeFacetText(string? value, string parameterName)
    {
        var normalized = KnowledgeContractValidation.NullIfWhiteSpace(value);
        KnowledgeContractValidation.RequireOptionalBounded(
            normalized,
            KnowledgeContractLimits.ArtifactFacetTextLength,
            parameterName);
        return normalized;
    }

    private static void ValidateOptionalUnitInterval(double? value, string parameterName)
    {
        if (value.HasValue && (!double.IsFinite(value.Value) || value.Value is < 0 or > 1))
            throw new ArgumentOutOfRangeException(parameterName);
    }
}
