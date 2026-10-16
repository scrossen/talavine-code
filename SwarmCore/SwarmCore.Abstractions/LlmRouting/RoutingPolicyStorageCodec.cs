using System.Text.Json;
using System.Text.Json.Nodes;

namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// Canonical persistence/sync contract for <see cref="RoutingPolicy"/>. The policy
/// is a typed nested object and a caller-owned lower-case GUID-N revision is its
/// required sibling. Unknown fields are ignored for forward compatibility, but
/// missing current fields and historical bare/flat shapes are rejected rather than
/// silently migrated.
/// </summary>
public static class RoutingPolicyStorageCodec
{
    private const string RevisionProperty = "revision";
    private const string PolicyProperty = "policy";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
    };
    private static readonly string[] RequiredPolicyProperties = JsonSerializer
        .SerializeToElement(RoutingPolicy.Default, JsonOptions)
        .EnumerateObject()
        .Select(property => property.Name)
        .ToArray();

    public static string Serialize(RoutingPolicy policy, string revision)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (RoutingPolicy.NormalizeUntrusted(policy) != policy)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy),
                "Routing policy values must be within the supported safe bounds.");
        }

        revision = ValidateRevision(revision);
        var root = new JsonObject
        {
            [PolicyProperty] = JsonSerializer.SerializeToNode(policy, JsonOptions),
            [RevisionProperty] = revision,
        };
        return root.ToJsonString(JsonOptions);
    }

    public static bool TryDeserialize(
        string? payload,
        out RoutingPolicySnapshot snapshot,
        out string validationCode)
    {
        snapshot = new RoutingPolicySnapshot(RoutingPolicy.Default, string.Empty);
        if (string.IsNullOrWhiteSpace(payload))
        {
            validationCode = "empty-policy";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                validationCode = "root-not-object";
                return false;
            }

            var properties = root.EnumerateObject().ToList();
            var policies = properties
                .Where(property => property.Name.Equals(
                    PolicyProperty,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (policies.Count != 1
                || !policies[0].NameEquals(PolicyProperty)
                || policies[0].Value.ValueKind != JsonValueKind.Object)
            {
                validationCode = "policy-missing-duplicate-or-not-object";
                return false;
            }

            // A payload containing policy fields at the storage root is a distinct
            // historical shape, even if it also carries the current nested object.
            if (properties.Any(property => RequiredPolicyProperties.Contains(
                    property.Name,
                    StringComparer.OrdinalIgnoreCase)))
            {
                validationCode = "flat-policy-shape-not-supported";
                return false;
            }

            var revisions = properties
                .Where(property => property.Name.Equals(
                    RevisionProperty,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (revisions.Count != 1 || !revisions[0].NameEquals(RevisionProperty))
            {
                validationCode = "revision-missing-or-duplicate";
                return false;
            }

            var revisionElement = revisions[0].Value;
            var revisionText = revisionElement.ValueKind == JsonValueKind.String
                ? revisionElement.GetString()
                : null;
            if (!Guid.TryParseExact(revisionText, "N", out var parsedRevision)
                || !string.Equals(revisionText, parsedRevision.ToString("N"), StringComparison.Ordinal))
            {
                validationCode = "revision-not-canonical-guid";
                return false;
            }

            var policyProperties = policies[0].Value.EnumerateObject().ToList();
            foreach (var required in RequiredPolicyProperties)
            {
                var matches = policyProperties
                    .Where(property => property.Name.Equals(
                        required,
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();
                // Additive connection preference: old valid policies retain every
                // existing limit and use subscription-first until explicitly changed.
                if (required == "preferredModelConnection" && matches.Count == 0)
                    continue;
                if (matches.Count != 1 || !matches[0].NameEquals(required))
                {
                    validationCode = "policy-property-missing-or-duplicate";
                    return false;
                }
            }

            var parsed = policies[0].Value.Deserialize<RoutingPolicy>(JsonOptions);
            if (parsed is null)
            {
                validationCode = "policy-unreadable";
                return false;
            }

            if (RoutingPolicy.NormalizeUntrusted(parsed) != parsed)
            {
                validationCode = "policy-out-of-range";
                return false;
            }

            snapshot = new RoutingPolicySnapshot(parsed, parsedRevision.ToString("N"));
            validationCode = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            validationCode = "policy-unreadable";
            return false;
        }
    }

    public static string ValidateRevision(string revision)
    {
        if (!Guid.TryParseExact(revision, "N", out var parsed)
            || !string.Equals(revision, parsed.ToString("N"), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A routing policy mutation revision must be a lower-case caller-generated GUID in N format.",
                nameof(revision));
        }

        return parsed.ToString("N");
    }
}
