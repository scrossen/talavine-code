using SwarmCore.Coding.Secrets;

namespace SwarmCore.Coding.Prompting;

/// <summary>Trusted receipt scoped by the host/orchestrator, separate from model text and repository instructions.</summary>
public sealed class CodingCredentialIntakeContext
{
    private readonly AsyncLocal<CredentialIntakeReceipt?> _current = new();
    public CredentialIntakeReceipt? Get(string conversationId, Guid profileId)
        => _current.Value is { IsValid: true } receipt && receipt.ConversationId == conversationId && receipt.ProfileId == profileId ? receipt : null;
    public IDisposable Enter(CredentialIntakeReceipt? receipt)
    {
        var prior = _current.Value;
        _current.Value = receipt is { IsValid: true } ? receipt : null;
        return new Exit(() => _current.Value = prior);
    }
    private sealed class Exit(Action exit) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) exit(); }
    }
}
