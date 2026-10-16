using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// Redacts credentials and user identifiers from provider/runtime diagnostics before
/// they cross into chat results or the durable routing ledger. This is intentionally
/// conservative: diagnostics remain useful (status codes, provider/model names) while
/// secrets, bearer tokens, and email addresses never become routing evidence.
/// </summary>
public static class RoutingDiagnosticSanitizer
{
    public const int MaxEvidenceJsonBytes = 16 * 1024;
    private const int MaxInputCharacters = 8 * 1024;
    private const int MaxEvidenceInputBytes = 8 * 1024 * 1024;
    private const int MaxEvidenceDepth = 24;
    private const int MaxEvidenceArrayItems = 12;
    private const int MaxEvidenceObjectProperties = 32;
    private const int MaxEvidenceStringLength = 256;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly Regex EmailAddress = new(
        @"(?<![\w.+-])[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}(?![\w.-])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout);
    // Any http(s) URL, plus any other scheme (postgres://, smtp://, redis://…)
    // whose authority carries user:password credentials.
    private static readonly Regex HttpUrl = new(
        @"(?i)\bhttps?://[^\s]+|\b[a-z][a-z0-9+.-]*://[^\s/@:]+:[^\s/@]+@[^\s]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout);
    private static readonly Regex BearerToken = new(
        @"(?i)\bbearer\s+[A-Za-z0-9._~+/=-]{6,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout);
    private static readonly Regex CredentialAssignment = new(
        """(?i)\b(api[_-]?key|authorization|password|passwd|secret|access[_-]?token|refresh[_-]?token|token)\b\s*[:=]\s*(?:"[^"]*"|'[^']*'|[^\s,;&]+)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout);
    private static readonly Regex KnownSecretPrefix = new(
        @"(?i)\b(?:sk|rk|pk)-(?:proj-)?[A-Za-z0-9_-]{8,}|\b(?:ghp|ghs|github_pat|xox[baprs])[-_][A-Za-z0-9_-]{8,}|\bAIza[A-Za-z0-9_-]{12,}|\b(?:AKIA|ASIA)[0-9A-Z]{16}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout);
    // Real JWTs start with "eyJ" (base64 of '{"') or have long opaque segments.
    // Requiring that keeps dotted application identifiers (e.g.
    // "message.sender-policy.apply") from being redacted as tokens.
    private static readonly Regex JwtLikeToken = new(
        @"\b(?:eyJ[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]{4,}|[A-Za-z0-9_-]{24,}\.[A-Za-z0-9_-]{24,}\.[A-Za-z0-9_-]{24,})\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout);
    private static readonly string[] SensitiveEvidenceKeyFragments =
    [
        "prompt", "body", "content", "subject", "header", "payload", "raw",
        "requesttext", "responsetext", "message", "email", "secret", "apikey",
        "api_key", "authorization", "bearer", "credential", "password", "tokenvalue"
    ];
    private static readonly HashSet<string> SafeTokenMetricKeys = new(StringComparer.Ordinal)
    {
        "totaltokens", "inputtokens", "outputtokens", "prompttokens", "completiontokens",
        "reasoningtokens", "cachedinputtokens", "estimatedinputtokens", "estimatedoutputtokens",
        "tokenspersecond", "latencysampleoutputtokens",
        "maxcontexttokens", "minimumcontexttokens",
        "requiredcontexttokens", "irreduciblecontexttokens"
    };
    private static readonly HashSet<string> SafeTechnicalKeyFields = new(StringComparer.Ordinal)
    {
        // A non-secret hardware compatibility fingerprint. Keep this single-item
        // allowlist explicit; unknown *Key fields are credentials until proven otherwise.
        "hardwarekey"
    };
    private static readonly HashSet<string> SafeMetricOrPolicyKeysWithSensitiveFragments = new(StringComparer.Ordinal)
    {
        // These are bounded routing-policy/benchmark fields. Their names contain
        // "message", but their values are booleans or numeric telemetry rather
        // than message content.
        "escalateflaggedmessages",
        "longthreadmessagethreshold",
        "estimatedcostperthousandmessagesusd"
    };
    private static readonly HashSet<string> SafeEvidencePropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Envelope / lifecycle
        "schema", "version", "status", "state", "source", "kind", "command", "scope",
        "request", "requirements", "candidates", "candidate", "selectedCandidate", "configuredRoute", "desiredPlan",
        "modelScoutDecision", "selected", "alternatives", "eligible",
        "requestedOverride", "validatedAdmission", "functionEvidence", "provenance", "weights", "score", "result", "routes", "route", "reason", "rejectionReason",
        "before", "after", "from", "to", "error", "failureKind", "failureScope", "succeeded", "timedOut", "deferred",
        "evidenceDisposition", "originalBytes", "sha256", "_membersTruncated", "requested", "validation",
        "stage", "details", "enabled", "operation", "originDeviceId", "originLsn",
        "governorCalls", "hardSafetyOnly", "advisorName", "policyKind", "priority",
        "routingPolicy", "budgetPressure", "evaluations", "recommended", "persistPreference",
        "errorMessage", "explicitAuthority", "spendEvaluation", "metered",
        "automaticCostLimitUsd", "selectedEstimatedCostUsd",

        // Routing identity and authorship
        "provider", "model", "functionType", "profileId", "fallbackProvider", "fallbackModel",
        "beforeProvider", "beforeModel", "afterProvider", "afterModel", "beforeFallbackProvider",
        "beforeFallbackModel", "afterFallbackProvider", "afterFallbackModel", "updatedBy", "userPinned",
        "beforeKnownGoodProvider", "beforeKnownGoodModel", "beforeKnownGoodConfirmedAt",
        "afterKnownGoodProvider", "afterKnownGoodModel", "afterKnownGoodConfirmedAt",
        "isDefault", "isUserPinned", "overrideProvenance", "correlationId", "auditCorrelationId",
        "admissionMode", "policyLane",

        // Capability / economics / evidence
        "capability", "quality", "speed", "cost", "reliability", "evidenceConfidence", "total", "appliedWeights",
        "routingPrior", "appliedRoutingPrior", "capacityEfficiency",
        "routingQualityPrior", "routingSpeedPrior", "routingCapacityEfficiencyPrior", "routingPriorSource",
        "capabilityProvenance", "provenanceKind", "clearanceTier", "bundleVersion",
        "evidenceScope", "sourceFunctionType",
        "capabilityScore", "estimatedLatencyMs", "averageLatencyMs", "latencySampleOutputTokens", "tokensPerSecond", "totalTokens",
        "inputTokens", "outputTokens", "promptTokens", "completionTokens", "reasoningTokens",
        "cachedInputTokens", "estimatedInputTokens", "estimatedOutputTokens",
        "estimatedCost", "estimatedCostUsd", "estimatedCostPerThousandMessagesUsd", "inputCostPer1K", "outputCostPer1K", "reliabilityScore",
        "reliabilitySampleCount", "passRate", "criticalFailures", "parseFailures", "operatorCleared", "isAvailable", "isLocal", "isSubscription",
        "supportsVision", "supportsToolUse", "supportsStructuredOutput", "maxContextTokens", "minimumContextTokens",
        "capabilityEvidenceSource", "evidenceFunctionType", "evidenceSource", "evidenceVersion",
        "evidenceTimestampUtc", "evidenceExpiresAtUtc", "evaluatedAtUtc", "expiresAtUtc", "isEvidenceCurrent", "lastEvaluatedAt",
        "requiresVision", "requiresToolUse", "requiresStructuredOutput", "interactivity", "privacy", "urgency",
        "workload", "selectionPolicyVersion", "demand", "demandSignals", "rankingNote",
        "requiredContextTokens", "irreducibleContextTokens",
        "latencyBudgetMs", "fallbackUsed", "responseTimeMs", "automaticMaterialization",
        "adoptedBootstrapPlaceholder",
        "exactAdmitted", "spendBlocked", "automatic", "routineCleared", "priorityCleared",
        "allowedProviders", "tier", "maximumEstimatedCostUsd", "minimumCapabilityScore",
        "minimumReliability", "requireMeasuredCapabilityScore", "requiredTier",
        "blockWeakerPolicyFallback", "requireCurrentFunctionEvidence",
        "maintainCurrentFunctionEvidenceRecovery", "disableFallback", "allowAnyProviderFallback",
        "latencyTargetMs", "capabilityEvidenceKind",

        // User-approved Scout policy / recovery command schema
        "monthlyCloudBudgetUsd", "clearMonthlyCloudBudget", "beforeMonthlyCloudBudgetUsd",
        "afterMonthlyCloudBudgetUsd", "beforeUsesCustomWeights", "afterUsesCustomWeights",
        "modelScoutCapabilityWeight", "modelScoutSpeedWeight", "modelScoutCostWeight",
        "resetCustomWeights", "useCustomWeights", "monthlyCloudBudgetTargetUsd",
        "monthlyCloudBudgetTargetIsHardStop", "budgetPressureThreshold", "allowMeteredPipelineFallback",
        "meteredPipelineDailyCapUsd", "clearMeteredPipelineDailyCap", "runId", "runStatus",
        "recovered", "configurationMaterialized", "requestedRoutine", "requestedPriority",
        "readable", "urgencyBoostMonthlyBudgetUsd", "subscriptionQuotaThreshold", "allowUrgencyBoost",
        "remainingFraction",
        "monthlyBudgetUsd", "monthToDateUsd", "burnFraction", "projectedMonthUsd",
        "quietHoursStart", "quietHoursEnd", "quietHoursForceLocal", "useCustomModelScoutWeights",
        "allowMeteredProvider", "revision", "mutationRevision", "enableHybridEscalation", "routineRoute", "priorityRoute",
        "planRevision", "planUpdatedAtUtc",
        "escalateSentMail", "escalateRespondedContacts", "escalateRealtimeChannels",
        "escalateCalendarItems", "escalateSensitiveThreads", "escalateLongThreads",
        "escalateFlaggedMessages", "longThreadMessageThreshold", "routineRouteUpdatedBy",
        "priorityRouteUpdatedBy", "routineUserOwned", "priorityUserOwned", "routineRouteIntent",
        "priorityRouteIntent",

        // Personal-assistance decision receipt evidence (PersonalAssistanceService.FinishAsync):
        // the source fact identity/version proof and the assignment's objective/target
        // linkage. These are opaque identifiers/hashes, never message content.
        "factId", "sourceFingerprint", "originalObjectiveId", "objectiveId",
        "missionConstraintVersion", "targetFingerprint",

        // Published function-proof receipt schema
        "evidenceType", "canonicalFunction", "hardwareBucket", "confidence", "weightedScore", "caseCount",
        "qualificationProtocolVersion", "providerPackIdentity", "routeKind", "sourceSuiteVersion",
        "executableBundleSha256", "evaluatorVersion", "requiredSettingsSha256", "expectedCaseCount",
        "providerFailures", "fallbackCaseCount",

        // Local artifact/runtime proof
        "hardwareKey", "proofIdentitySha256", "modelDigest", "runtime", "runtimeVersion", "executionRuntimeIdentity", "executionContractId", "quantization", "payloadSha256", "suiteSha256",
        "promptSha256", "benchmarkPromptSha256", "benchmarkSuiteSha256",
        "intentId", "replacementIntentId", "supersededIntentId", "approvalId", "approvalAuditId",
        "approvalGenerationSha256", "approvedAtUtc", "requestedSeeds", "requestedRoutes", "appliedRoutes", "cascaded",

        // Sensitive schema keys are retained so their redaction is explainable.
        "prompt", "body", "content", "subject", "headers", "payload", "raw", "requestText",
        "responseText", "message", "email", "secret", "apiKey", "authorization", "credential",
        "password", "token", "accessToken", "refreshToken", "idToken"
    };

    public static string? Sanitize(string? value, int maxLength = 2_000)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        maxLength = Math.Clamp(maxLength, 16, 8_192);
        var bounded = value.Length <= MaxInputCharacters ? value : value[..MaxInputCharacters];
        bounded = bounded.Replace('\r', ' ').Replace('\n', ' ').Trim();
        try
        {
            bounded = HttpUrl.Replace(bounded, SanitizeUrl);
            bounded = BearerToken.Replace(bounded, "Bearer [redacted]");
            bounded = CredentialAssignment.Replace(
                bounded,
                match => $"{match.Groups[1].Value}=[redacted]");
            bounded = JwtLikeToken.Replace(bounded, "[redacted-token]");
            bounded = KnownSecretPrefix.Replace(bounded, "[redacted-secret]");
            bounded = EmailAddress.Replace(bounded, "[redacted-email]");
        }
        catch (RegexMatchTimeoutException)
        {
            return "[provider diagnostic redacted: pattern timeout]";
        }

        return bounded.Length <= maxLength ? bounded : bounded[..maxLength] + "…";
    }

    /// <summary>
    /// Produces valid, bounded JSON for durable routing evidence. Sensitive
    /// fields are removed by key and every string value receives the same
    /// credential/identifier redaction as scalar diagnostics.
    /// </summary>
    public static string? SanitizeEvidenceJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        var inputBytes = Encoding.UTF8.GetBytes(json);
        if (inputBytes.Length > MaxEvidenceInputBytes)
            return BuildEvidencePlaceholder("oversized", inputBytes);

        try
        {
            using var document = JsonDocument.Parse(inputBytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = MaxEvidenceDepth
            });
            var sanitized = SanitizeEvidencePass(
                document.RootElement,
                MaxEvidenceArrayItems,
                MaxEvidenceObjectProperties,
                MaxEvidenceStringLength);
            if (sanitized.Length > MaxEvidenceJsonBytes)
            {
                sanitized = SanitizeEvidencePass(
                    document.RootElement,
                    maxArrayItems: 3,
                    maxObjectProperties: MaxEvidenceObjectProperties,
                    maxStringLength: 128);
            }

            return sanitized.Length <= MaxEvidenceJsonBytes
                ? Encoding.UTF8.GetString(sanitized)
                : BuildEvidencePlaceholder("truncated", sanitized);
        }
        catch (JsonException)
        {
            return BuildEvidencePlaceholder("invalid-json", inputBytes);
        }
    }

    /// <summary>
    /// Accepts only a canonical SHA-256 digest. A caller that accidentally supplies
    /// raw text receives a digest of that text instead, so hash-labelled audit
    /// columns can never become a covert transcript field.
    /// </summary>
    public static string? NormalizeSha256(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var trimmed = value.Trim();
        if (trimmed.Length == 64 && trimmed.All(Uri.IsHexDigit))
            return trimmed.ToLowerInvariant();

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(trimmed)))
            .ToLowerInvariant();
    }

    private static byte[] SanitizeEvidencePass(
        JsonElement root,
        int maxArrayItems,
        int maxObjectProperties,
        int maxStringLength)
    {
        using var stream = new MemoryStream(MaxEvidenceJsonBytes);
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteSanitizedElement(
                writer,
                root,
                depth: 0,
                maxArrayItems,
                maxObjectProperties,
                maxStringLength);
        }
        return stream.ToArray();
    }

    private static void WriteSanitizedElement(
        Utf8JsonWriter writer,
        JsonElement element,
        int depth,
        int maxArrayItems,
        int maxObjectProperties,
        int maxStringLength)
    {
        if (depth >= MaxEvidenceDepth)
        {
            writer.WriteStringValue("[depth-limit]");
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var propertyCount = 0;
                foreach (var property in element.EnumerateObject())
                {
                    if (propertyCount++ >= maxObjectProperties)
                    {
                        writer.WriteBoolean("_membersTruncated", true);
                        break;
                    }

                    var name = SanitizeEvidencePropertyName(property.Name);
                    writer.WritePropertyName(name);
                    var isKnownSchema = !name.StartsWith("_redactedKey_", StringComparison.Ordinal);
                    if (isKnownSchema
                        && IsSha256EvidenceKey(property.Name)
                        && property.Value.ValueKind == JsonValueKind.String)
                    {
                        writer.WriteStringValue(NormalizeSha256(property.Value.GetString()));
                    }
                    else if (IsSensitiveEvidenceKey(property.Name))
                    {
                        writer.WriteStringValue("[redacted]");
                    }
                    else if (name.StartsWith("_redactedKey_", StringComparison.Ordinal))
                    {
                        // Unknown fields are not part of the routing-evidence
                        // contract. Hashing only their key is insufficient because
                        // the corresponding value may itself be a name, identifier,
                        // prompt fragment, or other user data.
                        writer.WriteStringValue("[redacted-unknown-field]");
                    }
                    else
                    {
                        WriteSanitizedElement(
                            writer,
                            property.Value,
                            depth + 1,
                            maxArrayItems,
                            maxObjectProperties,
                            maxStringLength);
                    }
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                var itemCount = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (itemCount++ >= maxArrayItems)
                    {
                        writer.WriteStringValue("[items-truncated]");
                        break;
                    }
                    WriteSanitizedElement(
                        writer,
                        item,
                        depth + 1,
                        maxArrayItems,
                        maxObjectProperties,
                        maxStringLength);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(Sanitize(element.GetString(), maxStringLength) ?? string.Empty);
                break;
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var integer)) writer.WriteNumberValue(integer);
                else if (element.TryGetDecimal(out var number)) writer.WriteNumberValue(number);
                else writer.WriteRawValue(element.GetRawText());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;
        }
    }

    private static bool IsSha256EvidenceKey(string propertyName)
    {
        var normalized = new string(propertyName
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
        return normalized.EndsWith("sha256", StringComparison.Ordinal);
    }

    private static bool IsSensitiveEvidenceKey(string propertyName)
    {
        var normalized = new string(propertyName
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
        // A token count is routing telemetry, not prompt content. Return before
        // the broader "prompt"/"content" fragments so promptTokens remains
        // explainable while prompt, promptText, and token credentials stay redacted.
        if (SafeTokenMetricKeys.Contains(normalized))
        {
            return false;
        }
        if (SafeMetricOrPolicyKeysWithSensitiveFragments.Contains(normalized))
        {
            return false;
        }
        if (normalized.Contains("token", StringComparison.Ordinal))
        {
            return true;
        }
        if ((normalized.EndsWith("key", StringComparison.Ordinal)
             || normalized.EndsWith("keyvalue", StringComparison.Ordinal))
            && !SafeTechnicalKeyFields.Contains(normalized))
        {
            return true;
        }
        return SensitiveEvidenceKeyFragments.Any(fragment =>
            normalized.Contains(
                new string(fragment.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray()),
                StringComparison.Ordinal));
    }

    private static string SanitizeEvidencePropertyName(string propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName)) return "_unnamed";

        // Evidence keys are schema, never user data. Preserve ordinary identifier
        // names for explainability; hash anything that looks like free text, a URL,
        // an address, a credential, or an overlong dynamically supplied key.
        var sanitized = Sanitize(propertyName, 128);
        var isSchemaIdentifier = propertyName.Length <= 128
                                 && SafeEvidencePropertyNames.Contains(propertyName)
                                 && sanitized is not null
                                 && string.Equals(propertyName, sanitized, StringComparison.Ordinal)
                                 && (char.IsLetter(propertyName[0]) || propertyName[0] == '_')
                                 && propertyName.All(character =>
                                     char.IsLetterOrDigit(character)
                                     || character is '_' or '-' or '.');
        if (isSchemaIdentifier)
            return propertyName;

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(propertyName)))
            .ToLowerInvariant();
        return $"_redactedKey_{digest[..16]}";
    }

    private static string BuildEvidencePlaceholder(string disposition, byte[] payload)
        => JsonSerializer.Serialize(new
        {
            evidenceDisposition = disposition,
            originalBytes = payload.Length,
            sha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant()
        });

    private static string SanitizeUrl(Match match)
    {
        var trailing = string.Empty;
        var candidate = match.Value;
        while (candidate.Length > 0 && candidate[^1] is '.' or ',' or ';' or ')' or ']' or '}')
        {
            trailing = candidate[^1] + trailing;
            candidate = candidate[..^1];
        }

        // Provider diagnostics routinely put reset tokens, tenant identifiers,
        // mailbox ids, and other private material in URL paths as well as query
        // strings. Even percent-encoded path segments are opaque here, so there is
        // no safe diagnostic subset to retain. Provider/model fields already carry
        // the useful routing identity; durable free-form evidence keeps only a
        // stable marker and any sentence punctuation outside the URL.
        return "[redacted-url]" + trailing;
    }
}
