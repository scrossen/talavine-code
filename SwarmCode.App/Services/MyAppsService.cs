using SwarmCode.Hosting;
using SwarmCode.Hosting.Projects;
using SwarmCore.Agent.Catalog;
using SwarmCore.Coding.Permissions;
using SwarmCore.Coding.Projects;

namespace SwarmCode.App.Services;

/// <summary>Desktop adapters: native folder selection, project-bound chats, current permissions and preview opening.</summary>
public sealed class MyAppsService(AppState state, CodingAppLibraryStore library, CodingAppRuntime runtime)
{
    public CodingAppLibraryStore Library => library;
    public CodingAppRuntime Runtime => runtime;

    public async Task<WorkspaceSessionInfo> WorkAsync(string id, string? request = null, bool newSession = false)
    {
        if (state.IsOpeningWorkspace) throw new InvalidOperationException("Wait for the current project to finish opening.");
        var app = await Task.Run(() => library.Get(id));
        if (!Directory.Exists(app.Root)) throw new DirectoryNotFoundException("This app folder is missing. Restore it, or add its new location.");
        EnsureCanSwitch(app.Root);
        if (state.RepositoryRoot is null || !CodingAppPaths.Same(state.RepositoryRoot, app.Root))
            if (!await state.OpenWorkspaceAsync(app.Root)) throw new InvalidOperationException(state.LastError ?? "The app workspace could not open.");
        var sessions = await Task.Run(() => state.Sessions.ListAsync(app.Root));
        if (request is not null && sessions.Any(s => state.TryGetRunner(s.Id)?.IsRunning == true))
            throw new InvalidOperationException("This app's chat is still working. Open it or wait for it to finish before starting another build or launch setup.");
        var session = newSession ? null : sessions.Where(s => s.ParentSessionId is null).OrderByDescending(s => s.LastActivityUtc).FirstOrDefault();
        session ??= await state.NewSessionAsync();
        var runner = await state.GetRunnerAsync(session.Id) ?? throw new InvalidOperationException("The app chat could not open.");
        if (request is not null)
        {
            var prefs = state.Preferences.Current;
            if (!runner.TrySend(request, session.LaneName, prefs.Mode, prefs.HandOffOnRefusal,
                    session.Selection?.Model ?? prefs.DefaultModel))
                throw new InvalidOperationException("This app's chat is already working. Open it to continue.");
        }
        return session;
    }

    public async Task<WorkspaceSessionInfo> CreateAsync(string name, string description, string folder)
    {
        EnsureCanSwitch(CodingAppPaths.Within(folder, CodingAppPaths.FolderName(name)));
        var app = await Task.Run(() => library.Create(name, description, folder));
        return await WorkAsync(app.Id, CodingAppPrompts.Create(app), newSession: true);
    }

    private void EnsureCanSwitch(string root)
    {
        if (state.RepositoryRoot is { } current && !CodingAppPaths.Same(current, root)
            && (state.AnyTurnRunning || state.Agents.RunningCount > 0))
            throw new InvalidOperationException("Finish or stop the current chat and its agents before switching to another app. Your project has been kept.");
    }

    public CodingPermissionDecision RunPermission()
    {
        var rules = state.UserSettings.ReadPermissionRules();
        var policy = CodingPermissionPolicy.From(state.Preferences.Current.Mode, rules);
        // A project launch is arbitrary execution; shell deny rules apply as well as apps.* rules.
        return CodingAppLaunchAuthority.Decide(policy);
    }

    public async Task RunAsync(string id, string? approvedPlan = null)
    {
        var app = await Task.Run(() => library.Get(id));
        var permission = await Task.Run(RunPermission);
        if (permission.Verdict == CodingPermissionVerdict.Deny) throw new InvalidOperationException(permission.Reason);
        if (permission.Verdict == CodingPermissionVerdict.Ask && approvedPlan is null) throw new InvalidOperationException("Review and allow this app's launch first.");
        if (runtime.Status(id) is { State: CodingAppRunState.Running, Url: { } runningUrl })
        { await OpenPreviewAsync(runningUrl); return; }
        if (runtime.IsBusy(id)) return;
        _ = runtime.StartAsync(app, approvedPlan: approvedPlan, authorize: _ =>
        {
            var currentApp = library.Get(id); // A removed or relocated app cannot dispatch a delayed launch.
            if (!CodingAppPaths.Same(currentApp.Root, app.Root)) throw new InvalidOperationException("This app's folder changed. Open its current project before running it.");
            var current = RunPermission();
            if (current.Verdict == CodingPermissionVerdict.Deny || current.Verdict == CodingPermissionVerdict.Ask && approvedPlan is null)
                throw new InvalidOperationException("Launch permissions changed. Review the current settings before running this app.");
            return Task.CompletedTask;
        });
        while (runtime.Status(id).State == CodingAppRunState.Starting) await Task.Delay(100);
        if (runtime.Status(id) is { State: CodingAppRunState.Running, Url: { } url }) await OpenPreviewAsync(url);
    }

    public static async Task OpenPreviewAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsLoopback || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Only a local app preview can open here.");
        if (!await Browser.Default.OpenAsync(uri, BrowserLaunchMode.External))
            throw new InvalidOperationException("The app is running, but its preview could not open. Open its local address in your browser.");
    }

    public async Task RemoveAsync(string id)
    {
        await runtime.StopAsync(id);
        await Task.Run(() => library.Remove(id));
    }
}
