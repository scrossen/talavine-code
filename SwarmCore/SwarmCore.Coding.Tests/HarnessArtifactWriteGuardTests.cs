using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Experiments;
using SwarmCore.Missions;

namespace SwarmCore.Coding.Tests;

public sealed class HarnessArtifactWriteGuardTests
{
    private sealed class Paths : ISandboxPathResolver
    {
        public string Root => Path.GetFullPath("fixture-root");
        public bool OutputRetargeted { get; set; }
        public string Resolve(string path)
        {
            if (OutputRetargeted && path == "result.json") return Path.Combine(Root, "input.py");
            var target = Path.GetFullPath(path, Root);
            if (!target.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new UnauthorizedAccessException();
            return target;
        }
    }

    [Theory]
    [InlineData("fs.write_file", "result.json", true)]
    [InlineData("code.edit_file", "result.json", true)]
    [InlineData("fs.write_file", "input.py", false)]
    [InlineData("fs.write_file", "../result.json", false)]
    [InlineData("fs.write_file", "result.json/other", false)]
    [InlineData("unknown.tool", "result.json", false)]
    public async Task Only_declared_artifact_writes_are_admitted(string tool, string path, bool expected)
    {
        var guard = new HarnessArtifactWriteGuard(new Paths(), ["result.json"],
            new Dictionary<string, string> { ["fs.write_file"] = "path", ["code.edit_file"] = "path" });
        var decision = await guard.InspectAsync(new(tool, ToolSideEffect.WritesLocal, new { Path = path }, "fixture"));
        Assert.Equal(expected, decision.Allowed);
    }

    [Fact]
    public async Task An_execution_tool_cannot_be_admitted_by_forging_a_file_path()
    {
        var guard = new HarnessArtifactWriteGuard(new Paths(), ["result.json"], new Dictionary<string, string> { ["shell.exec"] = "path" });
        var decision = await guard.InspectAsync(new("shell.exec", ToolSideEffect.Exec, new { path = "result.json" }, "fixture"));
        Assert.False(decision.Allowed);
    }

    [Fact]
    public async Task A_retargeted_output_cannot_redirect_the_grant_to_an_input()
    {
        var paths = new Paths();
        var guard = new HarnessArtifactWriteGuard(paths, ["result.json"], new Dictionary<string, string> { ["fs.write_file"] = "path" });
        paths.OutputRetargeted = true;
        var decision = await guard.InspectAsync(new("fs.write_file", ToolSideEffect.WritesLocal, new { path = "result.json" }, "fixture"));
        Assert.False(decision.Allowed);
    }
}
