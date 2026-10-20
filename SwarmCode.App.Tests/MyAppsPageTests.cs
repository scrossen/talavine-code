using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SwarmCode.App.Services;
using SwarmCode.App.Tests.Components;
using SwarmCode.Hosting;
using SwarmCode.Hosting.Projects;
using SwarmCode.Hosting.Tests;
using SwarmCore.Coding.Projects;
using Talavine.TestSupport;

namespace SwarmCode.App.Tests;

public sealed class MyAppsPageTests
{
    [Fact]
    public async Task Library_separates_created_apps_and_project_history_and_searches_both()
    {
        await using var scratch = TestTempDirectory.Create("code-library-page");
        var paths = new SwarmCodeDataPaths(Path.Combine(scratch.Path, "profile"));
        var library = new CodingAppLibraryStore(paths);
        library.SetFolder(scratch.Path, null);
        var app = library.Create("Garden", "Track planting", scratch.Path);
        var projectRoot = Directory.CreateDirectory(Path.Combine(scratch.Path, "Multi app repo")).FullName;
        var project = library.Add(projectRoot);
        var recentRoot = Directory.CreateDirectory(Path.Combine(scratch.Path, "Documents")).FullName;
        await using var state = new AppState(paths, new(paths), new(paths), new(paths), NullLoggerFactory.Instance, new EncryptedTestKeyStore());
        await state.Sessions.RememberRepositoryAsync(app.Root);
        await state.Sessions.RememberRepositoryAsync(projectRoot);
        await state.Sessions.RememberRepositoryAsync(recentRoot);
        var internalRoot = Directory.CreateDirectory(Path.Combine(paths.WorktreesDirectory, "repo", "agent")).FullName;
        await state.Sessions.RememberRepositoryAsync(internalRoot);
        await state.Sessions.CreateAsync(paths.ChatWorkspaceDirectory);
        var store = new FileAppProcedureStore(paths);
        await using var runtime = new CodingAppRuntime(store, paths.RootDirectory, store.AcquireRunLease);
        await using var context = new BunitContext();
        context.Services.AddSingleton(state);
        context.Services.AddSingleton(new MyAppsService(state, library, runtime));
        context.Services.AddSingleton<IFolderPickerService, NoOpFolderPickerService>();
        context.Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        context.Services.AddSingleton<IdeIntegration>();

        var view = context.Render<MyApps>();
        view.WaitForAssertion(() => Assert.Equal(3, view.FindAll("article").Count));
        Assert.Equal("My Apps and Projects", view.Find("h1").TextContent);
        Assert.Single(view.FindAll("article"), e => e.TextContent.Contains("Garden"));
        Assert.Single(view.FindAll("button"), e => e.TextContent == "Run app");
        Assert.Equal(2, view.FindAll("button").Count(e => e.TextContent == "Open project"));
        Assert.DoesNotContain("chat-workspace", view.Markup);
        Assert.DoesNotContain(internalRoot, view.Markup);
        Assert.Null(state.Workspace);
        Assert.False(state.AnyTurnRunning);

        await view.Find("input[type=search]").InputAsync(new() { Value = "Documents" });
        Assert.Single(view.FindAll("article"));
        Assert.DoesNotContain("Run app", view.Find("article").TextContent);
        Assert.Contains("Documents", view.Find("article").TextContent);

        await view.Find("input[type=search]").InputAsync(new() { Value = "planting" });
        Assert.Single(view.FindAll("article"));
        Assert.Contains("Garden", view.Find("article").TextContent);
        Assert.Equal(2, library.Read().Apps.Count); // Browsing does not import history.

        view.Render(p => p.Add(x => x.AppId, project.Id));
        view.WaitForAssertion(() => Assert.Contains("several apps", view.Markup));
        Assert.DoesNotContain(view.FindAll("button"), e => e.TextContent == "Run app");
        Assert.Contains("Manage entry", view.Markup);
    }
}
