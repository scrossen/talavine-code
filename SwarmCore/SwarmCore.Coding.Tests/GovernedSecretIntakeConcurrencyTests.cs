using System.Collections.Concurrent;
using SwarmCore.Coding.Secrets;
using SwarmCore.Secrets;

namespace SwarmCore.Coding.Tests;

public sealed class GovernedSecretIntakeConcurrencyTests
{
    private const string First = "ghp_abcdefghijklmnopqrstuvwxyz0123456789";
    private const string Second = "ghp_zyxwvutsrqponmlkjihgfedcba9876543210"; // gitleaks:allow -- synthetic test fixture, never a live credential

    [Fact]
    public async Task Two_conversations_using_distinct_adapters_cannot_replace_each_others_confirmed_value()
    {
        var values = new ConcurrentDictionary<string, string>();
        var firstReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = new Vault(values, firstReady, secondReady);
        var b = new Vault(values, secondReady, firstReady);
        var first = SecretMessageScrubber.ScrubAsync(a, "Store github token " + First);
        var second = SecretMessageScrubber.ScrubAsync(b, "Store github token " + Second);
        var result = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        var nameA = Assert.Single(result[0].StoredNames);
        var nameB = Assert.Single(result[1].StoredNames);
        Assert.NotEqual(nameA, nameB);
        Assert.Equal(First, values[nameA]);
        Assert.Equal(Second, values[nameB]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Canceled_waiter_and_failed_checkpoint_release_governed_ownership(bool cancelCheckpoint)
    {
        var values = new ConcurrentDictionary<string, string>();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var ownerCancellation = new CancellationTokenSource();
        var owner = SecretMessageScrubber.ScrubAsync(new Vault(values), "Store github token " + First,
            SecretScrubOptions.Governed, ownerCancellation.Token, async (_, _, ct) =>
            {
                held.TrySetResult();
                await release.Task.WaitAsync(ct);
                throw new IOException("checkpoint unavailable");
            });
        try
        {
            await held.Task.WaitAsync(TimeSpan.FromSeconds(3));
            using var canceled = new CancellationTokenSource();
            var waiter = SecretMessageScrubber.ScrubAsync(new Vault(values),
                "Store github token " + Second, SecretScrubOptions.Governed, canceled.Token);
            await Task.Delay(50);
            Assert.False(waiter.IsCompleted);
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter.WaitAsync(TimeSpan.FromSeconds(3)));
            if (cancelCheckpoint) ownerCancellation.Cancel(); else release.TrySetResult();
            if (cancelCheckpoint) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner);
            else await Assert.ThrowsAsync<IOException>(() => owner);
            var next = await SecretMessageScrubber.ScrubAsync(new Vault(values), "Store github token " + Second).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(Second, values[Assert.Single(next.StoredNames)]);
            Assert.Contains(First, values.Values);
        }
        finally
        {
            release.TrySetResult(); ownerCancellation.Cancel();
            try { await owner.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        }
    }

    private sealed class Vault(ConcurrentDictionary<string, string> values,
        TaskCompletionSource? ready = null, TaskCompletionSource? peer = null) : ISecretVault
    {
        private int probes;
        public async Task<bool> HasAsync(string name, CancellationToken ct = default)
        {
            var exists = values.ContainsKey(name);
            if (!exists && Interlocked.Increment(ref probes) == 2 && ready is not null)
            {
                ready.TrySetResult();
                await Task.WhenAny(peer!.Task, Task.Delay(500, ct));
                ct.ThrowIfCancellationRequested();
            }
            return exists;
        }
        public Task<SecretEntry> SetAsync(SecretWriteRequest r, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); values[r.Name] = r.Value!;
            return Task.FromResult(new SecretEntry(r.Name, r.Kind, null, null, false, DateTime.UtcNow, DateTime.UtcNow, null, true));
        }
        public Task<string?> GetValueAsync(string n, CancellationToken ct = default) => Task.FromResult(values.GetValueOrDefault(n));
        public Task<SecretLogin?> GetLoginAsync(string n, CancellationToken ct = default) => Task.FromResult<SecretLogin?>(null);
        public Task<SecretEntry?> GetEntryAsync(string n, CancellationToken ct = default) => Task.FromResult<SecretEntry?>(null);
        public Task<bool> DeleteAsync(string n, CancellationToken ct = default) => Task.FromResult(values.TryRemove(n, out _));
        public Task<IReadOnlyList<SecretEntry>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SecretEntry>>([]);
    }
}
