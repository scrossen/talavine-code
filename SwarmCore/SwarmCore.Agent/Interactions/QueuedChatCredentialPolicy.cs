using SwarmCore.Agent.InstructionChat;

namespace SwarmCore.Agent.Interactions;

/// <summary>Queued drafts cannot reach governed credential intake until their future turn exists.</summary>
public static class QueuedChatCredentialPolicy
{
    public const string RefusalMessage = "This message contains credential-shaped text and was not queued or stored. Your draft is still in the composer. Send it after the current response finishes, or save the credential securely and use its reference.";

    public static bool ContainsCredential(string? prompt) => CredentialShapeDetector.Detect(prompt).Count != 0;

    public static void Validate(InteractionRequestCreate request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Kind == InteractionRequestKind.QueuedChatUpdate && ContainsCredential(request.Prompt))
            throw new QueuedChatCredentialException();
    }
}

/// <summary>Safe fixed-message refusal; it never includes the submitted draft.</summary>
public sealed class QueuedChatCredentialException() : InvalidOperationException(QueuedChatCredentialPolicy.RefusalMessage) { }
