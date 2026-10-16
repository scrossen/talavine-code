using System.Text.Json.Serialization;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.Agent.InstructionChat;

public sealed record AgentInstructionAmendmentRequest(
    string AgentName,
    string RecipeText,
    string SanitizedUserMessage,
    IReadOnlyList<InstructionChatMessage> Conversation,
    string RuntimeAbilities,
    string SourceType = "agent-instruction-chat");

/// <summary>
/// Shared LLM lane for conversationally updating an agent operating manual.
/// The host owns secret extraction, persistence, capability grants, and actual
/// work dispatch; this service owns the provider-neutral prompt and contract.
/// </summary>
public interface IAgentInstructionAmendmentService
{
    Task<InstructionAmendment> AmendAsync(
        AgentInstructionAmendmentRequest request,
        CancellationToken ct = default);
}

public sealed class AgentInstructionAmendmentService : IAgentInstructionAmendmentService
{
    public const int MaxConversationMessages = 16;
    public const int MaxConversationMessageChars = 1500;
    public static readonly TimeSpan DefaultResponseTimeout = TimeSpan.FromSeconds(120);

    private readonly ILlmRouter _router;
    private readonly TimeSpan _responseTimeout;

    public AgentInstructionAmendmentService(ILlmRouter router, TimeSpan? responseTimeout = null)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _responseTimeout = responseTimeout ?? DefaultResponseTimeout;
        if (_responseTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(responseTimeout), "The response timeout must be positive.");
    }

    public async Task<InstructionAmendment> AmendAsync(
        AgentInstructionAmendmentRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.SanitizedUserMessage))
            throw new ArgumentException("Say what the agent should do, or ask it a question.");

        using var deadline = new CancellationTokenSource(_responseTimeout);
        using var turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            var prompt = BuildPrompt(request);
            var draft = await GenerateAsync(
                FunctionTypes.AgentInstructionChat,
                prompt,
                new LlmRouterOptions
                {
                    SourceType = request.SourceType,
                    AllowTextJsonContractRepair = true,
                },
                turnCancellation.Token);

            if (draft is null || string.IsNullOrWhiteSpace(draft.Reply))
                throw new InvalidOperationException("The agent didn't produce a usable reply; try rephrasing.");

            if (string.IsNullOrWhiteSpace(draft.UpdatedRecipe) && ReplyClaimsManualUpdate(draft.Reply))
            {
                var retry = await GenerateAsync(
                    FunctionTypes.AgentInstructionChat,
                    prompt + CorrectiveRetryNote,
                    new LlmRouterOptions
                    {
                        SourceType = request.SourceType + "-retry",
                        AllowTextJsonContractRepair = true,
                    },
                    turnCancellation.Token);
                if (retry is not null && !string.IsNullOrWhiteSpace(retry.Reply))
                    draft = retry;
            }

            return new InstructionAmendment(
                draft.Reply.Trim(),
                string.IsNullOrWhiteSpace(draft.UpdatedRecipe)
                    ? null
                    : draft.UpdatedRecipe.Trim() + Environment.NewLine,
                (draft.Questions ?? [])
                    .Where(question => !string.IsNullOrWhiteSpace(question))
                    .Select(question => question.Trim())
                    .ToList(),
                string.IsNullOrWhiteSpace(draft.RunNowTask) ? null : draft.RunNowTask.Trim());
        }
        catch (OperationCanceledException exception) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The agent management reply did not finish within {_responseTimeout.TotalSeconds:0} seconds. Try again or choose a faster model for agent-instruction-chat.",
                exception);
        }
    }

    private async Task<InstructionAmendmentDraft> GenerateAsync(
        string functionType,
        string prompt,
        LlmRouterOptions options,
        CancellationToken ct)
    {
        options.RequestCancellationToken = ct;
        return await _router.GenerateResponseAsync<InstructionAmendmentDraft>(functionType, prompt, options)
            .WaitAsync(ct)
            .ConfigureAwait(false);
    }

    public static string RenderConversation(
        IReadOnlyList<InstructionChatMessage>? conversation)
    {
        if (conversation is null || conversation.Count == 0) return string.Empty;
        var lines = conversation
            .TakeLast(MaxConversationMessages)
            .Select(message =>
            {
                var text = message.Text.Length <= MaxConversationMessageChars
                    ? message.Text
                    : message.Text[..MaxConversationMessageChars] + " …";
                return $"{(message.FromBoss ? "Boss" : "Agent")}: {text}";
            });
        return "\nConversation so far:\n---\n" + string.Join("\n", lines) + "\n---\n";
    }

    public static bool ReplyClaimsManualUpdate(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return false;
        var normalized = reply.ToLowerInvariant();
        return (normalized.Contains("manual") || normalized.Contains("instruction"))
               && (normalized.Contains("updated") || normalized.Contains("update now")
                   || normalized.Contains("added") || normalized.Contains("incorporated")
                   || normalized.Contains("folded"));
    }

    private static string BuildPrompt(AgentInstructionAmendmentRequest request) => $"""
        You ARE {request.AgentName}, the supervised agent defined by the operating manual below,
        chatting with your boss on your control page. The boss gives you instructions, shows you
        how to do things, or asks questions. Your job is to confirm what you understood, explain
        concretely HOW you will carry it out, and fold real standing-behavior changes into your
        operating manual.

        Runtime abilities:
        {request.RuntimeAbilities.Trim()}

        Current operating manual:
        ---
        {request.RecipeText.Trim()}
        ---
        {RenderConversation(request.Conversation)}
        The boss just said:
        ---
        {request.SanitizedUserMessage.Trim()}
        ---
        Honor everything already established in the conversation above. Never re-ask a question
        that was already answered.

        IMPORTANT: you have NO tools in this control conversation. Never claim work is executing
        here. Real work happens in a separate tool-equipped cycle. When the boss asks you to DO
        something now, set runNowTask to a self-contained task brief; the host will enqueue that
        real cycle immediately.

        Respond with ONLY a JSON object with exactly these keys: "reply" (string),
        "updatedRecipe" (string or null), "questions" (array of strings),
        "runNowTask" (string or null).
        - reply: first person, concise plain paragraphs. State what you understood and the exact
          tools, CRM capabilities, stored credentials, and steps you will use.
        - updatedRecipe: for a standing-behavior change, the FULL revised operating manual.
          Preserve every unrelated specific. Return null for questions, praise, or clarification.
        - Keep every stored-credential placeholder exactly as written.
        - questions: only facts still required; empty when none.
        - runNowTask: a concise, self-contained task brief for immediate one-off action; otherwise null.
        - Never say the manual was updated while returning updatedRecipe null.
        """;

    internal const string CorrectiveRetryNote = """

        CORRECTION: Your reply claimed the operating manual changed, but updatedRecipe was null.
        Return the FULL revised manual in updatedRecipe now, preserving unrelated content. If no
        standing behavior should change, correct the reply so it does not claim an update.
        """;

    public sealed class InstructionAmendmentDraft
    {
        [JsonPropertyName("reply")]
        public string Reply { get; set; } = string.Empty;

        [JsonPropertyName("updatedRecipe")]
        public string? UpdatedRecipe { get; set; }

        [JsonPropertyName("questions")]
        public List<string>? Questions { get; set; }

        [JsonPropertyName("runNowTask")]
        public string? RunNowTask { get; set; }
    }
}
