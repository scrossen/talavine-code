using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SwarmCore.Coding.Routing;
using SwarmCore.LlmRouting.Configuration;
using SwarmCore.LlmRouting.LocalRuntime;

namespace SwarmCode.Hosting.Tests;

/// <summary>Induced refusal at the HTTP provider boundary; the host, engine, tools, persistence and Ollama inference are real.</summary>
public sealed class OllamaHandoffLiveTests : TempDirTest
{
    [OllamaSetupLiveFact]
    public Task SelectedProviderPartialRefusalContinuesOnRealOllamaWithoutRepeatingCompletedWork() => RunAsync(partial: true);

    [OllamaSetupLiveFact]
    public Task SelectedModelFullRefusalCompletesOnRealOllama() => RunAsync(partial: false);

    private async Task RunAsync(bool partial)
    {
        var repo = Path.Combine(Tmp, "repo"); Directory.CreateDirectory(repo);
        var evidence = Environment.GetEnvironmentVariable("SWARMCODE_HANDOFF_EVIDENCE_DIR");
        var model = Environment.GetEnvironmentVariable("SWARMCODE_OLLAMA_TEST_MODEL") ?? OpenModelDiscovery.Examples[0].Model;
        await using var fixture = new RefusingProvider(partial, Path.Combine(repo, "first.txt"));
        var source = new ConfiguredModelSource { Name = "refusal-fixture", Kind = "Compatible", Endpoint = fixture.Endpoint, Model = "controlled-refusal", RequiresApiKey = false };
        await new ModelSourcesStore(Paths).SaveAsync(new(0, [source]));
        var configuration = SwarmCodeHostComposer.BuildConfiguration(Paths, new Dictionary<string, string?>
        {
            ["Coding:Agents:Enabled"] = "false", ["Coding:FallbackDiscovery:Enabled"] = "false",
            ["Coding:HardTimeout"] = "00:05:00", ["Coding:MaxIterations"] = "12",
            ["PersonalMemory:Enabled"] = "false",
            ["Coding:Acceptance:Commands:0:Name"] = "Both appends ran exactly once",
            ["Coding:Acceptance:Commands:0:FileName"] = "/bin/sh",
            ["Coding:Acceptance:Commands:0:Arguments:0"] = "-c",
            ["Coding:Acceptance:Commands:0:Arguments:1"] = "printf 'FIRST\\n' | cmp - first.txt && printf 'SECOND\\n' | cmp - second.txt && printf 'HANDOFF_ACCEPTED\\n'",
            ["Coding:Acceptance:Commands:0:RequiredOutput"] = "HANDOFF_ACCEPTED",
        });
        await using var host = await WorkspaceHost.CreateAsync(new() { DataPaths = Paths, RepositoryRoot = repo, Configuration = configuration });
        var local = Assert.Single(host.Lanes, l => l.Provider == "Ollama" && l.Model == model);
        Assert.True(local.PreferOnDecline);
        var session = await host.Services.GetRequiredService<WorkspaceSessionCatalog>().CreateAsync(repo, "Controlled Ollama handoff", source.Name);
        // Use the same persisted choices as the desktop provider/model pickers, then load them through a new catalog.
        var choices = new WorkspaceSessionCatalog(Paths);
        if (partial) await choices.SetUserLaneAsync(session.Id, source.Name, source.Model!);
        else await choices.SetModelAsync(session.Id, source.Name, source.Model!);
        Assert.True((await new WorkspaceSessionCatalog(Paths).GetAsync(session.Id))!.Selection!.UserSelected);
        await using var runner = host.OpenSession(session.Id);
        var finished = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.TurnFinished += r => finished.TrySetResult(r);
        const string request = "Complete these two real file operations in this repository using shell.exec: " +
            "(1) append exactly one line FIRST to first.txt, using printf 'FIRST\\n' >> first.txt; " +
            "(2) append exactly one line SECOND to second.txt, using printf 'SECOND\\n' >> second.txt. " +
            "Each append must run exactly once. Inspect existing tool receipts and files before acting; never repeat a completed append. " +
            "Verify both files have exactly their one expected line, then report completion. Do the work now.";
        // No /auto or special per-request hand-off override; the saved choice wins over the supplied default.
        Assert.True(runner.TrySend(request, local.Name));
        TurnResult? result = null;
        try { result = await finished.Task.WaitAsync(TimeSpan.FromMinutes(6)); }
        finally
        {
            if (runner.IsRunning) runner.Cancel();
            if (!string.IsNullOrWhiteSpace(evidence))
            {
                var target = Path.Combine(evidence, partial ? "partial" : "full"); Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target, "entries.json"), JsonSerializer.Serialize(runner.Entries.Select(e => new { Type = e.GetType().Name, Value = JsonSerializer.SerializeToElement(e, e.GetType()) }), new JsonSerializerOptions { WriteIndented = true }));
                File.WriteAllText(Path.Combine(target, "primary-requests.json"), JsonSerializer.Serialize(fixture.Requests));
                File.WriteAllText(Path.Combine(target, "outcome.json"), JsonSerializer.Serialize(new { Result = result, Acceptance = runner.Acceptance, Model = model, InducedRefusal = true, UserSelectedStart = true,
                    FirstWriteBeforeHandoff = fixture.FirstWriteUtc, FirstWriteAfterHandoff = File.GetLastWriteTimeUtc(Path.Combine(repo, "first.txt")) }, new JsonSerializerOptions { WriteIndented = true }));
                foreach (var file in Directory.GetFiles(repo, "*.txt")) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
            }
        }
        Assert.NotNull(result);
        Assert.Null(result.Error); Assert.False(result.Cancelled); Assert.True(result.Completed);
        Assert.Equal(CodingAcceptanceStatus.Passed, runner.Acceptance?.Status);
        Assert.Equal(new[] { source.Name, local.Name }, result.LanesUsed);
        Assert.Equal("FIRST\n", File.ReadAllText(Path.Combine(repo, "first.txt")));
        Assert.Equal("SECOND\n", File.ReadAllText(Path.Combine(repo, "second.txt")));
        var entries = runner.Entries;
        Assert.Single(entries.OfType<NoticeEntry>(), e => e.Kind == NoticeKind.HandOff);
        var tools = entries.OfType<ToolCallEntry>().ToArray();
        Assert.DoesNotContain(tools, t => t.State is ToolCallState.Running or ToolCallState.Unknown);
        Assert.Single(tools, t => t.ToolName == "harness.decline");
        if (partial)
        {
            Assert.Single(tools, t => t.ToolCallId == "fixture-first" && t.ToolName == "shell.exec" && t.State == ToolCallState.Completed);
            // The first file must not even be rewritten with identical content during continuation.
            Assert.NotNull(fixture.FirstWriteUtc);
            Assert.Equal(fixture.FirstWriteUtc, File.GetLastWriteTimeUtc(Path.Combine(repo, "first.txt")));
        }
        // Opening the saved conversation is a read, not a rerun of the original request or its completed tools.
        await using var reopened = host.OpenSession(session.Id);
        await reopened.LoadHistoryAsync();
        Assert.Equal(tools.Length, reopened.Entries.OfType<ToolCallEntry>().Count());
        Assert.False(reopened.IsRunning);
        var saved = await new WorkspaceSessionCatalog(Paths).GetAsync(session.Id);
        Assert.True(saved!.Selection!.UserSelected);
        Assert.Equal(source.Name, saved.LaneName);
        Assert.Equal(source.Model, saved.Selection.Model);
        Assert.Equal("FIRST\n", File.ReadAllText(Path.Combine(repo, "first.txt")));
        Assert.Equal("SECOND\n", File.ReadAllText(Path.Combine(repo, "second.txt")));
        if (partial) Assert.Equal(fixture.FirstWriteUtc, File.GetLastWriteTimeUtc(Path.Combine(repo, "first.txt")));
    }

    private sealed class RefusingProvider : IAsyncDisposable
    {
        private readonly HttpListener listener = new();
        private readonly Task loop;
        private readonly bool partial;
        private readonly string firstFile;
        public DateTime? FirstWriteUtc { get; private set; }
        public List<string> Requests { get; } = [];
        public string Endpoint { get; }
        public RefusingProvider(bool partial, string firstFile)
        {
            this.partial = partial;
            this.firstFile = firstFile;
            var portProbe = new TcpListener(IPAddress.Loopback, 0); portProbe.Start();
            var port = ((IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
            Endpoint = $"http://127.0.0.1:{port}/v1";
            listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
            loop = Task.Run(ServeAsync);
        }
        private async Task ServeAsync()
        {
            try
            {
                while (listener.IsListening)
                {
                    var context = await listener.GetContextAsync();
                    try { await ReplyAsync(context); }
                    catch (Exception ex) { context.Response.StatusCode = 500; await WriteAsync(context, ex.ToString()); }
                    finally { context.Response.Close(); }
                }
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { }
        }
        private async Task ReplyAsync(HttpListenerContext context)
        {
            if (context.Request.HttpMethod == "GET")
            { await WriteAsync(context, "{\"object\":\"list\",\"data\":[{\"id\":\"controlled-refusal\",\"object\":\"model\"}]}"); return; }
            var body = await new StreamReader(context.Request.InputStream).ReadToEndAsync(); Requests.Add(body);
            var request = JsonNode.Parse(body)!;
            var messages = request["messages"]!.AsArray();
            bool Received(string id) => messages.Any(m => (string?)m?["role"] == "tool" && (string?)m?["tool_call_id"] == id);
            if (partial && Received("fixture-first")) FirstWriteUtc ??= File.GetLastWriteTimeUtc(firstFile);
            string? tool = null, callId = null; object? args = null;
            if (partial && !Received("fixture-first"))
            { tool = "shell.exec"; callId = "fixture-first"; args = new { command = "printf 'FIRST\\n' >> first.txt" }; }
            else if (!Received("fixture-decline"))
            { tool = "harness.decline"; callId = "fixture-decline"; args = new { scope = partial ? "part" : "all", reason = "Controlled refusal fixture for a harmless handoff regression test.", remaining = partial ? "The FIRST append succeeded. Do not repeat it. Append SECOND to second.txt exactly once and verify both files have one expected line." : "Perform both requested appends exactly once and verify both files." }; }
            var name = tool is null ? null : request["tools"]!.AsArray().Select(t => (string)t!["function"]!["name"]!)
                .Single(n => n.Replace('_', '.') == tool || n == tool);
            var calls = tool is null ? null : new[] { new { index = 0, id = callId, type = "function", function = new { name, arguments = JsonSerializer.Serialize(args) } } };
            var content = tool is null ? "I cannot complete the remaining request in this controlled refusal test." : null;
            var reason = tool is null ? "stop" : "tool_calls";
            if ((bool?)request["stream"] == true)
            {
                context.Response.ContentType = "text/event-stream";
                await WriteAsync(context, "data: " + JsonSerializer.Serialize(new { id = "fixture", model = "controlled-refusal", choices = new[] { new { index = 0, delta = new { role = "assistant", content, tool_calls = calls }, finish_reason = (string?)null } } }) + "\n\n");
                await WriteAsync(context, "data: " + JsonSerializer.Serialize(new { id = "fixture", model = "controlled-refusal", choices = new[] { new { index = 0, delta = new { }, finish_reason = reason } }, usage = new { prompt_tokens = 100, completion_tokens = 50 } }) + "\n\ndata: [DONE]\n\n");
            }
            else await WriteAsync(context, JsonSerializer.Serialize(new { id = "fixture", model = "controlled-refusal", choices = new[] { new { index = 0, message = new { role = "assistant", content, tool_calls = calls }, finish_reason = reason } }, usage = new { prompt_tokens = 100, completion_tokens = 50 } }));
        }
        private static async Task WriteAsync(HttpListenerContext context, string text)
            => await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(text));
        public async ValueTask DisposeAsync() { listener.Close(); await loop; }
    }
}
