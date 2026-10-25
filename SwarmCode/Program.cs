using System.Reflection;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwarmCode;
using SwarmCode.Hosting;
using SwarmCode.Hosting.Secrets;
using SwarmCode.Ui;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding;
using SwarmCore.Coding.Routing;
using SwarmCore.LlmRouting;
using SwarmCore.LlmRouting.Configuration;

try { Console.OutputEncoding = new UTF8Encoding(false); } catch (IOException) { }

var cli = CliOptions.Parse(args);
if (cli.Error is not null)
{
    Console.Error.WriteLine(cli.Error);
    return 2;
}
// This private callback must not resolve a repository, profile, credentials or providers.
if (cli.GoalMessageHook is { } goalHook)
    return await SwarmCore.Coding.Native.ClaudeGoalAttentionHooks.RunHelperAsync(goalHook, Console.In, Console.Out);
if (cli.Help)
{
    Console.WriteLine(CliOptions.HelpText);
    return 0;
}
if (cli.Version)
{
    var version = typeof(CliOptions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                  ?? typeof(CliOptions).Assembly.GetName().Version?.ToString() ?? "0.0.0";
    Console.WriteLine($"Talavine Code {version.Split('+')[0]}");
    return 0;
}

string repoRoot;
try
{
    repoRoot = Path.GetFullPath(cli.Repo ?? Directory.GetCurrentDirectory());
    if (!Directory.Exists(repoRoot))
    {
        Console.Error.WriteLine($"Repository path does not exist: {repoRoot}");
        return 2;
    }
}
catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
{
    Console.Error.WriteLine($"Invalid --repo path: {ex.Message}");
    return 2;
}

var dataPaths = new SwarmCodeDataPaths();
if (cli.McpHttpServer is { } httpServer)
    return await SwarmCode.Hosting.Mcp.SwarmCodeHttpMcpRelay.RunAsync(dataPaths, httpServer);
if (cli.BenchOpen is { } benchConversation)
{
    // Diagnostics: open one session from its files the way a fresh app start does and print the phase timings.
    var profileId = Guid.TryParse(cli.Profile, out var parsed) ? parsed : Guid.Empty;
    var report = await SessionOpenBenchmark.RunAsync(dataPaths, benchConversation, profileId);
    Console.WriteLine(report.ToJson());
    return 0;
}
if (cli.McpServer)
{
    // Vault bridge for a native coding agent: JSON-RPC over stdio, so nothing else may print to the console.
    return await SwarmCode.Hosting.Mcp.SwarmCodeMcpServer.RunAsync(dataPaths, repoRoot);
}

var nonInteractive = cli.PromptMode;
var autoApprove = cli.Yes || nonInteractive;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    DisableDefaults = true,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Configuration
    .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true, reloadOnChange: false)
    .AddJsonFile(dataPaths.UserConfigPath, optional: true, reloadOnChange: false)
    .AddEnvironmentVariables("SWARMCODE_")
    .AddInMemoryCollection(BuildOverrides(repoRoot, dataPaths));

// Framework and SwarmCore logs go to <data>/logs/swarmcode.log so they never interleave with the chat stream.
builder.Logging.ClearProviders();
builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
builder.Logging.AddProvider(new FileLoggerProvider(dataPaths.LogPath));

var services = builder.Services;
services.AddSingleton<IHostLifetime, NoopHostLifetime>(); // Ctrl+C belongs to the REPL, not the host.
if (!string.IsNullOrWhiteSpace(cli.AppendSystemPrompt))
{
    // --append-system-prompt: this run's extra instructions follow the configured ones (and the harness guidance appended later).
    var extra = cli.AppendSystemPrompt.Trim();
    services.PostConfigure<CodingOptions>(coding => coding.AdditionalInstructions = string.IsNullOrWhiteSpace(coding.AdditionalInstructions)
        ? extra : coding.AdditionalInstructions.Trim() + Environment.NewLine + extra);
}
services.AddSwarmCodeHost(new SwarmCodeHostOptions
{
    RepositoryRoot = repoRoot,
    DataPaths = dataPaths,
    Configuration = builder.Configuration,
    PermissionBroker = _ => new ConsolePermissionBroker(autoApprove),
    // user.ask reads one line from the terminal; -p mode has no one to answer, and --json keeps stdout for the stream.
    UserQuestionBroker = _ => new ConsoleUserQuestionBroker(nonInteractive, promptToStderr: cli.Json),
    // Self-improvement reporting is a developer feature: Debug builds only, and inert without a source checkout.
#if DEBUG
    SelfImprovement = true,
#endif
});

using var host = builder.Build();

// --list-lanes needs only the lane options (and the key provider for seeding), never an LLM call.
if (cli.ListLanes)
{
    var laneOptions = host.Services.GetRequiredService<IOptions<CodingLaneOptions>>().Value;
    if (laneOptions.Lanes.Count == 0)
    {
        Console.WriteLine("No lanes configured.");
        return 0;
    }
    var defaultLane = laneOptions.DefaultLane ?? laneOptions.Lanes.OrderBy(l => l.Priority).First().Name;
    foreach (var lane in laneOptions.Lanes.OrderBy(l => l.Priority))
    {
        var marker = string.Equals(lane.Name, defaultLane, StringComparison.OrdinalIgnoreCase) ? "*" : " ";
        var description = string.IsNullOrWhiteSpace(lane.Description) ? "" : $"  {lane.Description}";
        Console.WriteLine($"{marker} {lane.Name,-12} {lane.Provider}/{lane.Model}  (priority {lane.Priority}){description}");
    }
    Console.WriteLine("(* = default lane)");
    return 0;
}

using var runCts = new CancellationTokenSource();
if (cli.ListModels)
{
    ConsoleCancelEventHandler cancelProbe = (_, e) => { e.Cancel = true; runCts.Cancel(); };
    Console.CancelKeyPress += cancelProbe;
    await ModelLister.PrintAsync(host.Services.GetRequiredService<ILlmProviderRegistry>(), runCts.Token);
    Console.CancelKeyPress -= cancelProbe;
    return 0;
}

await host.StartAsync();
int exitCode;
try
{
    var orchestrator = host.Services.GetRequiredService<ICodingTurnOrchestrator>();
    var laneOpts = host.Services.GetRequiredService<IOptions<CodingLaneOptions>>().Value;

    if (cli.Lane is not null && orchestrator.ResolveLane(cli.Lane) is null)
    {
        Console.Error.WriteLine($"Unknown lane '{cli.Lane}'. Available: {string.Join(", ", orchestrator.Lanes.Select(l => l.Name))}");
        return 2;
    }

    var renderer = new ConsoleRenderer(new RendererOptions
    {
        Json = cli.Json,
        ShowThinking = cli.ShowThinking,
        Quiet = nonInteractive && !cli.Json,
    });
    var secretIntake = host.Services.GetService<SecretIntake>();
    // Personal memory: the turn context makes recall and the memory tools work; interactive turns also learn afterwards.
    var runner = new TurnRunner(orchestrator, renderer, cli.NoHandOff ? false : null, secretIntake)
    {
        MemoryTurns = host.Services.GetService<SwarmCode.Hosting.Memory.SwarmCodeMemoryTurns>(),
        MemoryCapture = nonInteractive ? null : host.Services.GetService<SwarmCode.Hosting.Memory.SwarmCodeMemoryCapture>(),
        Temporary = cli.Temporary,
        Decisions = host.Services.GetService<SwarmCode.Hosting.DecisionSessionCoordinator>(),
        Sessions = host.Services.GetService<SwarmCode.Hosting.WorkspaceSessionCatalog>(),
        RepositoryRoot = repoRoot,
        Agents = host.Services.GetService<SwarmCode.Hosting.Agents.SwarmCodeAgentHost>(),
        PermissionRules = host.Services.GetService<Microsoft.Extensions.Options.IOptions<SwarmCore.Coding.CodingOptions>>()?.Value.Permissions,
        Services = host.Services,
        // Interactive sessions can /undo a turn; a one-shot -p run keeps no checkpoints.
        Checkpoints = nonInteractive ? null : host.Services.GetService<SwarmCore.Coding.Checkpoints.TurnCheckpointService>(),
        PlanMode = cli.Plan,
        ModelOverride = string.IsNullOrWhiteSpace(cli.Model) ? null : cli.Model.Trim(),
        MaxIterations = cli.MaxTurns,
    };
    if (cli.PermissionMode is not null && SwarmCore.Coding.Permissions.CodingPermissionPolicy.TryParse(cli.PermissionMode, out var permissionMode))
    {
        if (permissionMode != SwarmCore.Coding.Permissions.CodingPermissionMode.Auto && autoApprove)
            Console.Error.WriteLine("[permissions] --permission-mode asks for approval, but this run auto-approves (-p or --yes): prompts are answered yes.");
        runner.PermissionMode = permissionMode;
    }

    // --continue / --resume: reuse a saved session of this repository so the earlier transcript is restored.
    string? resumedConversationId = null;
    if (cli.Continue || cli.Resume is not null)
    {
        var catalog = host.Services.GetService<SwarmCode.Hosting.WorkspaceSessionCatalog>();
        var saved = catalog is null ? [] : (await catalog.ListAsync(repoRoot)).Where(s => s.ParentSessionId is null).OrderByDescending(s => s.LastActivityUtc).ToList();
        if (cli.Continue)
        {
            resumedConversationId = saved.FirstOrDefault()?.Id;
            if (resumedConversationId is null) Console.Error.WriteLine("No saved session for this repository; starting a new one.");
        }
        else
        {
            var matches = saved.Where(s => s.Id.StartsWith(cli.Resume!, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 1) resumedConversationId = matches[0].Id;
            else
            {
                Console.Error.WriteLine(matches.Count == 0
                    ? $"No saved session of this repository matches '{cli.Resume}'. Run swarmcode and type /sessions to list them."
                    : $"'{cli.Resume}' matches {matches.Count} sessions; give more of the id.");
                return 2;
            }
        }
    }

    if (nonInteractive)
    {
        var prompt = cli.Prompt;
        if (string.IsNullOrWhiteSpace(prompt) && Console.IsInputRedirected)
            prompt = await Console.In.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            Console.Error.WriteLine("-p requires prompt text (argument or stdin).");
            return 2;
        }

        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; runCts.Cancel(); };
        Console.CancelKeyPress += cancel;
        // A one-shot run is a session of one turn: SessionStart ("startup", or "resume" for a continued session) before it, SessionEnd ("exit") after it.
        var oneShotConversation = resumedConversationId ?? Guid.NewGuid().ToString("N");
        var sessionStart = await SwarmCode.Hosting.Extensions.SwarmCodeTurnHooks.SessionStartAsync(host.Services, oneShotConversation, resumedConversationId is null ? "startup" : "resume", runCts.Token);
        foreach (var warning in sessionStart.Warnings) Console.Error.WriteLine($"[hooks] {warning}");
        runner.PendingContext = sessionStart.Context;
        // --lane chooses where to start; --no-handoff separately disables retries.
        var outcome = await runner.RunAsync(oneShotConversation, prompt.Trim(), cli.Lane, runCts.Token, explicitLane: cli.Lane is not null);
        foreach (var warning in await SwarmCode.Hosting.Extensions.SwarmCodeTurnHooks.SessionEndAsync(host.Services, oneShotConversation, "exit"))
            Console.Error.WriteLine($"[hooks] {warning}");
        Console.CancelKeyPress -= cancel;
        exitCode = outcome == TurnOutcome.Completed ? 0 : 1;
    }
    else
    {
        var repl = new Repl(
            orchestrator,
            runner,
            host.Services.GetRequiredService<ILlmApiKeyProvider>(),
            host.Services.GetRequiredService<ILlmProviderRegistry>(),
            repoRoot,
            cli.Lane,
            laneOpts.DefaultLane,
            secretIntake,
            host.Services.GetService<PasswordCsvImporter>())
        {
            Services = host.Services,
            DataPaths = dataPaths,
            InitialConversationId = resumedConversationId,
        };
        await repl.RunAsync();
        exitCode = 0;
    }
}
finally
{
    try { await host.StopAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
}

return exitCode;

/// <summary>Repository root plus the MCP bridge launch: this executable in --mcp-server mode, so native lanes get the vault.</summary>
static Dictionary<string, string?> BuildOverrides(string repoRoot, SwarmCodeDataPaths dataPaths)
{
    var overrides = new Dictionary<string, string?> { ["Coding:RepositoryRoot"] = repoRoot };
    SwarmCode.Hosting.Mcp.McpBridgeLocator.AddOverrides(overrides, dataPaths);
    return overrides;
}

/// <summary>Host lifetime that does not hook Ctrl+C, so the REPL can cancel a turn without stopping the host.</summary>
internal sealed class NoopHostLifetime : IHostLifetime
{
    public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
