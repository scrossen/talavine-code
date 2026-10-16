using System.Text.Json;
using System.Text.Json.Nodes;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;

namespace SwarmCore.Agent.AppControl;

/// <summary>What a host supplies to expose its catalog to an agent the person connected on this computer.</summary>
public sealed class AppControlOptions
{
    /// <summary>Short app id, also the MCP server name a client mounts it under (for example "talavine").</summary>
    public required string App { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>Stable identity of the profile/workspace whose data the tools act on.</summary>
    public required Guid ProfileId { get; init; }

    /// <summary>Identity of this connection; receipts and the turn conversation key carry it.</summary>
    public required Guid GrantId { get; init; }

    /// <summary>Authority name recorded on every turn context and receipt.</summary>
    public string Authority { get; init; } = "local-app-control";

    /// <summary>Host exclusions beyond the shared rule (code/command execution is never offered).</summary>
    public Func<ToolDescriptor, bool> IsOffered { get; init; } = static _ => true;

    /// <summary>Re-checked before and during each call (connection still enabled, profile still open).</summary>
    public Func<CancellationToken, Task> ValidateAuthority { get; init; } = static _ => Task.CompletedTask;

    /// <summary>Appends the planned/applied/failed receipt to the host's action ledger.</summary>
    public Func<RemoteProfileOperationReceipt, CancellationToken, Task> RecordReceipt { get; init; } = static (_, _) => Task.CompletedTask;

    /// <summary>
    /// Optional: runs the host's own assistant on a request in plain words and returns its answer. This is how
    /// an outside agent asks "why did X happen" or hands over multi-step work the host's assistant knows how to do.
    /// </summary>
    public Func<string, string?, CancellationToken, Task<string>>? Ask { get; init; }

    /// <summary>Extra guidance appended to the server instructions.</summary>
    public string? Instructions { get; init; }
}

/// <summary>
/// The shared "drive this app by prompt" surface: find the app's tools, call one through the same guards,
/// durable scheduler and receipts as the app's own assistant (<see cref="RemoteProfileToolRunner"/>), and
/// optionally ask the app's assistant. SDK-neutral: <see cref="Tools"/> feeds <see cref="LoopbackMcpServer"/>,
/// and a host with its own MCP endpoint can call <see cref="FindAsync"/>/<see cref="CallAsync"/> directly.
/// </summary>
public sealed class AppControlToolSurface(Func<IReadOnlyList<ToolDescriptor>> tools, RemoteProfileToolRunner runner, AppControlOptions options,
    Func<string, IReadOnlyList<ToolDescriptor>>? search = null)
{
    /// <summary>Over a host's capability catalog (Talavine): the catalog's own search ranks results first.</summary>
    public AppControlToolSurface(ICapabilityCatalog catalog, RemoteProfileToolRunner runner, AppControlOptions options)
        : this(catalog.All, runner, options, query => catalog.Search(query, null, 60)) { }

    public const int PageSize = 25;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public AppControlOptions Options => options;

    public IReadOnlyList<ToolDescriptor> Available()
        => tools()
            .Where(t => RemoteProfileToolPolicy.IsAvailable(t, RemoteToolReach.LocalAppControl) && options.IsOffered(t))
            .OrderBy(t => t.ToolId, StringComparer.Ordinal)
            .ToList();

    /// <summary>Tools matching <paramref name="query"/> (all when blank), best first, a page at a time.</summary>
    public string Find(string? query, int skip = 0)
    {
        var available = Available();
        IReadOnlyList<ToolDescriptor> matches;
        if (string.IsNullOrWhiteSpace(query))
        {
            matches = available;
        }
        else
        {
            var offered = available.ToDictionary(t => t.ToolId, StringComparer.Ordinal);
            var ranked = (search?.Invoke(query) ?? []).Where(t => offered.ContainsKey(t.ToolId)).ToList();
            var terms = query.Split([' ', ',', '.', '/', '-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(t => t.Length > 2).ToArray();
            var lexical = available
                .Select(t => (Tool: t, Score: Score(t, terms)))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .Select(x => x.Tool);
            matches = (search is null ? lexical : ranked.Concat(lexical)).DistinctBy(t => t.ToolId).ToList();
        }
        skip = Math.Max(0, skip);
        var page = matches.Skip(skip).Take(PageSize).Select(Describe).ToList();
        return JsonSerializer.Serialize(new
        {
            app = options.DisplayName,
            tools = page,
            total = matches.Count,
            nextSkip = skip + PageSize < matches.Count ? (int?)(skip + PageSize) : null,
            how = "Call one with call_tool using its exact id and an input object that matches inputSchema. "
                + "Describe the person's actual request in 'request'. Reuse operation_id only to retry the same action.",
        }, Json);
    }

    public object Describe(ToolDescriptor tool) => new
    {
        id = tool.ToolId,
        description = tool.Description,
        sideEffect = tool.SideEffect.ToString(),
        readOnly = tool.SideEffect == ToolSideEffect.ReadOnly && tool.ResolveSideEffect is null,
        inputSchema = tool.InputSchema,
    };

    /// <summary>Runs one tool for the person through the host's own guards and records a receipt.</summary>
    public async Task<string> CallAsync(string toolId, JsonObject input, string request, Guid? operationId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(toolId)) throw new ArgumentException("tool is required.");
        if (string.IsNullOrWhiteSpace(request))
            throw new ArgumentException("request is required: say in plain words what the person asked for.");
        var descriptor = tools().FirstOrDefault(t => string.Equals(t.ToolId, toolId, StringComparison.Ordinal));
        if (descriptor is null || !RemoteProfileToolPolicy.IsAvailable(descriptor, RemoteToolReach.LocalAppControl) || !options.IsOffered(descriptor))
            throw new InvalidOperationException($"{options.DisplayName} has no available tool '{toolId}'. Use find_tools to list what exists.");
        // A connected agent acts as the person for reading and local changes. Sending to someone or deleting
        // keeps the app's own approval: the person confirms it in the app (or the agent prepares a draft).
        if (descriptor.SideEffect is ToolSideEffect.ExternalSend or ToolSideEffect.Destructive)
            throw new InvalidOperationException(
                $"{toolId} sends or deletes, so {options.DisplayName} asks the person to approve it there. Tell the person what is ready to "
                + $"approve, or prepare it without sending (a draft) if {options.DisplayName} offers that.");
        var readOnly = descriptor.SideEffect == ToolSideEffect.ReadOnly && descriptor.ResolveSideEffect is null;
        var invocation = new RemoteProfileInvocation(options.ProfileId, options.GrantId, operationId ?? Guid.NewGuid(), request.Trim(), readOnly)
        {
            Reach = RemoteToolReach.LocalAppControl,
            Authority = options.Authority,
        };
        object result;
        try
        {
            var request2 = request.Trim();
            var recorded = (JsonObject)input.DeepClone();
            result = await runner.RunAsync(descriptor, input, invocation, options.ValidateAuthority,
                (receipt, token) => options.RecordReceipt(receipt with { Request = request2, Input = recorded }, token), ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"The input does not match {toolId}'s inputSchema: {ex.Message}");
        }
        return JsonSerializer.Serialize(result, Json);
    }

    /// <summary>The MCP tools a loopback server offers for this app.</summary>
    public IReadOnlyList<LoopbackMcpTool> Tools()
    {
        var name = options.DisplayName;
        var tools = new List<LoopbackMcpTool>
        {
            new("find_tools",
                $"Find what you can do in {name}. Returns tool ids, what each does, whether it changes data, and the input schema to call it with. "
                + "Search with plain words (\"create contact\", \"dark mode\", \"inbox rule\", \"why was this email archived\"); leave query empty to list everything a page at a time.",
                Schema(("query", "string", "Words describing what you want to do or learn.", false), ("skip", "integer", "Paging offset from nextSkip.", false)),
                ReadOnly: true,
                async (args, ct) =>
                {
                    await options.ValidateAuthority(ct).ConfigureAwait(false);
                    return new(Find(args["query"]?.GetValue<string>(), args["skip"] is JsonValue s && s.TryGetValue<int>(out var n) ? n : 0));
                }),
            new("call_tool",
                $"Run one {name} tool on the person's behalf, through {name}'s own checks, and record it in {name}'s activity log. "
                + "Use an id from find_tools and an input that matches its inputSchema.",
                Schema(("tool", "string", "Exact tool id from find_tools.", true),
                       ("input", "object", "Tool input matching the tool's inputSchema.", true),
                       ("request", "string", "The person's request in their words; it is recorded with the action.", true),
                       ("operation_id", "string", "Optional UUID. Reuse it only to retry this same action after an uncertain result.", false)),
                ReadOnly: false,
                async (args, ct) =>
                {
                    var input = AppControlInput.ParseObject(args["input"]);
                    Guid? operation = args["operation_id"]?.GetValue<string>() is { } op && Guid.TryParse(op, out var g) ? g : null;
                    var text2 = await CallAsync(args["tool"]?.GetValue<string>() ?? "", input, args["request"]?.GetValue<string>() ?? "", operation, ct).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(text2);
                    var isError = doc.RootElement.TryGetProperty("isError", out var e) && e.ValueKind == JsonValueKind.True;
                    return new(text2, isError);
                }),
        };
        if (options.Ask is { } ask)
            tools.Add(new("ask",
                $"Ask {name}'s own assistant in plain words. Use it for questions about why {name} did something, what its records show, "
                + $"or multi-step work its assistant knows how to do. The conversation appears in {name}'s history.",
                Schema(("request", "string", "What the person wants, in their words.", true),
                       ("conversation_id", "string", "Optional: continue an earlier ask conversation returned by this tool.", false)),
                ReadOnly: false,
                async (args, ct) =>
                {
                    await options.ValidateAuthority(ct).ConfigureAwait(false);
                    var request = args["request"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(request)) throw new ArgumentException("request is required.");
                    return new(await ask(request, args["conversation_id"]?.GetValue<string>(), ct).ConfigureAwait(false));
                }));
        return tools;
    }

    public string ServerInstructions()
        => $"This server controls {options.DisplayName} on this computer for the person who connected it. "
           + "Call find_tools first, then call_tool with an exact id and schema-shaped input. "
           + (options.Ask is null ? "" : "Use ask for why-questions and multi-step requests. ")
           + "Every action runs through the app's own checks and appears in its activity log. "
           + (options.Instructions ?? "");

    // Id words count most, then the tool's declared keywords (what the host says it is for), then its description.
    private static int Score(ToolDescriptor tool, string[] terms)
    {
        var id = tool.ToolId.ToLowerInvariant();
        var keywords = tool.Keywords.SelectMany(k => k.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToHashSet();
        var description = tool.Description.ToLowerInvariant();
        return terms.Select(t => t.ToLowerInvariant())
            .Sum(t => (id.Contains(t) ? 5 : 0) + (keywords.Contains(t) ? 3 : 0) + (description.Contains(t) ? 1 : 0));
    }

    private static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] properties)
    {
        var props = new JsonObject();
        var required = new JsonArray();
        foreach (var (n, t, d, r) in properties)
        {
            props[n] = new JsonObject { ["type"] = t, ["description"] = d };
            if (r) required.Add(n);
        }
        return new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = required };
    }
}
