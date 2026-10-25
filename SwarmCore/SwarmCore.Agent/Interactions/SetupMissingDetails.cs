using System.Text;
using SwarmCore.Abstractions.Security;

namespace SwarmCore.Agent.Interactions;

/// <summary>A value the agent filled from the brief or context, shown so the person can correct it.</summary>
public sealed record SetupInferredDetail(string Name, string Value, string? Label = null);

/// <summary>
/// The one shared "ask for what is missing" contract for setting up a repeatable process.
/// It generalizes the structured missing-fields result: the agent states the plan it inferred,
/// lists the values it filled itself, and asks once for the remaining details. Authority
/// (mode, permission grants, sender account, recipient scope, spend) is never asked here;
/// it stays with the owner's explicit configuration. Hosts without a durable interaction
/// store return this record in the tool result; hosts with one call <see cref="ToInteractionRequest"/>.
/// </summary>
public sealed record SetupMissingDetails
{
    /// <summary>Maximum combined asked plus inferred fields; matches the interaction store limit.</summary>
    public const int MaximumFields = 20;

    /// <summary>Stable id of the process being set up (for example a schedule or retention process id).</summary>
    public required string Process { get; init; }

    /// <summary>One sentence stating the plan the agent inferred.</summary>
    public required string Summary { get; init; }

    /// <summary>Details the agent could not infer and must ask for, batched into one request.</summary>
    public IReadOnlyList<InteractionFormField> Fields { get; init; } = [];

    /// <summary>Values the agent filled from context; each becomes an optional, pre-filled field.</summary>
    public IReadOnlyList<SetupInferredDetail> Inferred { get; init; } = [];

    /// <summary>
    /// Canonical names of authority fields that are never asked or inferred through this contract.
    /// <see cref="IsAuthorityField"/> also refuses close variants of these names.
    /// </summary>
    public static IReadOnlySet<string> NeverInferable { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "mode", "reviewMode", "autonomousMode", "autonomy", "executionMode", "dispatchMode",
        "permission", "permissions", "permissionGrant", "grant", "authority", "authorization", "approvalRequired",
        "senderAccount", "sender", "fromAccount", "fromAddress", "sendAs",
        "recipientScope", "audienceScope",
        "spendLimit", "spend", "budget", "dailyBudget", "costLimit", "maximumCost",
    };

    /// <summary>Field kinds that carry authority and are refused regardless of the field name.</summary>
    public static IReadOnlySet<string> NeverInferableKinds { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "mode", "permission", "grant", "authority", "sender-account", "recipient-scope", "spend-limit", "budget",
    };

    private static readonly HashSet<string> AuthorityWords = new(StringComparer.Ordinal)
    {
        "mode", "autonomous", "autonomy", "autonomously", "permission", "permissions", "grant", "grants",
        "authority", "authorization", "authorize", "approval", "approve", "sender", "sendas",
        "spend", "spending", "budget",
    };

    /// <summary>True when a field name or kind asks for authority, which this contract must never carry.</summary>
    public static bool IsAuthorityField(string? name, string? kind = null)
    {
        if (!string.IsNullOrWhiteSpace(kind) && NeverInferableKinds.Contains(kind.Trim())) return true;
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (NeverInferable.Contains(name.Trim())) return true;
        var words = Words(name);
        if (words.Any(AuthorityWords.Contains)) return true;
        bool Pair(string a, string b) => words.Contains(a) && words.Contains(b);
        return Pair("recipient", "scope") || Pair("recipients", "scope") || Pair("audience", "scope")
            || Pair("from", "account") || Pair("from", "address") || Pair("cost", "limit") || Pair("maximum", "cost")
            || Pair("send", "limit") || Pair("daily", "limit");
    }

    /// <summary>Refuses a malformed request, an authority field, or credential material in an inferred value.</summary>
    public void Validate()
    {
        Require(Process, nameof(Process), 200);
        Require(Summary, nameof(Summary), 1000);
        ArgumentNullException.ThrowIfNull(Fields);
        ArgumentNullException.ThrowIfNull(Inferred);
        if (Fields.Count == 0) throw new ArgumentException("A setup request must ask for at least one missing detail; proceed without asking when nothing is missing.");
        if (Fields.Count + Inferred.Count > MaximumFields)
            throw new ArgumentException($"A setup request is limited to {MaximumFields} asked and inferred details combined; batch fewer.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in Fields)
        {
            ArgumentNullException.ThrowIfNull(field);
            Require(field.Name, "field name", 100);
            Require(field.Label, "field label", 200);
            if (!names.Add(field.Name.Trim())) throw new ArgumentException($"Field '{field.Name}' is asked more than once.");
            RefuseAuthority(field.Name, field.Kind);
            if (field.Choices is { } choices)
            {
                if (choices.Count is 0 or > 20) throw new ArgumentException($"Field '{field.Name}' must offer 1-20 choices when it offers any.");
                if (choices.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != choices.Count)
                    throw new ArgumentException($"Field '{field.Name}' repeats a choice id.");
                if (field.Default is not null && choices.All(c => c.Id != field.Default))
                    throw new ArgumentException($"Field '{field.Name}' defaults to a value that is not one of its choices.");
            }
            if (field.IsSecret && field.Default is not null)
                throw new ArgumentException($"Secret field '{field.Name}' cannot carry a default value.");
            if (field.Default is not null && CredentialPatterns.Describe(field.Default) is not null)
                throw new ArgumentException($"Field '{field.Name}' default looks like a credential and is never pre-filled.");
        }
        foreach (var inferred in Inferred)
        {
            ArgumentNullException.ThrowIfNull(inferred);
            Require(inferred.Name, "inferred name", 100);
            Require(inferred.Value, $"inferred value for '{inferred.Name}'", 1000);
            if (!names.Add(inferred.Name.Trim()))
                throw new ArgumentException($"'{inferred.Name}' is both asked and inferred, or inferred twice.");
            RefuseAuthority(inferred.Name, null);
            if (InteractionCompletionCanonicalizer.ContainsSensitiveText(inferred.Name)
                || InteractionCompletionCanonicalizer.ContainsSensitiveText(inferred.Label)
                || CredentialPatterns.Describe(inferred.Value) is not null)
                throw new ArgumentException($"Inferred '{inferred.Name}' is credential material; credentials are never inferred or echoed.");
        }
    }

    /// <summary>
    /// Builds one blocking Clarification request: the summary and inferred values in the prompt,
    /// the missing details as required fields, and each inferred value as an optional pre-filled field.
    /// </summary>
    public InteractionRequestCreate ToInteractionRequest(
        string partitionKey,
        string callerRequestId,
        string ownerKind,
        string ownerId,
        string? title = null,
        string? threadId = null,
        Guid? turnId = null,
        string? objectiveId = null,
        string? agentId = null,
        DateTimeOffset? expiresUtc = null)
    {
        Validate();
        var fields = Fields.Concat(Inferred.Select(i => new InteractionFormField(
                i.Name.Trim(), string.IsNullOrWhiteSpace(i.Label) ? i.Name.Trim() : i.Label.Trim(),
                Required: false, HelpText: "Filled from context; change it if it is wrong.")
            { Default = i.Value.Trim() }))
            .ToArray();
        return new InteractionRequestCreate
        {
            PartitionKey = partitionKey,
            CallerRequestId = callerRequestId,
            Kind = InteractionRequestKind.Clarification,
            IsBlocking = true,
            Title = string.IsNullOrWhiteSpace(title) ? "A few details to finish setting this up" : title.Trim(),
            Prompt = Prompt(),
            ThreadId = threadId,
            TurnId = turnId,
            ObjectiveId = objectiveId,
            AgentId = agentId,
            OwnerKind = ownerKind,
            OwnerId = ownerId,
            FormFields = fields,
            ExpiresUtc = expiresUtc,
        };
    }

    /// <summary>Plain-text prompt: the inferred plan, then the values filled from context.</summary>
    public string Prompt()
    {
        var text = new StringBuilder(Summary.Trim());
        if (Inferred.Count > 0)
        {
            text.AppendLine().AppendLine().AppendLine("I filled these from what I already know; change any that are wrong:");
            foreach (var i in Inferred)
                text.Append("- ").Append(string.IsNullOrWhiteSpace(i.Label) ? i.Name.Trim() : i.Label.Trim())
                    .Append(": ").AppendLine(i.Value.Trim());
        }
        var result = text.ToString().TrimEnd();
        return result.Length <= 4000 ? result : result[..4000];
    }

    private static void RefuseAuthority(string name, string? kind)
    {
        if (IsAuthorityField(name, kind))
            throw new ArgumentException($"'{name}' is an authority setting (mode, permission, sender, recipient scope or spend). " +
                "It is never asked or inferred here; the owner sets it explicitly.");
    }

    private static void Require(string? value, string name, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} is required.");
        if (value.Trim().Length > max) throw new ArgumentException($"{name} is limited to {max} characters.");
    }

    private static HashSet<string> Words(string name)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        var current = new StringBuilder();
        void Flush() { if (current.Length > 0) { words.Add(current.ToString().ToLowerInvariant()); current.Clear(); } }
        var text = name.Trim();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (!char.IsLetterOrDigit(c)) { Flush(); continue; }
            if (char.IsUpper(c) && current.Length > 0 && (char.IsLower(text[i - 1]) || i + 1 < text.Length && char.IsLower(text[i + 1])))
                Flush();
            current.Append(c);
        }
        Flush();
        return words;
    }
}

/// <summary>Shared guidance text hosts append to a system prompt when an agent sets up a process.</summary>
public static class SetupMissingDetailsPrompts
{
    /// <summary>Infer first, ask once, batch, never ask for authority, state inferred values.</summary>
    public const string Guidance =
        "When you set up a process, infer every detail you can from the person's brief, the CRM and the inbox first. " +
        "Ask once, in one batched request, only for what you cannot infer. " +
        "Never ask for the client's name, email, amounts or dates already in a thread, the business name, the tone, or which CRM to use. " +
        "Never ask whether to run in Review or Autonomous mode, and never ask for permissions, the sender account, recipient scope or spend limits; " +
        "those stay with the owner's explicit settings. " +
        "State the plan you inferred in one sentence and list the values you filled so the person can correct them. " +
        "After the person answers, do not ask again; proceed with their answers.";
}
