using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Procedures;
using SwarmCore.Abstractions.Processes;

namespace SwarmCore.Coding.Projects;

public enum CodingAppRunState { Stopped, Starting, Running, Completed, Failed }
public sealed record CodingAppRunStatus(CodingAppRunState State, string Message, string? Url = null, bool Replayed = false);

/// <summary>Owns app processes independently of chat/workspace navigation. Model-authored launch plans are
/// validated against fresh local evidence before the shared learned-procedure runtime can promote/replay them.</summary>
public sealed class CodingAppRuntime(IAgentProcedureStore procedures, string profileScope, Func<string, IDisposable> acquireLease) : IAsyncDisposable
{
    private sealed class Run : IDisposable
    {
        public readonly CancellationTokenSource Stop = new();
        public readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Task => Completion.Task;
        public IChildProcess? Process;
        public void Dispose() { Process?.Dispose(); Stop.Dispose(); }
    }
    private readonly ConcurrentDictionary<string, Run> _runs = new();
    private readonly ConcurrentDictionary<string, CodingAppRunStatus> _statuses = new();
    private readonly object _lifecycle = new();
    private int _disposed;
    public event Action? Changed;
    public CodingAppRunStatus Status(string id) => _statuses.GetValueOrDefault(id) ?? new(CodingAppRunState.Stopped, "Not running");
    public bool IsBusy(string id) => _runs.ContainsKey(id);
    private void Notify()
    {
        foreach (var handler in Changed?.GetInvocationList() ?? [])
            try { ((Action)handler)(); } catch { /* A departing UI observer cannot interrupt process cleanup. */ }
    }
    private void Set(string id, CodingAppRunStatus status) { _statuses[id] = status; Notify(); }

    /// <summary>The caller authenticates current user intent and policy before calling. Never automatically retries a launch.</summary>
    public Task StartAsync(CodingAppProject app, CancellationToken ct = default, string? approvedPlan = null, Func<CancellationToken, Task>? authorize = null)
    {
        Run run;
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            run = new Run();
            if (!_runs.TryAdd(app.Id, run)) { run.Dispose(); return Task.CompletedTask; }
        }
        Set(app.Id, new(CodingAppRunState.Starting, "Checking how this app runs…"));
        _ = Task.Run(() => RunAsync(app, run, approvedPlan, authorize, ct), CancellationToken.None);
        return run.Task; // Completes when the app stops; UI starts it without awaiting its entire lifetime.
    }

    private async Task RunAsync(CodingAppProject app, Run run, string? approvedPlan, Func<CancellationToken, Task>? authorize, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, run.Stop.Token);
        var token = linked.Token;
        IDisposable? lease = null;
        ChildProcessLifetimeContainment? containment = null;
        Process? diagnostics = null;
        AgentProcedureRuntime? routine = null;
        AgentProcedureContext? context = null;
        var invocation = Guid.NewGuid().ToString("N");
        var dispatched = false;
        var checkpointCreated = false;
        var recorded = false;
        var replayed = false;
        JsonObject arguments = new();
        try
        {
            Set(app.Id, new(CodingAppRunState.Starting, "Checking how this app runs…"));
            lease = acquireLease(app.Id);
            var workloadKey = "swarmcode-app-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profileScope + "\n" + app.Id)));
            var sweep = await ChildProcessLifetimeContainment.ReapOrphansAsync(workloadKey, ct: token).ConfigureAwait(false);
            if (!sweep.Succeeded || sweep.LiveOwnerMarkers > 0 || sweep.UnverifiableMarkers > 0)
                throw new InvalidOperationException("A previous app process could not be proven stopped. Inspect its current run before launching again.");
            var plan = CodingAppLaunchFile.Read(app.Root) ?? throw new InvalidOperationException("Set up Run app in a project chat first.");
            if (approvedPlan is not null && approvedPlan != JsonSerializer.Serialize(plan, CodingAppLaunchFile.Json))
                throw new InvalidOperationException("The launch command changed after review. Review the current command before running it.");
            var fingerprint = Fingerprint(app.Root, plan);
            var descriptor = ToolDescriptor.Create<JsonObject, JsonObject>("apps.launch", "app-launch", "Launch the current app with fresh readiness verification.",
                ToolCostClass.Cheap, ToolSideEffect.Exec, (_, _) => throw new NotSupportedException()) with { ImplementationFingerprint = "app-launch-v1" };
            arguments = new JsonObject { ["projectRoot"] = app.Root, ["plan"] = JsonSerializer.SerializeToNode(plan, CodingAppLaunchFile.Json) };
            context = new AgentProcedureContext
            {
                Key = new(profileScope, "app-launch", app.Id), Store = procedures,
                BindingFingerprint = fingerprint, Inputs = arguments.DeepClone().AsObject(),
                AllowTool = (tool, args) => tool.ToolId == descriptor.ToolId && JsonNode.DeepEquals(args, arguments),
                CheckPreconditionAsync = (_, _, _) => Task.FromResult(Fingerprint(app.Root, plan) == fingerprint),
                VerifyAsync = (evidence, _) => Task.FromResult<AgentProcedureVerification?>(evidence.Receipts.Count == 1
                    && evidence.Receipts[0] is { IsError: false, HostCompletionVerified: true }
                    && Fingerprint(app.Root, plan) == fingerprint
                    ? new("app-launch-readiness-v1", fingerprint, DateTimeOffset.UtcNow) : null),
                FormatResult = _ => "App launch verified."
            };
            routine = await AgentProcedureRuntime.StartAsync(context, [descriptor], token).ConfigureAwait(false);
            if (routine.StopReason is { } stop) throw new InvalidOperationException(stop);
            replayed = routine.IsReplaying;
            if (replayed && await routine.NextAsync(token).ConfigureAwait(false) is null)
                throw new InvalidOperationException("This app changed. Set up Run app again using its current files.");
            if (Fingerprint(app.Root, plan) != fingerprint) throw new InvalidOperationException("This app changed while preparing to run. Try again.");
            if (plan.ReadyWhen == "http" && await PortOpenAsync(new Uri(plan.Url!), token).ConfigureAwait(false))
                throw new InvalidOperationException("The preview port is already in use. Stop the other server or set up a different port.");
            token.ThrowIfCancellationRequested();
            if (authorize is not null) await authorize(token).ConfigureAwait(false);
            await routine.BeforeDispatchAsync(invocation, token).ConfigureAwait(false);
            checkpointCreated = true;
            var workingDirectory = CodingAppPaths.Within(app.Root, plan.WorkingDirectory);
            var program = !Path.IsPathRooted(plan.Program) && (plan.Program.Contains('/') || plan.Program.Contains('\\'))
                ? CodingAppPaths.Within(app.Root, Path.Combine(plan.WorkingDirectory, plan.Program)) : plan.Program;
            var start = new ProcessStartInfo(program)
            {
                WorkingDirectory = workingDirectory, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var argument in plan.Arguments) start.ArgumentList.Add(argument);
            // Never retain app output: it may contain credentials. Drain both pipes to prevent child deadlocks.
            var process = run.Process = ChildProcessLauncher.Start(Native.NativeLaneSupport.ForLaunch(start));
            dispatched = true;
            diagnostics = process.AttachDiagnosticsProcess();
            containment = diagnostics is null ? null : ChildProcessLifetimeContainment.Attach(diagnostics, workloadKey);
            process.OutputLineReceived += _ => { };
            process.ErrorLineReceived += _ => { };
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            using (var readiness = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                readiness.CancelAfter(TimeSpan.FromSeconds(plan.TimeoutSeconds));
                try { await VerifyReadyAsync(process, plan, readiness.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new InvalidOperationException("The app did not become ready in time. Use Set up Run app to diagnose its launch."); }
            }
            await routine.RecordAsync(new(descriptor.ToolId, arguments, new JsonObject { ["verified"] = true }, false, true, true,
                HostCompletionVerified: true, OutcomeUncertain: false, InvocationId: invocation), CancellationToken.None).ConfigureAwait(false);
            recorded = true;
            if (await routine.TryCompleteAsync(CancellationToken.None).ConfigureAwait(false) is null || !await context.AcceptAsync(CancellationToken.None).ConfigureAwait(false))
                throw new InvalidOperationException("The verified launch could not be saved because the project changed. Run setup again.");
            if (plan.ReadyWhen == "exit")
                Set(app.Id, new(CodingAppRunState.Completed, "Finished successfully", Replayed: replayed));
            else
            {
                Set(app.Id, new(CodingAppRunState.Running, plan.ReadyWhen == "http" ? "Ready to open" : "Started · running process", plan.Url, replayed));
                await process.WaitForExitAsync(token).ConfigureAwait(false);
                Set(app.Id, new(process.ExitCode == 0 ? CodingAppRunState.Stopped : CodingAppRunState.Failed,
                    process.ExitCode == 0 ? "App closed" : $"App exited with code {process.ExitCode}. Set up Run app to diagnose it."));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { Set(app.Id, new(CodingAppRunState.Stopped, "Stopped")); }
        catch (Exception ex) { Set(app.Id, new(CodingAppRunState.Failed, ex.Message)); }
        finally
        {
            var stopped = true;
            if (run.Process is { } process && dispatched)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch { stopped = false; }
            }
            if (routine is not null && !recorded)
            {
                try
                {
                    await routine.RecordAsync(new("apps.launch", arguments, null, true, dispatched, true,
                        OutcomeUncertain: !stopped, InvocationId: invocation), CancellationToken.None).ConfigureAwait(false);
                }
                catch { /* A retained uncertainty checkpoint prevents replay after an incomplete receipt. */ }
            }
            // Reconcile only this attempt, after observing its owned child exit. Never release a barrier from
            // an earlier crashed attempt: a missing receipt cannot prove what that earlier process did.
            if (checkpointCreated && stopped && context is not null)
            {
                try
                {
                    var history = await procedures.GetAsync(context.Key, CancellationToken.None).ConfigureAwait(false);
                    if (history?.ActiveRevisionId is null && history?.Revisions.LastOrDefault()?.FailureCode is "outcome_uncertain" or "effect_checkpoint_required")
                        await AgentProcedureRuntime.ResolveUncertaintyAsync(procedures, context.Key, history.Version,
                            new("owned-app-process-stopped-v1", invocation, DateTimeOffset.UtcNow), CancellationToken.None).ConfigureAwait(false);
                }
                catch { /* A failed inspection commit retains the barrier. */ }
            }
            lease?.Dispose();
            containment?.Dispose();
            diagnostics?.Dispose();
            if (!stopped) Set(app.Id, new(CodingAppRunState.Failed, "The app process could not be proven stopped. Inspect its run before trying again."));
            _runs.TryRemove(app.Id, out _);
            run.Dispose();
            run.Completion.TrySetResult();
            Notify();
        }
    }

    public async Task StopAsync(string id)
    {
        if (!_runs.TryGetValue(id, out var run)) return;
        try { run.Stop.Cancel(); } catch (ObjectDisposedException) { return; }
        await run.Task.ConfigureAwait(false);
    }

    private static async Task VerifyReadyAsync(IChildProcess process, CodingAppLaunchPlan plan, CancellationToken ct)
    {
        if (plan.ReadyWhen == "exit")
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException($"The app exited with code {process.ExitCode}.");
            return;
        }
        if (plan.ReadyWhen == "process")
        {
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1000, plan.TimeoutSeconds * 500)), ct).ConfigureAwait(false);
            if (process.HasExited) throw new InvalidOperationException($"The app closed before startup could be verified (exit {process.ExitCode}).");
            return;
        }
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited) throw new InvalidOperationException($"The app exited before its preview was ready (exit {process.ExitCode}).");
            try
            {
                using var response = await client.GetAsync(plan.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode && !process.HasExited) return;
            }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
    }

    private static async Task<bool> PortOpenAsync(Uri uri, CancellationToken ct)
    {
        using var client = new TcpClient();
        try { await client.ConnectAsync(uri.Host, uri.Port, ct).AsTask().WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); return true; }
        catch (SocketException) { return false; }
        catch (TimeoutException) { return false; }
    }

    private static string Fingerprint(string root, CodingAppLaunchPlan plan)
    {
        var data = new StringBuilder(CodingAppPaths.Normalize(root)).Append(JsonSerializer.Serialize(plan, CodingAppLaunchFile.Json));
        data.Append(File.ReadAllText(CodingAppPaths.Within(root, CodingAppLaunchFile.RelativePath)));
        // Dependency/launch-contract changes invalidate replay. Source edits are verified again by readiness each run.
        foreach (var file in new[] { "package.json", "package-lock.json", "pnpm-lock.yaml", "yarn.lock", "requirements.txt", "pyproject.toml", "Cargo.toml", "Cargo.lock", "global.json" }
            .Concat(Directory.EnumerateFiles(root, "*.csproj").Select(Path.GetFileName)!))
        {
            var path = CodingAppPaths.Within(root, file!);
            data.Append(file).Append(File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "missing");
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data.ToString())));
    }

    public async ValueTask DisposeAsync()
    {
        string[] ids;
        lock (_lifecycle)
        {
            if (_disposed != 0) return;
            _disposed = 1;
            ids = _runs.Keys.ToArray();
        }
        foreach (var id in ids) await StopAsync(id).ConfigureAwait(false);
    }
}
