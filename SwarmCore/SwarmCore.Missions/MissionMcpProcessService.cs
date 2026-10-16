using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SwarmCore.Abstractions.Processes;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Continual;

namespace SwarmCore.Missions;

public sealed record MissionMcpProcessInput(string Command, string[] Arguments, string? WorkingDirectory = null,
    string? Tool = null, Dictionary<string, JsonElement>? ToolArguments = null);
public sealed record MissionMcpProcessResult(bool Succeeded, string Content) : IToolExecutionEvidence
{ public bool LocalProcessCompleted => true; }

/// <summary>General stdio MCP execution in a fresh kernel-contained process. Tool metadata
/// does not grant filesystem/network access; descendants inherit the mission sandbox.</summary>
public sealed class MissionMcpProcessService(ISandboxPathResolver paths)
{
    public async Task<MissionMcpProcessResult> ExecuteAsync(MissionMcpProcessInput input, CancellationToken ct)
    {
        if (!MissionPrimitiveFence.IsMission) throw new MissionActionSkippedException("An active mission is required.");
        var start = new ProcessStartInfo(input.Command)
        {
            WorkingDirectory = input.WorkingDirectory is null ? paths.Root : paths.Resolve(input.WorkingDirectory),
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var argument in input.Arguments) start.ArgumentList.Add(argument);
        await Task.Run(() => MissionProcessSandbox.Apply(start), ct);
        await MissionPrimitiveFence.ValidateAsync(ct);
        using var child = await Task.Run(() => MissionProcessSandbox.Start(start, ct), ct);
        child.BeginErrorReadLine(); // Drain diagnostics without exposing unclassified server stderr.
        using var cancellation = ct.Register(() => { try { child.Kill(true); } catch (InvalidOperationException) { } });
        try
        {
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(child.StandardInput.BaseStream, child.StandardOutput.BaseStream), cancellationToken: ct);
            if (input.Tool is null)
            {
                var tools = await client.ListToolsAsync(cancellationToken: ct);
                return new(true, JsonSerializer.Serialize(tools.Select(t => new { t.Name, t.Description, t.JsonSchema })));
            }
            await MissionPrimitiveFence.ValidateAsync(ct);
            var args = input.ToolArguments?.ToDictionary(p => p.Key, p => (object?)p.Value);
            var result = await client.CallToolAsync(input.Tool, args, cancellationToken: ct);
            return new(result.IsError != true, JsonSerializer.Serialize(result.Content));
        }
        finally
        {
            try { if (!child.HasExited) child.Kill(true); } catch (InvalidOperationException) when (child.HasExited) { }
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
