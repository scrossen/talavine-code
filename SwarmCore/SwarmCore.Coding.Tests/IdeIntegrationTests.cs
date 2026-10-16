using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SwarmCore.Agent.AppControl;
using SwarmCore.Coding.Ide;

namespace SwarmCore.Coding.Tests;

/// <summary>Open-in-editor resolution, path fencing, reference extraction, and the editor-to-app selection bridge.</summary>
public sealed class IdeIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ide-tests-" + Guid.NewGuid().ToString("N"));

    public IdeIntegrationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static EditorEnvironment FakeEnv(EditorPlatform platform, string? path, params string[] files)
    {
        var set = new HashSet<string>(files, platform == EditorPlatform.Linux ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        return new EditorEnvironment(platform, path, ".COM;.EXE;.BAT;.CMD",
            platform == EditorPlatform.Windows ? @"C:\Users\p\AppData\Local" : null,
            platform == EditorPlatform.Windows ? @"C:\Program Files" : null,
            platform == EditorPlatform.Windows ? @"C:\Users\p" : "/Users/p",
            set.Contains);
    }

    // ---- command resolution ---------------------------------------------------------------------------------

    [Fact]
    public void Windows_prefers_the_code_executable_beside_code_cmd_and_passes_an_argument_list()
    {
        var env = FakeEnv(EditorPlatform.Windows, null,
            @"C:\Users\p\AppData\Local\Programs\Microsoft VS Code\bin\code.cmd",
            @"C:\Users\p\AppData\Local\Programs\Microsoft VS Code\Code.exe");
        var file = @"C:\My Repos\a&b\src\Foo Bar.cs";

        var plan = EditorCommandResolver.Plan(file, 12, 3, null, env);

        var process = Assert.IsType<EditorProcessStep>(plan[0]);
        Assert.Equal(@"C:\Users\p\AppData\Local\Programs\Microsoft VS Code\Code.exe", process.FileName);
        Assert.Equal(["--goto", file + ":12:3"], process.Arguments);
        var info = EditorLauncher.ToStartInfo(process);
        Assert.False(info.UseShellExecute);
        Assert.Equal(2, info.ArgumentList.Count);
        Assert.Equal(file + ":12:3", info.ArgumentList[1]);
        Assert.IsType<EditorUriStep>(plan[1]);
        Assert.IsType<EditorDefaultAppStep>(plan[2]);
    }

    [Fact]
    public void Windows_batch_cli_is_skipped_when_a_path_contains_cmd_metacharacters()
    {
        var env = FakeEnv(EditorPlatform.Windows, @"C:\tools\vscode\bin", @"C:\tools\vscode\bin\code.cmd");

        var unsafePlan = EditorCommandResolver.Plan(@"C:\repo\a&calc.cs", 1, null, null, env);
        Assert.DoesNotContain(unsafePlan, s => s is EditorProcessStep);
        Assert.IsType<EditorUriStep>(unsafePlan[0]);

        var safePlan = EditorCommandResolver.Plan(@"C:\repo with space\a.cs", 4, null, null, env);
        var process = Assert.IsType<EditorProcessStep>(safePlan[0]);
        Assert.Equal(@"C:\tools\vscode\bin\code.cmd", process.FileName);
        Assert.Equal(["--goto", @"C:\repo with space\a.cs:4"], process.Arguments);
    }

    [Fact]
    public void MacOS_finds_the_cli_inside_the_application_bundle()
    {
        var cli = "/Applications/Visual Studio Code.app/Contents/Resources/app/bin/code";
        var plan = EditorCommandResolver.Plan("/Users/p/repo/a b.ts", 7, null, null, FakeEnv(EditorPlatform.MacOS, "/usr/bin:/bin", cli));

        var process = Assert.IsType<EditorProcessStep>(plan[0]);
        Assert.Equal(cli, process.FileName);
        Assert.Equal(["--goto", "/Users/p/repo/a b.ts:7"], process.Arguments);
    }

    [Fact]
    public void Without_vscode_the_plan_falls_back_to_the_uri_then_the_default_app()
    {
        var plan = EditorCommandResolver.Plan("/home/p/repo/x.py", 3, 2, null, FakeEnv(EditorPlatform.Linux, "/usr/bin"));

        var uri = Assert.IsType<EditorUriStep>(plan[0]);
        Assert.Equal("vscode://file/home/p/repo/x.py:3:2", uri.Uri.AbsoluteUri);
        Assert.IsType<EditorDefaultAppStep>(plan[1]);
    }

    [Fact]
    public void Vscode_uri_encodes_spaces_and_keeps_the_drive_letter()
    {
        var uri = EditorCommandResolver.VsCodeUri(@"C:\My Repo\src\a#b.cs", 10, null);
        Assert.Equal("vscode://file/C:/My%20Repo/src/a%23b.cs:10", uri.AbsoluteUri);
    }

    [Fact]
    public void Configured_command_is_tokenized_without_a_shell_and_supports_placeholders()
    {
        var env = FakeEnv(EditorPlatform.Linux, "/usr/bin", "/usr/bin/subl");
        var plan = EditorCommandResolver.Plan("/repo/a b.rs", 5, 2, "subl \"{file}:{line}:{column}\" --wait", env);
        var process = Assert.IsType<EditorProcessStep>(plan[0]);
        Assert.Equal("/usr/bin/subl", process.FileName);
        Assert.Equal(["/repo/a b.rs:5:2", "--wait"], process.Arguments);
        Assert.IsType<EditorDefaultAppStep>(plan[1]);
        Assert.DoesNotContain(plan, s => s is EditorUriStep);

        var cursor = EditorCommandResolver.Plan("/repo/x.cs", 9, null, "\"/opt/My Editors/cursor\"", FakeEnv(EditorPlatform.Linux, null, "/opt/My Editors/cursor"));
        var step = Assert.IsType<EditorProcessStep>(cursor[0]);
        Assert.Equal("/opt/My Editors/cursor", step.FileName);
        Assert.Equal(["--goto", "/repo/x.cs:9"], step.Arguments);
    }

    [Fact]
    public async Task Launcher_falls_through_failed_steps_and_never_opens_files_outside_the_repository()
    {
        var file = Path.Combine(_root, "src", "Thing.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "namespace X;\n\npublic class Thing\n{\n    int Value => 42;\n}\n");
        var started = new List<ProcessStartInfo>();
        var uris = new List<Uri>();
        var launcher = new EditorLauncher(
            openUri: uri => { uris.Add(uri); return Task.FromResult(true); },
            openDefault: _ => Task.FromResult(false),
            startProcess: info => { started.Add(info); throw new System.ComponentModel.Win32Exception("not found"); },
            environment: () => FakeEnv(OperatingSystem.IsWindows() ? EditorPlatform.Windows : EditorPlatform.Linux, null,
                OperatingSystem.IsWindows() ? @"C:\Users\p\AppData\Local\Programs\Microsoft VS Code\bin\code.cmd" : "/usr/bin/code"));

        var result = await launcher.OpenAsync(new EditorTarget("src/Thing.cs", SearchText: "    int Value => 42;"), _root, null);

        Assert.True(result.Opened);
        Assert.Single(started);
        Assert.Contains(file + ":5:5", started[0].ArgumentList);
        Assert.EndsWith("Thing.cs:5:5", Assert.Single(uris).AbsoluteUri);

        var escape = await launcher.OpenAsync(new EditorTarget("../" + Path.GetFileName(_root) + "-other/x.cs"), _root, null);
        Assert.False(escape.Opened);
        var missing = await launcher.OpenAsync(new EditorTarget("src/Missing.cs"), _root, null);
        Assert.False(missing.Opened);
        Assert.Single(started);
    }

    // ---- path policy and references -------------------------------------------------------------------------

    [Fact]
    public void Path_policy_accepts_repository_files_and_existing_absolute_files_only()
    {
        var inside = Path.Combine(_root, "a.txt");
        File.WriteAllText(inside, "x");
        var outsideDir = _root + "-sibling";
        Directory.CreateDirectory(outsideDir);
        var outside = Path.Combine(outsideDir, "b.txt");
        File.WriteAllText(outside, "y");
        try
        {
            Assert.Equal(inside, EditorPathPolicy.Resolve("a.txt", _root));
            Assert.Equal(inside, EditorPathPolicy.Resolve(inside, _root));
            Assert.Null(EditorPathPolicy.Resolve("../" + Path.GetFileName(outsideDir) + "/b.txt", _root)); // relative escape
            Assert.Equal(outside, EditorPathPolicy.Resolve(outside, _root)); // an existing absolute file is allowed
            Assert.Null(EditorPathPolicy.Resolve(Path.Combine(_root, "nope.txt"), _root));
            Assert.Null(EditorPathPolicy.Resolve("a.txt", null));
            Assert.False(EditorPathPolicy.IsUnder(outside, _root)); // "<root>-sibling" is not under "<root>"
        }
        finally { Directory.Delete(outsideDir, true); }
    }

    [Theory]
    [InlineData("src/Foo.cs:12", "src/Foo.cs", 12, null)]
    [InlineData("src/Foo.cs:12:4", "src/Foo.cs", 12, 4)]
    [InlineData("src/Foo.cs#L30", "src/Foo.cs", 30, null)]
    [InlineData("src/Foo.cs:L3-L9", "src/Foo.cs", 3, null)]
    [InlineData(@"C:\repo\Foo.cs:7", @"C:\repo\Foo.cs", 7, null)]
    [InlineData("README.md", "README.md", null, null)]
    public void Parses_file_references(string text, string path, int? line, int? column)
    {
        Assert.True(FileReferenceParser.TryParse(text, out var target));
        Assert.Equal(path, target.Path);
        Assert.Equal(line, target.Line);
        Assert.Equal(column, target.Column);
    }

    [Theory]
    [InlineData("https://example.com/a.js")]
    [InlineData("dotnet test")]
    [InlineData("Foo")]
    [InlineData("v1.2")]
    [InlineData("...")]
    public void Rejects_non_references(string text) => Assert.False(FileReferenceParser.TryParse(text, out _));

    [Fact]
    public void Extracts_references_from_tool_inputs_and_results_structurally()
    {
        var grep = ToolFileReferences.Extract("""{"pattern":"Foo"}""",
            """{"matches":[{"path":"src/A.cs","line":4,"text":"Foo"},{"path":"src/B.cs","line":9,"text":"Foo"}],"totalMatches":2}""");
        Assert.Equal(["src/A.cs:4", "src/B.cs:9"], grep.Select(t => t.Label));

        var glob = ToolFileReferences.Extract("""{"pattern":"**/*.csproj"}""", """{"files":["a/A.csproj","b/B.csproj"],"total":2}""");
        Assert.Equal(["a/A.csproj", "b/B.csproj"], glob.Select(t => t.Label));

        var status = ToolFileReferences.Extract("{}", """{"branch":"main","entries":[{"status":" M","path":"x.cs"}]}""");
        Assert.Equal("x.cs", Assert.Single(status).Path);

        var edit = ToolFileReferences.Extract("""{"path":"src/A.cs","oldText":"a","newText":"b + c"}""",
            """{"path":"C:/repo/src/A.cs","replacements":1,"succeeded":true}""");
        var target = Assert.Single(edit);
        Assert.Equal("b + c", target.SearchText);

        var notebook = ToolFileReferences.Extract("""{"notebook_path":"nb/analysis.ipynb","cell_id":"3"}""", "not json");
        Assert.Equal("nb/analysis.ipynb", Assert.Single(notebook).Path);
        Assert.Empty(ToolFileReferences.Extract(null, "plain text result"));
    }

    // ---- selection formatting and routing -------------------------------------------------------------------

    [Fact]
    public void Formats_a_selection_as_a_fenced_reference_block_that_quoted_backticks_cannot_close()
    {
        var file = Path.Combine(_root, "src", "a.md");
        var block = IdeReferenceFormatter.Format(new IdeSelection(_root, file, 3, 5, "x\n```\ny\r\n", "markdown"), _root);
        Assert.Equal("````markdown src/a.md:L3-L5\nx\n```\ny\n````", block);
        Assert.Equal("`src/a.md:L3`", IdeReferenceFormatter.Format(new IdeSelection(_root, file, 3, 3, "", null), _root));
        Assert.Equal("draft\n\nblock\n", IdeReferenceFormatter.AppendToDraft("draft  \n", "block"));
    }

    [Fact]
    public void Routes_to_the_visible_or_most_recent_session_for_the_repository()
    {
        var repoA = Path.Combine(_root, "a");
        var repoB = Path.Combine(_root, "b");
        var nested = Path.Combine(repoA, "vendor", "lib");
        var file = Path.Combine(repoA, "src", "x.cs");
        var now = DateTime.UtcNow;
        var sessions = new List<IdeSessionCandidate>
        {
            new("old-a", repoA, now.AddHours(-2)),
            new("new-a", repoA, now.AddMinutes(-1)),
            new("child-a", repoA, now, IsTopLevel: false),
            new("b", repoB, now),
            new("nested", nested, now),
        };

        Assert.Equal("new-a", IdeSessionRouter.Choose(sessions, file, activeSessionId: "b")?.Id);
        Assert.Equal("old-a", IdeSessionRouter.Choose(sessions, file, activeSessionId: "old-a")?.Id);
        Assert.Equal("nested", IdeSessionRouter.Choose(sessions, Path.Combine(nested, "y.cs"), null)?.Id);
        Assert.Equal("b", IdeSessionRouter.Choose(sessions, Path.Combine(_root, "elsewhere", "z.cs"), "b")?.Id);
        Assert.Null(IdeSessionRouter.Choose(sessions, Path.Combine(_root, "elsewhere", "z.cs"), null));
    }

    [Fact]
    public void Selection_validation_rejects_relative_paths_bad_ranges_and_oversize_text()
    {
        var file = Path.Combine(_root, "a.cs");
        Assert.True(IdeSelection.TryParse(new JsonObject { ["file"] = file, ["startLine"] = 2, ["endLine"] = 4, ["selectionText"] = "x", ["languageId"] = "csharp" }, out var ok, out _));
        Assert.Equal((2, 4, "csharp"), (ok.StartLine, ok.EndLine, ok.LanguageId));
        Assert.False(IdeSelection.TryParse(new JsonObject { ["file"] = "a.cs" }, out _, out _));
        Assert.False(IdeSelection.TryParse(new JsonObject { ["file"] = file, ["startLine"] = 5, ["endLine"] = 4 }, out _, out _));
        Assert.False(IdeSelection.TryParse(new JsonObject { ["file"] = file, ["selectionText"] = new string('x', IdeSelection.MaxSelectionChars + 1) }, out _, out _));
        Assert.True(IdeSelection.TryParse(new JsonObject { ["file"] = file, ["languageId"] = "x\"; rm -rf" }, out var cleaned, out _));
        Assert.Null(cleaned.LanguageId);
    }

    // ---- the loopback bridge --------------------------------------------------------------------------------

    [Fact]
    public async Task Bridge_publishes_a_live_discovery_record_and_withdraws_it()
    {
        var dir = Path.Combine(_root, "discovery");
        var server = new IdeBridgeServer("swarmcode", "SwarmCode", "1.0", (_, _) => Task.FromResult(new IdeDeliveryResult(true, "ok")), NullLogger.Instance, dir);
        Assert.True(server.Start());

        var record = new LoopbackDiscoveryDirectory(dir).Find("swarmcode");
        Assert.NotNull(record);
        Assert.Equal(Environment.ProcessId, record!.ProcessId);
        Assert.Equal(server.Url, record.Url);
        Assert.Equal(server.Token, record.Token);
        Assert.NotNull(record.ProcessStartUtc);
        Assert.True(LoopbackDiscoveryDirectory.IsLive(record));
        Assert.StartsWith("http://127.0.0.1:", record.Url);
        Assert.EndsWith(IdeBridgeServer.Route, record.Url);

        // A record whose recorded start time does not match the running process is stale (reused process id).
        Assert.False(LoopbackDiscoveryDirectory.IsLive(record with { ProcessStartUtc = record.ProcessStartUtc!.Value.AddMinutes(-10) }));

        await server.DisposeAsync();
        Assert.False(File.Exists(Path.Combine(dir, "swarmcode.json")));
    }

    [Fact]
    public async Task Bridge_rejects_unauthenticated_cross_origin_misdirected_and_oversize_requests_and_delivers_valid_ones()
    {
        var delivered = new List<IdeSelection>();
        await using var server = new IdeBridgeServer("swarmcode", "SwarmCode", null,
            (selection, _) => { lock (delivered) delivered.Add(selection); return Task.FromResult(new IdeDeliveryResult(true, "Inserted.", "s1")); },
            NullLogger.Instance, Path.Combine(_root, "discovery2"));
        server.Start();
        var port = new Uri(server.Url!).Port;
        var body = JsonSerializer.Serialize(new { workspaceFolder = _root, file = Path.Combine(_root, "a.cs"), startLine = 1, endLine = 2, selectionText = "x", languageId = "csharp" });

        Assert.Equal(401, (await SendAsync(port, body, token: null)).Status);
        Assert.Equal(401, (await SendAsync(port, body, token: new string('0', 64))).Status);
        Assert.Equal(421, (await SendAsync(port, body, server.Token, host: "evil.example:" + port)).Status);
        Assert.Equal(403, (await SendAsync(port, body, server.Token, origin: "https://evil.example")).Status);
        Assert.Equal(413, (await SendAsync(port, body, server.Token, declaredLength: IdeBridgeServer.MaxBodyBytes + 1, sendBody: false)).Status);
        Assert.Equal(422, (await SendAsync(port, """{"file":"relative.cs"}""", server.Token)).Status);
        Assert.Empty(delivered);

        var ok = await SendAsync(port, body, server.Token, origin: $"http://127.0.0.1:{port}");
        Assert.Equal(200, ok.Status);
        Assert.Contains("\"sessionId\":\"s1\"", ok.Body);
        var selection = Assert.Single(delivered);
        Assert.Equal(Path.Combine(_root, "a.cs"), selection.File);
        Assert.Equal((1, 2), (selection.StartLine, selection.EndLine));
    }

    private static async Task<(int Status, string Body)> SendAsync(int port, string body, string? token, string? host = null, string? origin = null,
        int? declaredLength = null, bool sendBody = true)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port);
        var stream = client.GetStream();
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = new StringBuilder()
            .Append("POST ").Append(IdeBridgeServer.Route).Append(" HTTP/1.1\r\n")
            .Append("Host: ").Append(host ?? $"127.0.0.1:{port}").Append("\r\n")
            .Append("Content-Type: application/json\r\n")
            .Append("Content-Length: ").Append(declaredLength ?? bytes.Length).Append("\r\n")
            .Append("Connection: close\r\n");
        if (token is not null) head.Append("Authorization: Bearer ").Append(token).Append("\r\n");
        if (origin is not null) head.Append("Origin: ").Append(origin).Append("\r\n");
        head.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()));
        if (sendBody) await stream.WriteAsync(bytes);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var response = await reader.ReadToEndAsync(timeout.Token);
        var status = int.Parse(response.Split(' ')[1]);
        var split = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        return (status, split >= 0 ? response[(split + 4)..] : "");
    }
}
