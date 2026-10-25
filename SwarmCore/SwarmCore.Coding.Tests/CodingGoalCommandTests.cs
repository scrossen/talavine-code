using SwarmCore.Coding.Goals;

namespace SwarmCore.Coding.Tests;

public sealed class CodingGoalCommandTests
{
    [Theory]
    [InlineData("/goal", true)]
    [InlineData("/GOAL fix this", true)]
    [InlineData("/goal\nfix this", true)]
    [InlineData("/goals fix this", false)]
    [InlineData("/goal/file.txt", false)]
    [InlineData("goal fix this", false)]
    public void Invocation_requires_the_complete_command_name(string text, bool expected)
        => Assert.Equal(expected, CodingGoalCommand.IsInvocation(text));

    [Theory]
    [InlineData(null, CodingGoalCommandKind.Status)]
    [InlineData("", CodingGoalCommandKind.Status)]
    [InlineData(" \t\r\n ", CodingGoalCommandKind.Status)]
    [InlineData("status", CodingGoalCommandKind.Status)]
    [InlineData(" STATUS ", CodingGoalCommandKind.Status)]
    [InlineData("pause", CodingGoalCommandKind.Pause)]
    [InlineData("ReSuMe", CodingGoalCommandKind.Resume)]
    [InlineData("cancel", CodingGoalCommandKind.Cancel)]
    public void Control_commands_are_case_insensitive_and_never_become_objectives(string? arguments, CodingGoalCommandKind expected)
    {
        var command = CodingGoalCommand.Parse(arguments);
        Assert.Equal(expected, command.Kind);
        Assert.Null(command.Objective);
        Assert.Null(command.Error);
    }

    [Theory]
    [InlineData("  Make the build green  ", "Make the build green")]
    [InlineData("Fix  this\nand verify\tthat", "Fix  this\nand verify\tthat")]
    [InlineData("start Make the build green", "Make the build green")]
    [InlineData("START\n  status reports show current progress  ", "status reports show current progress")]
    [InlineData("start pause", "pause")]
    [InlineData("start start a new app", "start a new app")]
    public void Start_retains_the_complete_objective_and_supports_command_words(string arguments, string expected)
    {
        var command = CodingGoalCommand.Parse(arguments);
        Assert.Equal(CodingGoalCommandKind.Start, command.Kind);
        Assert.Equal(expected, command.Objective);
        Assert.Null(command.Error);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("start \r\n ")]
    [InlineData("status reports")]
    [InlineData("pause for now")]
    [InlineData("resume\nwork")]
    [InlineData("cancel this")]
    public void Missing_objectives_and_ambiguous_control_arguments_do_not_dispatch_work(string arguments)
    {
        var command = CodingGoalCommand.Parse(arguments);
        Assert.Equal(CodingGoalCommandKind.Invalid, command.Kind);
        Assert.Null(command.Objective);
        Assert.Contains("/goal start", command.Error);
    }
}
