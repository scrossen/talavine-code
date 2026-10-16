using SwarmCore.Coding.Secrets;
using SwarmCore.Secrets;

namespace SwarmCore.Coding.Tests;

/// <summary>
/// A typed credential never passes through the shared intake step in clear text, whatever the vault does:
/// a write that throws, returns no entry, or does not persist leaves <c>[redacted]</c> and a not-stored value.
/// </summary>
public sealed class SecretScrubFailureTests
{
    private const string Token = "ghp_abcdefghijklmnopqrstuvwxyz0123456789";

    public enum Failure { Throws, ReturnsNull, DoesNotPersist }

    internal sealed class FailingVault(Failure failure) : ISecretVault
    {
        public Task<SecretEntry> SetAsync(SecretWriteRequest request, CancellationToken ct = default) => failure switch
        {
            Failure.Throws => throw new IOException("vault database is locked"),
            Failure.ReturnsNull => Task.FromResult<SecretEntry>(null!),
            _ => Task.FromResult(new SecretEntry(SecretNames.Normalize(request.Name), request.Kind, null, null, false, DateTime.UtcNow, DateTime.UtcNow, null, true)),
        };
        public Task<string?> GetValueAsync(string name, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<SecretLogin?> GetLoginAsync(string name, CancellationToken ct = default) => Task.FromResult<SecretLogin?>(null);
        public Task<SecretEntry?> GetEntryAsync(string name, CancellationToken ct = default) => Task.FromResult<SecretEntry?>(null);
        public Task<bool> HasAsync(string name, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> DeleteAsync(string name, CancellationToken ct = default) => Task.FromResult(false);
        public Task<IReadOnlyList<SecretEntry>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SecretEntry>>([]);
    }

    [Theory]
    [InlineData(Failure.Throws)]
    [InlineData(Failure.ReturnsNull)]
    [InlineData(Failure.DoesNotPersist)]
    public async Task A_credential_the_vault_could_not_store_is_redacted_not_passed_through(Failure failure)
    {
        var input = $"my github token is {Token}, use it to push";

        var result = await SecretMessageScrubber.ScrubAsync(new FailingVault(failure), input);

        Assert.DoesNotContain(Token, result.Text);
        Assert.Contains(SecretMessageScrubber.RedactedPlaceholder, result.Text);
        Assert.DoesNotContain("{{secret:", result.Text);
        Assert.True(result.Changed); // the text differs from the input even though nothing was stored
        Assert.True(result.CredentialsDetected);
        Assert.Empty(result.StoredNames);
        Assert.Equal(Token, Assert.Single(result.UnstoredValues));
        Assert.Equal(Token, Assert.Single(result.DetectedValues));
    }

    [Fact]
    public void Redact_without_a_vault_removes_every_detected_credential()
    {
        var result = SecretMessageScrubber.Redact($"token {Token} please", SecretScrubOptions.Governed);

        Assert.DoesNotContain(Token, result.Text);
        Assert.True(result.Changed);
        Assert.Equal(Token, Assert.Single(result.UnstoredValues));
        Assert.False(SecretMessageScrubber.Redact("nothing here", SecretScrubOptions.Governed).Changed);
    }
}
