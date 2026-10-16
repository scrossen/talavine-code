using System.Security.Cryptography;
using System.Text;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.ToolUseEngine;

namespace SwarmCore.Agent.Runtime;

/// <summary>Reconciles a checked saved answer into an append-only provider transcript.</summary>
public static class AgentSavedOutputRepair
{
    /// <summary>
    /// The caller must own the conversation execution gate. A stable marker makes
    /// retries idempotent even if the host redacts the persisted notice body.
    /// Earlier tool protocol and subsequent user turns remain intact; this notice
    /// corrects an answer and carries no new user authority or completion verdict.
    /// </summary>
    public static async Task AppendCorrectionAsync(IAgentSessionStore sessions, string conversationId,
        Guid turnId, string request, string checkedOutput, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkedOutput);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(checkedOutput)));
        var marker = $"{EngineAuthoredNotice.Prefix}saved-answer correction {turnId:N}/{digest}:";
        var transcript = await sessions.LoadAsync(conversationId, ct);
        if (transcript.Any(m => m.Role == ChatRole.User && m.Parts.OfType<TextPart>()
                .Any(p => p.Text.StartsWith(marker, StringComparison.Ordinal)))) return;

        var notice = EngineAuthoredNotice.Create($"saved-answer correction {turnId:N}/{digest}",
            $"Output checks repaired the assistant answer for the earlier request below. " +
            "Use the corrected answer when referring to that response. This does not change later requests, " +
            "authorize any action, or assert that the objective is complete; acceptance is checked separately.\n" +
            $"Earlier request:\n{request}\nCorrected answer:\n{checkedOutput}");
        await sessions.AppendAsync(conversationId, ChatMessage.Text(ChatRole.User, notice), ct);
    }
}
