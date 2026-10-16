namespace SwarmCode;

/// <summary>Hand-rolled command-line parsing (no System.CommandLine).</summary>
public sealed class CliOptions
{
    public string? Repo { get; private set; }
    public string? Lane { get; private set; }
    public string? Prompt { get; private set; }
    public bool PromptMode { get; private set; }
    public bool Json { get; private set; }
    public bool Yes { get; private set; }
    public bool NoHandOff { get; private set; }
    /// <summary>Temporary session: personal memory is neither read nor written.</summary>
    public bool Temporary { get; private set; }
    public bool ListLanes { get; private set; }
    public bool ListModels { get; private set; }
    public bool ShowThinking { get; private set; }
    public bool McpServer { get; private set; }
    public string? McpHttpServer { get; private set; }
    /// <summary>Conversation id to open under measurement (diagnostics; prints a JSON report).</summary>
    public string? BenchOpen { get; private set; }
    /// <summary>Profile id whose working-context checkpoint the benchmark restores.</summary>
    public string? Profile { get; private set; }
    /// <summary>Continue the most recent session of this repository.</summary>
    public bool Continue { get; private set; }
    /// <summary>Resume one session by id (or a unique id prefix).</summary>
    public string? Resume { get; private set; }
    /// <summary>Permission mode for the turns: auto (default), ask-risky, ask.</summary>
    public string? PermissionMode { get; private set; }
    /// <summary>Exact model id to run the lane with (the lane's provider stays).</summary>
    public string? Model { get; private set; }
    /// <summary>Start in plan mode: investigate read-only and propose a plan first.</summary>
    public bool Plan { get; private set; }
    /// <summary>Extra instructions appended to the coding system prompt for this run.</summary>
    public string? AppendSystemPrompt { get; private set; }
    /// <summary>Model iterations per lane attempt (0 = configured default).</summary>
    public int MaxTurns { get; private set; }
    public bool Version { get; private set; }
    public bool Help { get; private set; }
    public string? Error { get; private set; }

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h" or "--help" or "-?":
                    o.Help = true;
                    break;
                case "--version":
                    o.Version = true;
                    break;
                case "--repo":
                    if (!TakeValue(args, ref i, arg, o, out var repo)) return o;
                    o.Repo = repo;
                    break;
                case "--lane":
                    if (!TakeValue(args, ref i, arg, o, out var lane)) return o;
                    o.Lane = lane;
                    break;
                case "-p" or "--prompt":
                    o.PromptMode = true;
                    // The text is optional: with no value, -p reads the prompt from stdin.
                    if (i + 1 < args.Length && !(args[i + 1].StartsWith('-') && args[i + 1].Length > 1 && IsKnownFlag(args[i + 1])))
                        o.Prompt = args[++i];
                    break;
                case "--json":
                    o.Json = true;
                    break;
                case "--yes" or "-y":
                    o.Yes = true;
                    break;
                case "--no-handoff":
                    o.NoHandOff = true;
                    break;
                case "--temporary":
                    o.Temporary = true;
                    break;
                case "--list-lanes":
                    o.ListLanes = true;
                    break;
                case "--list-models":
                    o.ListModels = true;
                    break;
                case "--show-thinking":
                    o.ShowThinking = true;
                    break;
                case "--mcp-server":
                    o.McpServer = true;
                    break;
                case "--mcp-http-server":
                    if (!TakeValue(args, ref i, arg, o, out var httpServer)) return o;
                    o.McpHttpServer = httpServer;
                    break;
                case "--bench-open":
                    if (!TakeValue(args, ref i, arg, o, out var bench)) return o;
                    o.BenchOpen = bench;
                    break;
                case "--profile":
                    if (!TakeValue(args, ref i, arg, o, out var profile)) return o;
                    o.Profile = profile;
                    break;
                case "-c" or "--continue":
                    o.Continue = true;
                    break;
                case "-r" or "--resume":
                    if (!TakeValue(args, ref i, arg, o, out var resume)) return o;
                    o.Resume = resume;
                    break;
                case "--permission-mode":
                    if (!TakeValue(args, ref i, arg, o, out var mode)) return o;
                    if (!SwarmCore.Coding.Permissions.CodingPermissionPolicy.TryParse(mode, out _))
                    {
                        o.Error = $"Unknown permission mode '{mode}'. Use auto, ask-risky or ask.";
                        return o;
                    }
                    o.PermissionMode = mode;
                    break;
                case "--model":
                    if (!TakeValue(args, ref i, arg, o, out var model)) return o;
                    o.Model = model;
                    break;
                case "--plan":
                    o.Plan = true;
                    break;
                case "--append-system-prompt":
                    if (!TakeValue(args, ref i, arg, o, out var append)) return o;
                    o.AppendSystemPrompt = append;
                    break;
                case "--max-turns":
                    if (!TakeValue(args, ref i, arg, o, out var turns)) return o;
                    if (!int.TryParse(turns, out var max) || max <= 0)
                    {
                        o.Error = "--max-turns requires a positive number.";
                        return o;
                    }
                    o.MaxTurns = max;
                    break;
                default:
                    o.Error = $"Unknown argument '{arg}'. Use --help.";
                    return o;
            }
        }
        if (o.Continue && o.Resume is not null)
        {
            o.Error = "Use either --continue or --resume <id>, not both.";
            return o;
        }
        return o;
    }

    private static bool IsKnownFlag(string value) => value is
        "-h" or "--help" or "--version" or "--repo" or "--lane" or "-p" or "--prompt" or "--json"
        or "--yes" or "-y" or "--no-handoff" or "--temporary" or "--list-lanes" or "--list-models" or "--show-thinking" or "--mcp-server"
        or "--bench-open" or "--profile" or "--mcp-http-server" or "-c" or "--continue" or "-r" or "--resume" or "--permission-mode"
        or "--model" or "--plan" or "--append-system-prompt" or "--max-turns";

    private static bool TakeValue(string[] args, ref int i, string name, CliOptions o, out string value)
    {
        if (i + 1 >= args.Length)
        {
            o.Error = $"{name} requires a value.";
            value = "";
            return false;
        }
        value = args[++i];
        return true;
    }

    public const string HelpText = """
        Talavine Code - terminal coding agent on the SwarmCore runtime

        Usage: swarmcode [options]

          --repo <path>        repository to work in (default: current directory)
          --lane <name>        lane to start on (default: configured default lane)
          --model <id>         exact model id for the lane (the lane's provider stays)
          -p, --prompt <text>  non-interactive: run one turn, print the final text, exit
                               (0 = accepted or explicitly unverified answer, 1 = failed/stopped)
                               reads stdin if no text; configured host checks must pass
          -c, --continue       continue the most recent session of this repository
          -r, --resume <id>    resume a session by id (or a unique id prefix); /sessions lists them
          --json               print every turn event as one JSON line on stdout
          --yes, -y            auto-approve permission prompts
          --permission-mode <m> auto (default), ask-risky (ask before commands, pushes, browser,
                               desktop and app actions) or ask (ask before every side effect)
          --plan               start in plan mode: investigate read-only and propose a plan first
          --max-turns <n>      model iterations per lane attempt (default: configured limit)
          --append-system-prompt <text>  extra instructions for the agent in this run
          --no-handoff         do not continue on another lane when a lane refuses
          --temporary          temporary session: personal memory is neither read nor written
          --show-thinking      show model reasoning (dim)
          --list-lanes         print configured lanes and exit
          --list-models        probe providers, print availability and models, and exit
          --mcp-server         run the vault bridge as a stdio MCP server for a native coding agent
                               (started automatically for the claude and codex lanes)
          --mcp-http-server <name> run the host HTTP relay for an existing configured MCP server
          --bench-open <id>    diagnostics: measure opening session <id> from its files and print a
                               JSON report (--profile <guid> selects the checkpoint to restore)
          --version            print the version
          -h, --help           show this help

        Interactive commands: /help /lanes /lane <name> /model [id|auto] /mode [auto|ask-risky|ask] /models
          /sessions /resume [n|id] /new /clear /plan /approve /compact /cost /status /agents /mcp /hooks /permissions
          /undo /review <text> /export [path] /init /keys /key <provider|brave> [value]
          /secret <name> [value] /secrets /forget <name> /import-passwords <csv> /quit
        Prefix a message with @<lane> to run that one message on a specific lane; @path/to/file attaches a file.
        Environment: SWARMCODE_HOME (data dir), NO_COLOR, SWARMCODE_<Section>__<Key> overrides.
        """;
}
