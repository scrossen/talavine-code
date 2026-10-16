using SwarmCore.Abstractions.Computer;
namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>Only the host supplies the session and exact observation used by native actions.</summary>
public sealed record ComputerToolBinding(string ToolName, ComputerObservation Observation);

/// <summary>Native server state cannot survive a route or application-summary boundary.
/// Preserve completed call identity and metadata as ordinary function history.</summary>
public static class ComputerToolProjection
{
    public static IReadOnlyList<ChatMessage> ToFunctionMessages(IReadOnlyList<ChatMessage> messages)
        => messages.Select(message => message with { Parts = message.Parts
            .Where(p => p is not (ResponsesReasoningPart or ResponsesOutputPart or ResponsesCompactionPart or ResponsesConfigurationUpdatePart))
            .Select<ChatContentPart, ChatContentPart>(p => p switch {
                ToolUsePart call => call with { NativeComputerCall = null },
                ToolResultPart result => result with { NativeComputerScreenshot = null },
                _ => p }).ToArray() }).Where(m => m.Parts.Count > 0).ToArray();
}
