using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Routing;

namespace SwarmCore.Coding.Tests;

public sealed class CodingInspectionProgressTests
{
    private static ToolCallCompleted Result(string tool, string result) => new("turn", Guid.NewGuid().ToString("N"), tool, result, false, TimeSpan.Zero);

    [Theory]
    [InlineData("fs.write_file")]
    [InlineData("notebook.edit")]
    public void Fresh_evidence_and_file_writes_reset_repetition_but_successful_commands_do_not(string writer)
    {
        var progress = new CodingInspectionProgress(2);
        Assert.False(progress.Observe(Result("code.glob", "empty")));
        Assert.False(progress.Observe(Result("code.glob", "empty")));
        Assert.False(progress.Observe(Result("shell.exec", "directory listed")));
        Assert.True(progress.Observe(Result("code.glob", "empty")));
        Assert.False(progress.Observe(Result("fs.read_file", "new evidence")));
        Assert.False(progress.Observe(Result("code.glob", "empty")));
        Assert.False(progress.Observe(Result(writer, "saved")));
        Assert.False(progress.Observe(Result("code.glob", "empty")));
        Assert.False(progress.Observe(Result("code.glob", "empty")));
        Assert.True(progress.Observe(Result("code.glob", "empty")));
    }
}
