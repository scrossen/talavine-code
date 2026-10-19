using System.Runtime.CompilerServices;
using SwarmCode.Ui;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Extensions;
using SwarmCore.Coding.Routing;

namespace SwarmCode.Tests;

[CollectionDefinition("Console input", DisableParallelization = true)]
public sealed class ConsoleInputCollection { }

[Collection("Console input")]
public sealed class GreetingCommandTests
{
    [Theory]
    [InlineData("/greeting")]
    [InlineData("  /GREETING  ")]
    public async Task Repl_greets_locally_and_lists_command_in_help(string command)
    {
        await using var scratch = Talavine.TestSupport.TestTempDirectory.Create("code-greeting");
        // A repository template cannot replace the built-in greeting with a model prompt.
        var commands = Path.Combine(scratch.Path, ".swarmcode", "commands");
        Directory.CreateDirectory(commands);
        await File.WriteAllTextAsync(Path.Combine(commands, "greeting.md"), "Send this to a model instead.");
        var orchestrator = new RecordingOrchestrator();
        var renderer = new ConsoleRenderer(new() { Quiet = true }, new StringWriter(), new StringWriter());
        var runner = new TurnRunner(orchestrator, renderer, false);
        var repl = new Repl(orchestrator, runner, null!, null!, scratch.Path, null, null);
        var originalIn = Console.In;
        var originalOut = Console.Out;
        using var input = new StringReader(command + "\n/help\n/quit\n");
        using var output = new StringWriter();
        try
        {
            Console.SetIn(input);
            Console.SetOut(output);
            await repl.RunAsync();
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
        }

        Assert.Contains(GreetingCommand.Message, output.ToString());
        Assert.Contains("/greeting                show a welcome message", output.ToString());
        Assert.DoesNotContain("Unknown command", output.ToString());
        Assert.DoesNotContain("Send this to a model instead.", output.ToString());
        Assert.Equal(0, orchestrator.Turns);
        Assert.Contains("/greeting", CliOptions.HelpText);
    }

    private sealed class RecordingOrchestrator : ICodingTurnOrchestrator
    {
        public int Turns { get; private set; }
        public IReadOnlyList<CodingLane> Lanes => [];
        public CodingLane? ResolveLane(string? name) => null;
        public async IAsyncEnumerable<TurnEvent> RunAsync(CodingTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Turns++;
            await Task.Yield();
            yield return new TurnCompleted("unexpected", "Unexpected model call", TimeSpan.Zero);
        }
    }
}
