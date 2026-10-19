using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace SwarmCode.Hosting.Tests;

/// <summary>Real local inference, tool execution and compaction with synthetic files outside the workspace.</summary>
public sealed class LocalFileAccessLiveTests : TempDirTest
{
    [OllamaSetupLiveFact]
    [Trait("LlmMode", "Live")]
    public async Task UserRequestedOutsideWorkspaceReadSurvivesCompaction()
    {
        var repo = Directory.CreateDirectory(Path.Combine(Tmp, "repo")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(Tmp, "outside")).FullName;
        var model = Environment.GetEnvironmentVariable("SWARMCODE_OLLAMA_TEST_MODEL")
            ?? throw new InvalidOperationException("Set SWARMCODE_OLLAMA_TEST_MODEL to an installed tool-capable model.");
        var configuration = SwarmCodeHostComposer.BuildConfiguration(Paths, new Dictionary<string, string?>
        {
            ["Coding:Agents:Enabled"] = "false", ["Coding:FallbackDiscovery:Enabled"] = "false",
            ["Coding:HardTimeout"] = "00:03:00", ["Coding:MaxIterations"] = "8",
            ["PersonalMemory:Enabled"] = "false",
        });
        await using var host = await WorkspaceHost.CreateAsync(new() { DataPaths = Paths, RepositoryRoot = repo, Configuration = configuration });
        var lane = Assert.Single(host.Lanes, l => l.Provider == "Ollama" && l.Model == model);
        var session = await host.Services.GetRequiredService<WorkspaceSessionCatalog>().CreateAsync(repo, "Local file access", lane.Name);
        await using var runner = host.OpenSession(session.Id);
        try
        {
            for (var turn = 0; turn < 2; turn++)
            {
                var file = Path.Combine(outside, $"probe-{turn}.txt");
                var expected = "LOCAL_ACCESS_" + Guid.NewGuid().ToString("N");
                await File.WriteAllTextAsync(file, expected);
                if (turn > 0) Assert.True(runner.RequestCompaction());
                var start = runner.Entries.Count;
                var finished = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnFinished(TurnResult result) => finished.TrySetResult(result);
                runner.TurnFinished += OnFinished;
                try
                {
                    Assert.True(runner.TrySend($"Why can't you see my entire computer? Check your actual access by reading this one local text file: {file}. "
                        + "I authorize that read. Return its exact contents and briefly explain whether the workspace limits all local file access. Do not change any files.",
                        lane.Name, allowHandOff: false));
                    var result = await finished.Task.WaitAsync(TimeSpan.FromMinutes(4));
                    Assert.Null(result.Error);
                    Assert.False(result.Cancelled);
                    Assert.True(result.Completed);
                    Assert.Equal([lane.Name], result.LanesUsed);
                    var entries = runner.Entries.Skip(start).ToArray();
                    Assert.Contains(entries.OfType<ToolCallEntry>(), t => t.State == ToolCallState.Completed
                        && (t.ToolName == "fs.read_file" || t.ToolName == "shell.exec") && t.Result?.Contains(expected) == true);
                    Assert.Contains(entries.OfType<AssistantEntry>(), a => a.Text.Contains(expected));
                }
                finally { runner.TurnFinished -= OnFinished; }
            }
            Assert.Contains(runner.Entries.OfType<NoticeEntry>(), n => n.Kind == NoticeKind.Compaction
                && n.Text.StartsWith("Context compacted:", StringComparison.Ordinal));
        }
        finally
        {
            if (runner.IsRunning) runner.Cancel();
            var evidence = Environment.GetEnvironmentVariable("SWARMCODE_LOCAL_ACCESS_EVIDENCE_DIR");
            if (!string.IsNullOrWhiteSpace(evidence))
            {
                Directory.CreateDirectory(evidence);
                await File.WriteAllTextAsync(Path.Combine(evidence, "entries.json"), JsonSerializer.Serialize(
                    runner.Entries.Select(e => new { Type = e.GetType().Name, Value = JsonSerializer.SerializeToElement(e, e.GetType()) }),
                    new JsonSerializerOptions { WriteIndented = true }));
            }
        }
    }
}
