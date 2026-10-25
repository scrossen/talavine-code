namespace SwarmCore.Agent.Business;

public enum BusinessKind { Service, Product }
public enum BusinessDisposition { New, Assessing, Qualified, NotNow, Declined, Suppressed, Proposed, ConfirmedSale, Delivering, CustomerValueConfirmed, SupportOpen, Retained }

/// <summary>A lifecycle case's standing on a shared sales pipeline.</summary>
public enum BusinessDealOutcome { Open, Won, Lost }

/// <summary>Owner supplied configuration. Claims and terms are allowlists, never source instructions.</summary>
public sealed record BusinessBriefDefinition(string Name, BusinessKind Kind, string Offer, string Audience,
    string[] AllowedClaims, string[] QualificationCriteria, string TimeZoneId = "UTC",
    int WorkingStartHour = 9, int WorkingEndHour = 17, int EvidenceMaxAgeDays = 30,
    decimal MaximumProposalValue = 0, string Currency = "USD", string[]? AllowedTerms = null, DayOfWeek[]? WorkingDays = null,
    bool RequiresInventoryFulfillment = false)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 200 || string.IsNullOrWhiteSpace(Offer) || Offer.Length > 4000
            || string.IsNullOrWhiteSpace(Audience) || Audience.Length > 2000 || !Enum.IsDefined(Kind)
            || AllowedClaims is null || QualificationCriteria is not { Length: > 0 and <= 20 }
            || AllowedClaims.Length > 50 || AllowedTerms?.Length > 50
            || QualificationCriteria.Concat(AllowedClaims).Concat(AllowedTerms ?? []).Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 1000)
            || WorkingStartHour < 0 || WorkingEndHour > 24 || WorkingStartHour >= WorkingEndHour
            || EvidenceMaxAgeDays is < 1 or > 365 || MaximumProposalValue < 0
            || Currency is null || Currency.Length != 3 || !Currency.All(char.IsAsciiLetter))
            throw new ArgumentException("Supply a bounded offer, audience, qualification criteria, claims, commercial bounds and working hours.");
        if (WorkingDays is not null && (WorkingDays.Length is < 1 or > 7 || WorkingDays.Distinct().Count() != WorkingDays.Length || WorkingDays.Any(x => !Enum.IsDefined(x))))
            throw new ArgumentException("Choose one to seven distinct working days.");
        if (RequiresInventoryFulfillment && Kind != BusinessKind.Product) throw new ArgumentException("Inventory fulfillment applies to product offers.");
        _ = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
    }
}

public sealed record BusinessEvidenceFact(string SourceIdentity, string SourceUrl, DateTimeOffset ObservedAtUtc,
    string[] Facts, bool Conflicting = false, bool CustomerConfirmed = false);
public sealed record BusinessQualification(bool Qualified, string Reason, string[] MatchedCriteria);

/// <summary>An internal, unissued service invoice draft. It is not a payment request or receipt.</summary>
public sealed record BusinessServiceInvoiceSnapshot(Guid CaseId, long ContactId, Guid ProposalId, int ProposalVersion,
    string ProposalPayloadHash, Guid AcceptanceEvidenceId, Guid DeliveryPlanId, Guid FirstValueEvidenceId,
    int BriefRevision, string SellerName, string ServiceDescription, decimal Amount, string Currency, string[] Terms, string[] Claims);

public static class BusinessServiceInvoicePolicy
{
    public static string WitnessReason(Guid proposalId) => $"Internal invoice draft created for accepted proposal {proposalId:D}; unissued.";

    public static string ProposalHash(Guid caseId, long contactId, int version, int briefRevision, decimal value,
        string currency, string[] terms, string[] claims) => Hash(new
        {
            Id = caseId, ContactId = contactId, version, Revision = briefRevision,
            Value = value.ToString("G29", System.Globalization.CultureInfo.InvariantCulture),
            Currency = currency, Terms = terms, Claims = claims, ProductScope = (object?)null
        });

    public static string SnapshotHash(BusinessServiceInvoiceSnapshot snapshot)
    {
        if (snapshot.CaseId == Guid.Empty || snapshot.ContactId <= 0 || snapshot.ProposalId == Guid.Empty
            || snapshot.ProposalVersion <= 0 || snapshot.ProposalPayloadHash is null || snapshot.ProposalPayloadHash.Length != 64
            || snapshot.AcceptanceEvidenceId == Guid.Empty || snapshot.DeliveryPlanId == Guid.Empty
            || snapshot.FirstValueEvidenceId == Guid.Empty || snapshot.BriefRevision <= 0
            || string.IsNullOrWhiteSpace(snapshot.SellerName) || snapshot.SellerName.Length > 200
            || string.IsNullOrWhiteSpace(snapshot.ServiceDescription) || snapshot.ServiceDescription.Length > 4000
            || snapshot.Amount < 0 || snapshot.Currency is null || snapshot.Currency.Length != 3 || !snapshot.Currency.All(char.IsAsciiLetter)
            || snapshot.Terms is null || snapshot.Terms.Length > 50 || snapshot.Claims is null || snapshot.Claims.Length > 50
            || snapshot.Terms.Concat(snapshot.Claims).Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 1000))
            throw new ArgumentException("Service invoice draft requires a bounded accepted offer and delivery identity.");
        return Hash(snapshot);
    }

    private static string Hash<T>(T value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(value))));
}

public static class BusinessLifecycleRules
{
    /// <summary>
    /// Checks a selected customer observation against current same-source history.
    /// Hosts supply case/tenant-scoped rows under their mutation transaction; this
    /// policy neither creates customer confirmation nor rewrites historical effects.
    /// </summary>
    public static bool ConfirmsCurrent(BusinessBriefDefinition brief, BusinessEvidenceFact selected,
        IReadOnlyList<BusinessEvidenceFact> history, string fact, DateTimeOffset now)
    {
        if (!selected.CustomerConfirmed || selected.Conflicting || selected.ObservedAtUtc > now
            || selected.ObservedAtUtc < now.AddDays(-brief.EvidenceMaxAgeDays)
            || !selected.Facts.Contains(fact, StringComparer.Ordinal)) return false;
        var source = history.Where(e => e.SourceIdentity == selected.SourceIdentity).ToArray();
        // Any later observation supersedes this selection, even if its clock is
        // invalid. It cannot authorize falling back to a favorable older fact.
        if (source.Length == 0 || source.Max(e => e.ObservedAtUtc) != selected.ObservedAtUtc) return false;
        return source.Where(e => e.ObservedAtUtc == selected.ObservedAtUtc).All(e =>
            e.CustomerConfirmed && !e.Conflicting && e.SourceUrl == selected.SourceUrl
            && e.Facts.Order(StringComparer.Ordinal).SequenceEqual(selected.Facts.Order(StringComparer.Ordinal), StringComparer.Ordinal));
    }

    public static BusinessQualification Qualify(BusinessBriefDefinition brief, IReadOnlyList<BusinessEvidenceFact> evidence,
        DateTimeOffset now, bool identityAmbiguous)
    {
        brief.Validate();
        if (identityAmbiguous) return new(false, "Resolve the contact identity before qualification.", []);
        var eligible = evidence.Where(e => e.ObservedAtUtc <= now && e.ObservedAtUtc >= now.AddDays(-brief.EvidenceMaxAgeDays)).ToArray();
        var current = eligible.GroupBy(e => e.SourceIdentity, StringComparer.Ordinal)
            .SelectMany(group => group.Where(e => e.ObservedAtUtc == group.Max(x => x.ObservedAtUtc))).ToArray();
        if (current.Any(e => e.Conflicting)) return new(false, "Conflicting evidence requires an owned decision.", []);
        if (current.GroupBy(e => e.SourceIdentity, StringComparer.Ordinal).Any(group => group
            .Select(e => System.Text.Json.JsonSerializer.Serialize(e.Facts.Order(StringComparer.Ordinal))).Distinct(StringComparer.Ordinal).Count() > 1))
            return new(false, "Different observations share the latest source timestamp. Refresh or resolve that uncertainty before qualification.", []);
        // Reference research about a separate organization is attributed context only; it can never
        // satisfy a criterion for this customer even if its literal text repeats a criterion. A sender's
        // submitted inquiry text (form or captured message) is likewise attributed context only.
        var matches = brief.QualificationCriteria.Where(c => current.Any(e => !BusinessPublicReferencePolicy.IsReference(e)
            && !BusinessInquiryCapturePolicy.IsSubmittedText(e)
            && e.Facts.Any(f => string.Equals(c, f, StringComparison.OrdinalIgnoreCase)))).ToArray();
        return new(matches.Length == brief.QualificationCriteria.Length,
            matches.Length == brief.QualificationCriteria.Length ? "Every configured criterion has current attributed evidence." : "Missing or stale evidence; no qualified opportunity is inferred.", matches);
    }

    public static bool NeedsNextAction(BusinessDisposition state) => state is not (BusinessDisposition.Declined or BusinessDisposition.Suppressed or BusinessDisposition.Retained);

    /// <summary>
    /// How a lifecycle disposition reads on a host's shared sales pipeline: sold work (through delivery,
    /// support and retention) is won, declined or suppressed work is lost, everything else is open. Hosts map
    /// the outcome onto their own closed-stage vocabulary; the disposition itself stays on the case.
    /// </summary>
    public static BusinessDealOutcome DealOutcome(BusinessDisposition state) => state switch
    {
        BusinessDisposition.ConfirmedSale or BusinessDisposition.Delivering or BusinessDisposition.CustomerValueConfirmed
            or BusinessDisposition.SupportOpen or BusinessDisposition.Retained => BusinessDealOutcome.Won,
        BusinessDisposition.Declined or BusinessDisposition.Suppressed => BusinessDealOutcome.Lost,
        _ => BusinessDealOutcome.Open,
    };
    /// <summary>Continuing or reopening assessment records an owner decision, not qualification.</summary>
    public static bool IsAssessmentReview(BusinessDisposition from, BusinessDisposition to) =>
        (from is BusinessDisposition.Assessing or BusinessDisposition.NotNow) && to == BusinessDisposition.Assessing;
    public static bool CanTransition(BusinessDisposition from, BusinessDisposition to) => (from, to) switch
    {
        (_, BusinessDisposition.Suppressed) => true,
        (BusinessDisposition.New, BusinessDisposition.Assessing) => true,
        (BusinessDisposition.Assessing or BusinessDisposition.NotNow, BusinessDisposition.Assessing) => true,
        (BusinessDisposition.Assessing or BusinessDisposition.NotNow, BusinessDisposition.Qualified or BusinessDisposition.NotNow or BusinessDisposition.Declined) => true,
        (BusinessDisposition.Qualified or BusinessDisposition.Proposed, BusinessDisposition.Proposed) => true,
        (BusinessDisposition.Proposed, BusinessDisposition.ConfirmedSale or BusinessDisposition.Declined) => true,
        (BusinessDisposition.ConfirmedSale, BusinessDisposition.Delivering) => true,
        (BusinessDisposition.Delivering, BusinessDisposition.CustomerValueConfirmed or BusinessDisposition.SupportOpen) => true,
        (BusinessDisposition.CustomerValueConfirmed, BusinessDisposition.Retained or BusinessDisposition.SupportOpen) => true,
        (BusinessDisposition.Retained, BusinessDisposition.SupportOpen) => true,
        (BusinessDisposition.SupportOpen, BusinessDisposition.CustomerValueConfirmed or BusinessDisposition.Delivering) => true,
        _ => false
    };

    public static bool WithinWorkingHours(BusinessBriefDefinition brief, DateTimeOffset utc)
    {
        var local = TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.FindSystemTimeZoneById(brief.TimeZoneId));
        return (brief.WorkingDays?.Contains(local.DayOfWeek) ?? local.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            && local.Hour >= brief.WorkingStartHour && local.Hour < brief.WorkingEndHour;
    }
}
