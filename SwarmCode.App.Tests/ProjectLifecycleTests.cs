using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SwarmCode.App.Services;
using SwarmCode.Hosting;
using SwarmCode.Hosting.Llm;
using SwarmCode.Hosting.Projects;
using SwarmCode.Hosting.Tests;

namespace SwarmCode.App.Tests;

public sealed class ProjectLifecycleTests
{
    [Fact]
    public async Task Closing_a_project_keeps_history_and_new_chats_use_the_separate_workspace_after_restart()
    {
        await using var ownedScratch = Talavine.TestSupport.TestTempDirectory.Create("code-project-lifecycle");
        var scratch = ownedScratch.Path;
        var paths = new SwarmCodeDataPaths(Path.Combine(scratch, "profile"));
        await File.WriteAllTextAsync(paths.UserConfigPath, """
            {"Coding":{"SelfImprovement":{"Enabled":false}},"CodingLanes":{"Lanes":[{"Name":"test","Provider":"Ollama","Model":"fixture"}]}}
            """);
        var keyStore = new EncryptedTestKeyStore();
        AppState State() => new(paths, new(paths), new(paths), new(paths), NullLoggerFactory.Instance, keyStore);
        try
        {
            string projectChatId, chatId;
            var project = ProjectFolders.Create(scratch, "Project");
            await using (var state = State())
            {
                var first = await state.NewSessionAsync();
                Assert.False(state.HasProject);
                Assert.Equal(paths.ChatWorkspaceDirectory, first.RepositoryRoot);
                Assert.True(await state.OpenWorkspaceAsync(project), state.LastError);
                Assert.True(state.HasProject);
                projectChatId = (await state.NewSessionAsync()).Id;
                await state.CloseProjectAsync();
                Assert.Null(state.Workspace);
                Assert.Null(state.Preferences.Current.LastRepositoryRoot);
                Assert.Null(state.Preferences.Current.LastSessionId);
                Assert.True(Directory.Exists(project));
                Assert.NotNull(await state.Sessions.GetAsync(projectChatId));
                var chat = await state.NewSessionAsync();
                chatId = chat.Id;
                Assert.Equal(paths.ChatWorkspaceDirectory, chat.RepositoryRoot);
                Assert.False(state.HasProject);
                Assert.Equal("No project", state.ProjectName(state.RepositoryRoot));
                Assert.Equal(project, Assert.Single(await state.Sessions.RecentRepositoriesAsync()));
            }
            await using (var restarted = State())
            {
                Assert.Equal(paths.ChatWorkspaceDirectory, restarted.Preferences.Current.LastRepositoryRoot);
                Assert.True(await restarted.OpenWorkspaceAsync(restarted.Preferences.Current.LastRepositoryRoot!));
                Assert.False(restarted.HasProject);
                Assert.NotNull(await restarted.GetRunnerAsync(chatId));
                Assert.NotNull(await restarted.Sessions.GetAsync(projectChatId));
                Assert.True(await restarted.OpenWorkspaceAsync(project));
                Assert.NotNull(await restarted.GetRunnerAsync(projectChatId));
            }
        }
        finally
        {
            // Dispose the app hosts first, then drain only this fixture's idle service-store pool.
            // Active leaked connections still keep cleanup failing; other fixtures' pools are untouched.
            using var servicesPool = new SqliteConnection($"Data Source={Path.Combine(paths.RootDirectory, "services.db")}");
            SqliteConnection.ClearPool(servicesPool);
        }
    }
}
