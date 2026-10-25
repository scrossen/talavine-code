using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SwarmCode.App.Services;
using SwarmCode.Hosting;
using SwarmCode.Hosting.Projects;
using SwarmCode.Hosting.Tests;
using SwarmCore.Coding.Tools.BackgroundShell;

namespace SwarmCode.App.Tests;

public sealed class StarInvitationBusyTests
{
    [Fact]
    public async Task A_real_workspace_shell_defers_the_invitation_without_a_model_turn_until_stopped()
    {
        await using var ownedScratch = Talavine.TestSupport.TestTempDirectory.Create("code-star-invitation-busy");
        var paths = new SwarmCodeDataPaths(Path.Combine(ownedScratch.Path, "profile"));
        await File.WriteAllTextAsync(paths.UserConfigPath, """
            {"Coding":{"SelfImprovement":{"Enabled":false}},"CodingLanes":{"Lanes":[{"Name":"test","Provider":"Ollama","Model":"fixture"}]}}
            """);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            var state = new AppState(paths, new(paths), new(paths), new(paths),
                NullLoggerFactory.Instance, new EncryptedTestKeyStore());
            try
            {
                var project = ProjectFolders.Create(ownedScratch.Path, "Project");
                Assert.True(await state.OpenWorkspaceAsync(project, deadline.Token)
                    .WaitAsync(TimeSpan.FromSeconds(30)), state.LastError);
                var session = await state.NewSessionAsync(deadline.Token).WaitAsync(TimeSpan.FromSeconds(10));
                var runner = Assert.IsType<ConversationRunner>(await state.GetRunnerAsync(session.Id, deadline.Token)
                    .WaitAsync(TimeSpan.FromSeconds(10)));
                var shells = state.Workspace!.Services.GetRequiredService<BackgroundShellManager>();

                // Merely opening a session does not execute a model. The shell is the only work in this fixture.
                Assert.Empty(runner.Entries);
                Assert.False(runner.IsRunning);
                Assert.False(state.AnyTurnRunning);
                Assert.Equal(0, shells.RunningCount);
                Assert.False(state.HasActiveOrQueuedWork);
                var activityChanges = 0;
                state.Changed += () => Interlocked.Increment(ref activityChanges);

                try
                {
                    var shell = await shells.StartAsync(session.Id, BoundedSleepCommand(), project,
                        "invitation-busy-fixture", deadline.Token).WaitAsync(TimeSpan.FromSeconds(10));
                    var output = await shells.ReadAsync(session.Id, shell.Id,
                        "invitation-shell-ready", 5, deadline.Token).WaitAsync(TimeSpan.FromSeconds(10));

                    Assert.True(output.PatternMatched, output.Output);
                    Assert.True(output.Shell.IsRunning);
                    Assert.Equal(1, shells.RunningCount);
                    Assert.False(state.AnyTurnRunning);
                    Assert.Empty(runner.Entries);
                    Assert.True(state.HasActiveOrQueuedWork);
                    Assert.True(Volatile.Read(ref activityChanges) > 0);

                    var beforeStop = Volatile.Read(ref activityChanges);
                    var stopped = await Task.Run(() => shells.Stop(session.Id, shell.Id))
                        .WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.Equal(BackgroundShellStatus.Stopped, stopped.Status);
                    Assert.Equal(0, shells.RunningCount);
                    Assert.False(state.HasActiveOrQueuedWork);
                    Assert.True(Volatile.Read(ref activityChanges) > beforeStop);
                    Assert.False(state.AnyTurnRunning);
                    Assert.Empty(runner.Entries);
                }
                finally
                {
                    // Only this fixture's conversation is stopped, including a start that timed out before returning.
                    await Task.Run(() => shells.StopAll(session.Id)).WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
            finally
            {
                await state.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
            }
        }
        finally
        {
            // Drain this disposed host's idle pool without disturbing concurrently running fixtures.
            using var servicesPool = new SqliteConnection($"Data Source={Path.Combine(paths.RootDirectory, "services.db")}");
            SqliteConnection.ClearPool(servicesPool);
        }
    }

    private static string BoundedSleepCommand() => OperatingSystem.IsWindows()
        ? $"echo invitation-shell-ready & \"{Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")}\" -NoLogo -NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\""
        : "echo invitation-shell-ready; /bin/sleep 30";
}
