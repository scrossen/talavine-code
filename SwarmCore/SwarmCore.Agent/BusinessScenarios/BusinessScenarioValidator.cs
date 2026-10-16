using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SwarmCore.Agent.BusinessScenarios;

public static partial class BusinessScenarioValidator
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter<BusinessScenarioEventKind>() },
    };

    public static BusinessScenarioDocument LoadAndValidate(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var document = JsonSerializer.Deserialize<BusinessScenarioDocument>(json, SerializerOptions)
            ?? throw new JsonException("The business scenario document is empty.");
        Validate(document);
        if ((document.AgentOutputEvents ?? []).Count != 0)
            throw new BusinessScenarioValidationException(
                ["agentOutputEvents must be empty in an initial scenario fixture; the runtime appends outputs."]);
        return document;
    }

    public static void Validate(BusinessScenarioDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var errors = new List<string>();

        if (document.SchemaVersion != CurrentSchemaVersion)
            errors.Add($"schemaVersion must be {CurrentSchemaVersion}.");
        if (!IsStableId(document.ScenarioId))
            errors.Add("scenarioId must be a stable lowercase identifier.");

        ValidateBusiness(document.Business, errors);
        if (document.SeedEvents is null) errors.Add("seedEvents is required.");
        if (document.InjectedEvents is null) errors.Add("injectedEvents is required.");
        if (document.AgentOutputEvents is null) errors.Add("agentOutputEvents is required.");
        var seedEvents = document.SeedEvents ?? [];
        var injectedEvents = document.InjectedEvents ?? [];
        var outputEvents = document.AgentOutputEvents ?? [];

        if (seedEvents.Count == 0) errors.Add("at least one seed event is required.");
        if (document.Business?.Roles is not { Count: > 0 }) errors.Add("business must define at least one role.");
        if (document.Business?.Offers is not { Count: > 0 }) errors.Add("business must define at least one offer.");

        ValidateEvents(seedEvents, BusinessScenarioEventKind.Seed, "seedEvents", errors);
        ValidateEvents(injectedEvents, BusinessScenarioEventKind.Injected, "injectedEvents", errors);
        ValidateEvents(outputEvents, BusinessScenarioEventKind.AgentOutput, "agentOutputEvents", errors);

        var allEvents = seedEvents.Concat(injectedEvents).Concat(outputEvents).ToArray();
        var byId = new Dictionary<string, BusinessScenarioEvent>(StringComparer.Ordinal);
        foreach (var item in allEvents)
        {
            if (string.IsNullOrWhiteSpace(item.EventId))
                continue;
            if (!byId.TryAdd(item.EventId, item))
                errors.Add($"eventId '{item.EventId}' is duplicated.");
        }

        foreach (var item in allEvents)
        {
            foreach (var dependency in item.DependsOn ?? [])
            {
                if (!byId.ContainsKey(dependency))
                    errors.Add($"event '{item.EventId}' depends on missing event '{dependency}'.");
                else if (byId[dependency].OccurredAtUtc > item.OccurredAtUtc)
                    errors.Add($"event '{item.EventId}' occurs before dependency '{dependency}'.");
            }

            foreach (var sourceEventId in item.DeliveryEvidence?.SourceEventIds ?? [])
            {
                if (!byId.ContainsKey(sourceEventId))
                    errors.Add($"simulated delivery '{item.DeliveryEvidence!.OperationId}' links missing event '{sourceEventId}'.");
            }
        }

        foreach (var group in outputEvents
                     .Where(item => item.DeliveryEvidence is not null)
                     .GroupBy(item => item.DeliveryEvidence!.OperationId, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
            errors.Add($"simulated delivery operationId '{group.Key}' appears more than once.");

        if (HasDependencyCycle(allEvents, byId))
            errors.Add("event dependencies must form an acyclic graph.");

        if (errors.Count > 0)
            throw new BusinessScenarioValidationException(errors);
    }

    private static void ValidateBusiness(BusinessScenarioProfile? business, ICollection<string> errors)
    {
        if (business is null)
        {
            errors.Add("business is required.");
            return;
        }

        if (!IsStableId(business.BusinessId)) errors.Add("business.businessId must be a stable lowercase identifier.");
        if (string.IsNullOrWhiteSpace(business.Name)) errors.Add("business.name is required.");
        if (string.IsNullOrWhiteSpace(business.BusinessType)) errors.Add("business.businessType is required.");
        if (string.IsNullOrWhiteSpace(business.TimeZone)) errors.Add("business.timeZone is required.");
        if (business.Owner is null
            || string.IsNullOrWhiteSpace(business.Owner.DisplayName)
            || !IsEmailAddress(business.Owner.WorkEmail)
            || string.IsNullOrWhiteSpace(business.Owner.PersonalAccountPolicy))
            errors.Add("business.owner requires displayName, a workEmail, and personalAccountPolicy.");
        if (business.Roles is null) errors.Add("business.roles is required.");
        if (business.Offers is null) errors.Add("business.offers is required.");
        if (business.PublicReferences is null) errors.Add("business.publicReferences is required.");
        if (!string.IsNullOrWhiteSpace(business.WebsiteUrl) && !IsHttpsUrl(business.WebsiteUrl))
            errors.Add("business.websiteUrl must be an absolute HTTPS URL.");

        foreach (var role in business.Roles ?? [])
        {
            if (!IsStableId(role.RoleId)) errors.Add("role ids must be stable lowercase identifiers.");
            if (string.IsNullOrWhiteSpace(role.Title) || string.IsNullOrWhiteSpace(role.RoutineScope))
                errors.Add($"role '{role.RoleId}' requires a title and routineScope.");
        }
        if ((business.Roles ?? []).Select(role => role.RoleId).Distinct(StringComparer.Ordinal).Count()
            != (business.Roles ?? []).Count)
            errors.Add("business role ids must be unique.");

        foreach (var offer in business.Offers ?? [])
        {
            if (!IsStableId(offer.OfferId)) errors.Add("offer ids must be stable lowercase identifiers.");
            if (string.IsNullOrWhiteSpace(offer.Name) || string.IsNullOrWhiteSpace(offer.Description))
                errors.Add($"offer '{offer.OfferId}' requires a name and description.");
        }
        if ((business.Offers ?? []).Select(offer => offer.OfferId).Distinct(StringComparer.Ordinal).Count()
            != (business.Offers ?? []).Count)
            errors.Add("business offer ids must be unique.");

        foreach (var reference in business.PublicReferences ?? [])
        {
            if (!IsStableId(reference.SourceId)) errors.Add("public reference source ids must be stable lowercase identifiers.");
            if (string.IsNullOrWhiteSpace(reference.Title) || string.IsNullOrWhiteSpace(reference.UseScope))
                errors.Add($"public reference '{reference.SourceId}' requires a title and useScope.");
            if (!IsHttpsUrl(reference.Url)) errors.Add($"public reference '{reference.SourceId}' must use an absolute HTTPS URL.");
            if (reference.RetrievedAtUtc == default || reference.RetrievedAtUtc.Offset != TimeSpan.Zero)
                errors.Add($"public reference '{reference.SourceId}' retrievedAtUtc must be UTC.");
        }
    }

    private static void ValidateEvents(
        IReadOnlyList<BusinessScenarioEvent> events,
        BusinessScenarioEventKind expectedKind,
        string collection,
        ICollection<string> errors)
    {
        foreach (var item in events)
        {
            if (!IsStableId(item.EventId)) errors.Add($"{collection} contains an invalid eventId.");
            if (item.Kind != expectedKind)
                errors.Add($"event '{item.EventId}' in {collection} must have kind '{expectedKind}'.");
            if (item.OccurredAtUtc.Offset != TimeSpan.Zero)
                errors.Add($"event '{item.EventId}' occurredAtUtc must be UTC.");
            if (item.OccurredAtUtc == default)
                errors.Add($"event '{item.EventId}' requires occurredAtUtc.");
            if (item.DependsOn is null)
                errors.Add($"event '{item.EventId}' requires a dependsOn array (use an empty array when there are no dependencies).");
            if (item.Source is null || !IsStableId(item.Source.SourceId) || string.IsNullOrWhiteSpace(item.Source.SourceType))
                errors.Add($"event '{item.EventId}' requires a stable source identity and sourceType.");
            if (item.Source?.Uri is { Length: > 0 } uri && !IsHttpsUrl(uri))
                errors.Add($"event '{item.EventId}' source URI must be an absolute HTTPS URL.");
            if (item.Evidence.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                errors.Add($"event '{item.EventId}' requires evidence.");
            else if (item.Evidence.ValueKind != JsonValueKind.Object)
                errors.Add($"event '{item.EventId}' evidence must be a JSON object.");

            if (ContainsExternalSuccessClaim(item.Evidence))
                errors.Add($"event '{item.EventId}' cannot claim downstream delivery, acceptance, payment, or customer-received success.");

            if (item.DeliveryEvidence is not null)
            {
                if (expectedKind != BusinessScenarioEventKind.AgentOutput)
                    errors.Add($"event '{item.EventId}' cannot seed or inject delivery evidence.");
                if (item.DeliveryEvidence.Status != SimulatedDeliveryStatus.Simulated)
                    errors.Add($"event '{item.EventId}' has an illegal delivery status.");
                if (!IsStableId(item.DeliveryEvidence.OperationId)
                    || string.IsNullOrWhiteSpace(item.DeliveryEvidence.Recipient)
                    || string.IsNullOrWhiteSpace(item.DeliveryEvidence.Subject)
                    || string.IsNullOrWhiteSpace(item.DeliveryEvidence.Body)
                    || item.DeliveryEvidence.SourceEventIds is not { Count: > 0 }
                    || !string.Equals(item.DeliveryEvidence.SimulationLabel, "Simulated Sent", StringComparison.Ordinal))
                    errors.Add($"event '{item.EventId}' requires a complete Simulated Sent receipt.");
                foreach (var sourceEventId in item.DeliveryEvidence.SourceEventIds ?? [])
                {
                    if (!IsStableId(sourceEventId))
                        errors.Add($"simulated delivery '{item.DeliveryEvidence.OperationId}' contains an invalid source event id.");
                }
                foreach (var attachmentSourceId in item.DeliveryEvidence.AttachmentSourceIds ?? [])
                {
                    if (!IsStableId(attachmentSourceId))
                        errors.Add($"simulated delivery '{item.DeliveryEvidence.OperationId}' contains an invalid attachment source id.");
                }
                if (!string.Equals(item.Source?.SourceType, "local-simulation-sink", StringComparison.Ordinal))
                    errors.Add($"event '{item.EventId}' simulated delivery evidence must come from the local simulation sink.");
            }
        }
    }

    private static bool HasDependencyCycle(
        IReadOnlyList<BusinessScenarioEvent> events,
        IReadOnlyDictionary<string, BusinessScenarioEvent> byId)
    {
        var states = new Dictionary<string, byte>(StringComparer.Ordinal);
        foreach (var item in events)
        {
            if (Visit(item.EventId)) return true;
        }

        return false;

        bool Visit(string id)
        {
            if (states.TryGetValue(id, out var state)) return state == 1;
            states[id] = 1;
            if (byId.TryGetValue(id, out var current))
            {
                foreach (var dependency in current.DependsOn ?? [])
                {
                    if (byId.ContainsKey(dependency) && Visit(dependency)) return true;
                }
            }
            states[id] = 2;
            return false;
        }
    }

    private static bool ContainsExternalSuccessClaim(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return element.ValueKind == JsonValueKind.Array && element.EnumerateArray().Any(ContainsExternalSuccessClaim);

        foreach (var property in element.EnumerateObject())
        {
            var key = NormalizePropertyName(property.Name);
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                var value = NormalizePropertyName(property.Value.GetString() ?? string.Empty);
                if (((key is "deliverystatus" or "deliverystate" or "externaldeliverystatus" or "customerdeliverystatus"
                        or "sentstatus" or "customerreceiptstatus")
                    && (value is "delivered" or "sent" or "received" or "customerreceived"))
                    || ((key is "paymentstatus" or "paymentstate") && (value is "paid" or "settled" or "paymentcomplete"))
                    || ((key is "canonicaldealstatus" or "salesoutcome" or "lifecycleoutcome")
                        && (value is "accepted" or "won" or "closedwon" or "booked")))
                    return true;
            }

            if (property.Value.ValueKind == JsonValueKind.True
                && (key is "delivered" or "externallydelivered" or "customerreceived" or "paid"
                    or "paymentreceived" or "canonicaldealaccepted" or "dealwon" or "booked"))
                return true;

            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                && ContainsExternalSuccessClaim(property.Value))
                return true;
        }

        return false;
    }

    private static string NormalizePropertyName(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool IsStableId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && StableIdRegex().IsMatch(value);

    private static bool IsHttpsUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(uri.Host);

    private static bool IsEmailAddress(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && System.Net.Mail.MailAddress.TryCreate(value, out var address)
        && string.Equals(address.Address, value, StringComparison.Ordinal);

    [GeneratedRegex("^[a-z0-9]+(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex StableIdRegex();
}

public sealed class BusinessScenarioValidationException(IReadOnlyList<string> errors)
    : ArgumentException(string.Join(Environment.NewLine, errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
