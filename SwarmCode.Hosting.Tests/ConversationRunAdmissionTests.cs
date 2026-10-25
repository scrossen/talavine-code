namespace SwarmCode.Hosting.Tests;

public sealed class ConversationRunAdmissionTests
{
    [Fact]
    public void Existing_turn_wins_and_idle_restart_excludes_new_turns_until_aborted()
    {
        var gate = new ConversationRunAdmission();
        var turn = gate.TryEnterTurn(); Assert.NotNull(turn); Assert.Null(gate.TryQuiesce());
        turn!.Dispose(); turn.Dispose();
        var restart = gate.TryQuiesce(); Assert.NotNull(restart);
        Assert.Null(gate.TryEnterTurn()); Assert.Null(gate.TryQuiesce());
        restart!.Dispose(); restart.Dispose();
        using var next = gate.TryEnterTurn(); Assert.NotNull(next);
    }

    [Fact]
    public async Task Concurrent_restart_and_turn_never_both_win_admission()
    {
        for (var i = 0; i < 100; i++)
        {
            var gate = new ConversationRunAdmission();
            using var barrier = new Barrier(2);
            IDisposable? turn = null; ConversationRunAdmission.RestartAdmission? restart = null;
            await Task.WhenAll(Task.Run(() => { barrier.SignalAndWait(); turn = gate.TryEnterTurn(); }),
                Task.Run(() => { barrier.SignalAndWait(); restart = gate.TryQuiesce(); }));
            Assert.True((turn is null) != (restart is null));
            turn?.Dispose(); restart?.Dispose();
        }
    }

    [Fact]
    public async Task Helper_completion_releases_failed_quit_without_old_cleanup_releasing_a_new_restart()
    {
        var gate = new ConversationRunAdmission();
        var first = gate.TryQuiesce()!;
        var helperExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = Task.Run(async () => { try { await helperExited.Task; } finally { first.Dispose(); } });
        Assert.Null(gate.TryEnterTurn());
        helperExited.SetResult(); await lifetime;
        using (var turn = gate.TryEnterTurn()) Assert.NotNull(turn);
        using var second = gate.TryQuiesce(); Assert.NotNull(second);
        first.Dispose(); Assert.Null(gate.TryEnterTurn());
    }
}
