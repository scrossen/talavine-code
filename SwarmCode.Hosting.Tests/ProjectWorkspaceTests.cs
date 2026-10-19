using SwarmCode.Hosting.Projects;

namespace SwarmCode.Hosting.Tests;

public sealed class ProjectWorkspaceTests : TempDirTest
{
    [Fact]
    public async Task Chats_without_project_survive_restart_and_never_enter_recent_projects()
    {
        var project = ProjectFolders.Create(Tmp, "A project");
        var catalog = new WorkspaceSessionCatalog(Paths);
        var projectChat = await catalog.CreateAsync(project);
        var chat = await catalog.CreateAsync(Paths.ChatWorkspaceDirectory);
        await catalog.RememberRepositoryAsync(Paths.ChatWorkspaceDirectory);
        var reopened = new WorkspaceSessionCatalog(new SwarmCodeDataPaths(Paths.RootDirectory));
        Assert.Equal(chat.Id, Assert.Single(await reopened.ListAsync(Paths.ChatWorkspaceDirectory)).Id);
        Assert.Equal(projectChat.Id, Assert.Single(await reopened.ListAsync(project)).Id);
        Assert.Equal(project, Assert.Single(await reopened.RecentRepositoriesAsync()));
        Assert.False(Paths.IsChatWorkspace(project));
        Assert.False(Paths.IsChatWorkspace(Paths.RootDirectory));
        Assert.False(Paths.IsChatWorkspace(Paths.ChatWorkspaceDirectory + "-other"));
        Assert.True(Paths.IsChatWorkspace(Paths.ChatWorkspaceDirectory + Path.DirectorySeparatorChar));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("../elsewhere")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public void New_project_rejects_names_that_escape_the_chosen_location(string name)
        => Assert.Throws<ArgumentException>(() => ProjectFolders.Create(Tmp, name));

    [Fact]
    public void New_project_keeps_existing_files_and_projects_intact()
    {
        var project = ProjectFolders.Create(Tmp, "Example");
        var file = Path.Combine(project, "keep.txt");
        File.WriteAllText(file, "keep");
        Assert.Throws<IOException>(() => ProjectFolders.Create(Tmp, "Example"));
        Assert.Equal("keep", File.ReadAllText(file));
        Assert.Throws<IOException>(() => ProjectFolders.Create(project, "keep.txt"));
        Assert.Throws<DirectoryNotFoundException>(() => ProjectFolders.Create(Path.Combine(Tmp, "missing"), "Example"));
    }
}
