using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Tools;
using SwarmCore.Missions;

namespace SwarmCore.Coding.Tests;

public sealed class NotebookToolsTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("notebook-tools-");
    private readonly SandboxPathResolver _sandbox;
    private readonly AsyncLocalCurrentTurnContextAccessor _accessor = new();

    public NotebookToolsTests()
    {
        _sandbox = new SandboxPathResolver(Options.Create(new SandboxOptions { Root = _root.FullName }));
    }

    public void Dispose()
    {
        _accessor.Current = null;
        try { _root.Delete(recursive: true); } catch { /* best effort */ }
    }

    // Written the way Jupyter writes (json.dumps indent=1, sort_keys, ensure_ascii=False, trailing newline).
    private static readonly string Nb45 = Nb45Raw.Replace("\r\n", "\n");
    private const string Nb45Raw = """
{
 "cells": [
  {
   "cell_type": "markdown",
   "id": "aaaa0001",
   "metadata": {
    "tags": [
     "intro"
    ]
   },
   "source": [
    "# Título\n",
    "Some text"
   ]
  },
  {
   "cell_type": "code",
   "execution_count": 3,
   "id": "bbbb0002",
   "metadata": {
    "scrolled": true
   },
   "outputs": [
    {
     "name": "stdout",
     "output_type": "stream",
     "text": [
      "hello\n"
     ]
    },
    {
     "data": {
      "image/png": "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk",
      "text/plain": [
       "<Figure size 640x480 with 1 Axes>"
      ]
     },
     "execution_count": 3,
     "metadata": {},
     "output_type": "execute_result"
    },
    {
     "ename": "ValueError",
     "evalue": "bad",
     "output_type": "error",
     "traceback": [
      "\u001b[0;31mValueError\u001b[0m: bad"
     ]
    }
   ],
   "source": [
    "print('hello')\n",
    "plot()"
   ]
  },
  {
   "cell_type": "raw",
   "id": "cccc0003",
   "metadata": {},
   "source": []
  }
 ],
 "metadata": {
  "kernelspec": {
   "display_name": "Python 3 (ipykernel)",
   "language": "python",
   "name": "python3"
  },
  "language_info": {
   "name": "python",
   "version": "3.11.4"
  },
  "zeta": 1.50,
  "alpha": true
 },
 "nbformat": 4,
 "nbformat_minor": 5
}

""";

    private static readonly string Nb44 = Nb44Raw.Replace("\r\n", "\n");
    private const string Nb44Raw = """
{
 "cells": [
  {
   "cell_type": "code",
   "execution_count": null,
   "metadata": {},
   "outputs": [],
   "source": [
    "x = 1"
   ]
  }
 ],
 "metadata": {},
 "nbformat": 4,
 "nbformat_minor": 4
}

""";

    private string Write(string rel, string content)
    {
        var path = Path.Combine(_root.FullName, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private async Task<NotebookReadTool.Output> Read(NotebookReadTool.Input input)
        => (NotebookReadTool.Output)(await new NotebookReadTool(_sandbox).Descriptor.Invoke(input, default))!;

    private async Task<NotebookEditTool.Output> Edit(NotebookEditTool.Input input)
        => (NotebookEditTool.Output)(await new NotebookEditTool(_sandbox).Descriptor.Invoke(input, default))!;

    private static JsonArray Cells(string path) => (JsonArray)JsonNode.Parse(File.ReadAllText(path))!["cells"]!;

    [Fact]
    public async Task Read_returns_cells_and_summarizes_outputs_without_base64()
    {
        Write("nb.ipynb", Nb45);
        var r = await Read(new() { Path = "nb.ipynb" });
        Assert.True(r.Succeeded, r.Error);
        Assert.Equal("4.5", r.NbFormat);
        Assert.Equal("Python 3 (ipykernel)", r.Kernel);
        Assert.Equal("python", r.Language);
        Assert.Equal(3, r.CellCount);
        Assert.False(r.Truncated);
        var code = r.Cells![1];
        Assert.Equal("bbbb0002", code.Id);
        Assert.Equal("code", code.Type);
        Assert.Equal("print('hello')\nplot()", code.Source);
        Assert.Equal(3, code.ExecutionCount);
        Assert.Equal("[stdout] hello\n", code.Outputs![0]);
        Assert.Contains("[image/png output]", code.Outputs[1]);
        Assert.Contains("<Figure size", code.Outputs[1]);
        Assert.StartsWith("[error] ValueError: bad", code.Outputs[2]);
        Assert.False(code.Outputs[2].Contains('\u001b'), "ANSI escapes were not stripped");
        Assert.DoesNotContain(code.Outputs, o => o.Contains("iVBOR", StringComparison.Ordinal));
        Assert.Equal("# Título\nSome text", r.Cells[0].Source);
        Assert.Equal("", r.Cells[2].Source);
    }

    [Fact]
    public async Task Read_honors_cell_range_and_output_cap()
    {
        var long45 = Nb45.Replace("\"hello\\n\"", "\"" + new string('x', 500) + "\"");
        Write("nb.ipynb", long45);
        var r = await Read(new() { Path = "nb.ipynb", StartIndex = 1, MaxCells = 1, MaxOutputChars = 100 });
        Assert.True(r.Succeeded, r.Error);
        Assert.True(r.Truncated);
        var cell = Assert.Single(r.Cells!);
        Assert.Equal(1, cell.Index);
        Assert.True(cell.OutputsTruncated);
        Assert.Contains("more chars]", cell.Outputs![0]);
        Assert.True(cell.Outputs.Sum(o => o.Length) < 400);
    }

    [Fact]
    public void Unchanged_notebook_round_trips_byte_for_byte()
    {
        Assert.Equal(Nb45, NotebookDocument.Parse(Nb45).Serialize());
        Assert.Equal(Nb44, NotebookDocument.Parse(Nb44).Serialize());
        var crlf = Nb45.Replace("\n", "\r\n");
        Assert.Equal(crlf, NotebookDocument.Parse(crlf).Serialize());
        var twoSpaces = "{\n  \"cells\": [],\n  \"metadata\": {},\n  \"nbformat\": 4,\n  \"nbformat_minor\": 2\n}";
        Assert.Equal(twoSpaces, NotebookDocument.Parse(twoSpaces).Serialize());
        var ascii = Nb45.Replace("Título", "T\\u00edtulo");
        Assert.Equal(ascii, NotebookDocument.Parse(ascii).Serialize());
    }

    [Fact]
    public async Task Replace_code_source_clears_outputs_and_preserves_everything_else()
    {
        var path = Write("nb.ipynb", Nb45);
        var r = await Edit(new() { Path = "nb.ipynb", CellId = "bbbb0002", Source = "print('bye')\nplot(2)\n" });
        Assert.True(r.Succeeded, r.Error);
        Assert.Equal(1, r.CellIndex);
        Assert.Contains("-print('hello')", r.Preview);
        Assert.Contains("+print('bye')", r.Preview);

        var expected = Nb45[..Nb45.IndexOf("   \"execution_count\": 3,", StringComparison.Ordinal)]
            + "   \"execution_count\": null,\n   \"id\": \"bbbb0002\",\n   \"metadata\": {\n    \"scrolled\": true\n   },\n   \"outputs\": [],\n   \"source\": [\n    \"print('bye')\\n\",\n    \"plot(2)\\n\"\n   ]\n  },"
            + Nb45[(Nb45.IndexOf("  {\n   \"cell_type\": \"raw\"", StringComparison.Ordinal) - 1)..];
        Assert.Equal(expected, File.ReadAllText(path));
    }

    [Fact]
    public async Task Replace_markdown_by_index_keeps_metadata_and_sorted_key_order()
    {
        var path = Write("nb.ipynb", Nb45);
        var r = await Edit(new() { Path = "nb.ipynb", CellIndex = 0, Source = "# New" });
        Assert.True(r.Succeeded, r.Error);
        var text = File.ReadAllText(path);
        Assert.Equal(Nb45.Replace("    \"# Título\\n\",\n    \"Some text\"\n", "    \"# New\"\n"), text);
        Assert.Contains("\"zeta\": 1.50,\n  \"alpha\": true", text);
    }

    [Fact]
    public async Task Edit_preserves_crlf_line_endings_and_missing_trailing_newline()
    {
        var crlf = Nb45.TrimEnd('\n').Replace("\n", "\r\n");
        var path = Write("nb.ipynb", crlf);
        var r = await Edit(new() { Path = "nb.ipynb", CellIndex = 0, Source = "# New" });
        Assert.True(r.Succeeded, r.Error);
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("\r\r", text);
        Assert.Equal(text.Count(c => c == '\n'), text.Split("\r\n").Length - 1);
        Assert.EndsWith("}", text);
        Assert.Equal(crlf.Replace("    \"# Título\\n\",\r\n    \"Some text\"\r\n", "    \"# New\"\r\n"), text);
    }

    [Fact]
    public async Task Insert_generates_an_id_for_nbformat_4_5_in_sorted_key_order()
    {
        var path = Write("nb.ipynb", Nb45);
        var r = await Edit(new() { Path = "nb.ipynb", Operation = "insert", CellId = "aaaa0001", Position = "after", CellType = "code", Source = "a = 1\nb = 2" });
        Assert.True(r.Succeeded, r.Error);
        Assert.Equal(1, r.CellIndex);
        Assert.Matches("^[0-9a-f]{8}$", r.CellId);
        var cells = Cells(path);
        Assert.Equal(4, cells.Count);
        var cell = (JsonObject)cells[1]!;
        Assert.Equal(["cell_type", "execution_count", "id", "metadata", "outputs", "source"], cell.Select(p => p.Key).ToArray());
        Assert.Equal(["a = 1\n", "b = 2"], cell["source"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        Assert.Equal("bbbb0002", (string?)cells[2]!["id"]);

        var md = await Edit(new() { Path = "nb.ipynb", Operation = "insert", CellIndex = 0, Position = "before", CellType = "markdown", Source = "top" });
        Assert.True(md.Succeeded, md.Error);
        var first = (JsonObject)Cells(path)[0]!;
        Assert.Equal(["cell_type", "id", "metadata", "source"], first.Select(p => p.Key).ToArray());
        Assert.NotEqual(r.CellId, md.CellId);

        var appended = await Edit(new() { Path = "nb.ipynb", Operation = "insert", Source = "end", CellType = "raw" });
        Assert.True(appended.Succeeded, appended.Error);
        Assert.Equal(5, appended.CellIndex);
    }

    [Fact]
    public async Task Insert_into_nbformat_4_4_adds_no_id()
    {
        var path = Write("nb.ipynb", Nb44);
        var r = await Edit(new() { Path = "nb.ipynb", Operation = "insert", CellIndex = 1, Source = "y = 2" });
        Assert.True(r.Succeeded, r.Error);
        Assert.Null(r.CellId);
        var cell = (JsonObject)Cells(path)[1]!;
        Assert.False(cell.ContainsKey("id"));
        Assert.Equal(Nb44.Replace("  }\n ],", "  },\n  {\n   \"cell_type\": \"code\",\n   \"execution_count\": null,\n   \"metadata\": {},\n   \"outputs\": [],\n   \"source\": [\n    \"y = 2\"\n   ]\n  }\n ],"), File.ReadAllText(path));
    }

    [Fact]
    public async Task Delete_removes_the_cell()
    {
        var path = Write("nb.ipynb", Nb45);
        var r = await Edit(new() { Path = "nb.ipynb", Operation = "delete", CellId = "bbbb0002" });
        Assert.True(r.Succeeded, r.Error);
        Assert.Equal(2, r.CellCount);
        Assert.Contains("-plot()", r.Preview);
        var ids = Cells(path).Select(c => (string)c!["id"]!).ToArray();
        Assert.Equal(["aaaa0001", "cccc0003"], ids);
    }

    [Fact]
    public async Task Change_type_adds_and_drops_code_fields()
    {
        var path = Write("nb.ipynb", Nb45);
        var toMarkdown = await Edit(new() { Path = "nb.ipynb", Operation = "change_type", CellId = "bbbb0002", CellType = "markdown" });
        Assert.True(toMarkdown.Succeeded, toMarkdown.Error);
        var cell = (JsonObject)Cells(path)[1]!;
        Assert.Equal(["cell_type", "id", "metadata", "source"], cell.Select(p => p.Key).ToArray());
        Assert.Equal("markdown", (string?)cell["cell_type"]);
        Assert.Equal(true, (bool?)cell["metadata"]!["scrolled"]);

        var toCode = await Edit(new() { Path = "nb.ipynb", Operation = "change_type", CellIndex = 0, CellType = "code" });
        Assert.True(toCode.Succeeded, toCode.Error);
        var code = (JsonObject)Cells(path)[0]!;
        Assert.Equal(["cell_type", "execution_count", "id", "metadata", "outputs", "source"], code.Select(p => p.Key).ToArray());
        Assert.Contains("~ type: markdown -> code", toCode.Preview);

        var same = await Edit(new() { Path = "nb.ipynb", Operation = "change_type", CellIndex = 0, CellType = "code" });
        Assert.False(same.Succeeded);
    }

    [Fact]
    public async Task Bad_targets_and_inputs_fail_without_writing()
    {
        Write("nb.ipynb", Nb45);
        var before = File.ReadAllBytes(Path.Combine(_root.FullName, "nb.ipynb"));
        Assert.Contains("No cell has id", (await Edit(new() { Path = "nb.ipynb", CellId = "nope", Source = "x" })).Error);
        Assert.Contains("out of range", (await Edit(new() { Path = "nb.ipynb", CellIndex = 9, Source = "x" })).Error);
        Assert.Contains("not 0", (await Edit(new() { Path = "nb.ipynb", CellId = "bbbb0002", CellIndex = 0, Source = "x" })).Error);
        Assert.Contains("Unknown operation", (await Edit(new() { Path = "nb.ipynb", Operation = "move", CellIndex = 0 })).Error);
        Assert.Contains("Unknown cell type", (await Edit(new() { Path = "nb.ipynb", Operation = "insert", CellType = "sql" })).Error);
        Assert.Contains("changed since", (await Edit(new() { Path = "nb.ipynb", CellIndex = 0, Source = "x", ExpectedSha256 = "00" })).Error);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(_root.FullName, "nb.ipynb")));
    }

    [Fact]
    public async Task Invalid_notebooks_get_a_clear_error()
    {
        Write("broken.ipynb", "{ \"cells\": [ ");
        var broken = await Read(new() { Path = "broken.ipynb" });
        Assert.False(broken.Succeeded);
        Assert.Contains("not valid JSON", broken.Error);

        Write("nocells.ipynb", "{ \"nbformat\": 4 }");
        Assert.Contains("\"cells\"", (await Edit(new() { Path = "nocells.ipynb", CellIndex = 0, Source = "x" })).Error);

        Write("v3.ipynb", "{ \"cells\": [], \"nbformat\": 3 }");
        Assert.Contains("nbformat 3", (await Read(new() { Path = "v3.ipynb" })).Error);

        Write("notes.txt", Nb45);
        Assert.Contains(".ipynb", (await Read(new() { Path = "notes.txt" })).Error);

        Assert.Contains("not found", (await Read(new() { Path = "missing.ipynb" })).Error);
    }

    [Fact]
    public async Task Paths_outside_the_workspace_and_git_metadata_are_refused()
    {
        var outside = Path.Combine(Path.GetTempPath(), "outside-" + Guid.NewGuid().ToString("N") + ".ipynb");
        File.WriteAllText(outside, Nb44);
        try
        {
            _accessor.Current = new CurrentTurnContext(
                "chat", null, Guid.NewGuid(), CommunicationScopeKind: AgentCommunicationScopeKind.TrustedProfileWide,
                CommunicationScopeAuthority: "user", CommunicationScopeUserInitiated: true, UnattendedAgentTurn: true)
            { WorkspaceRoot = _root.FullName };
            var fenced = new SandboxPathResolver(Options.Create(new SandboxOptions { Root = _root.FullName, AllowAbsolutePathsOutsideTurnWorkspace = false }));
            var edit = new NotebookEditTool(fenced).Descriptor;
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => edit.Invoke(
                new NotebookEditTool.Input { Path = "../" + Path.GetFileName(outside), CellIndex = 0, Source = "x" }, default));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => edit.Invoke(
                new NotebookEditTool.Input { Path = outside, CellIndex = 0, Source = "x" }, default));

            Write(".git/hooks/nb.ipynb", Nb44);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => edit.Invoke(
                new NotebookEditTool.Input { Path = ".git/hooks/nb.ipynb", CellIndex = 0, Source = "x" }, default));
            Assert.Equal(Nb44, File.ReadAllText(Path.Combine(_root.FullName, ".git", "hooks", "nb.ipynb")));
            Assert.Equal(Nb44, File.ReadAllText(outside));
        }
        finally
        {
            _accessor.Current = null;
            File.Delete(outside);
        }
    }

    [Fact]
    public void Notebook_tools_are_harness_tools_with_edit_classification()
    {
        Assert.Contains(NotebookReadTool.ToolId, CodingToolRegistrar.CodingToolIds);
        Assert.Contains(NotebookEditTool.ToolId, CodingToolRegistrar.CodingToolIds);
        Assert.Equal(SwarmCore.Agent.Catalog.ToolSideEffect.ReadOnly, new NotebookReadTool(_sandbox).Descriptor.SideEffect);
        Assert.Equal(new EditFileTool(_sandbox).Descriptor.SideEffect, new NotebookEditTool(_sandbox).Descriptor.SideEffect);
        Assert.Equal(new EditFileTool(_sandbox).Descriptor.Cost, new NotebookEditTool(_sandbox).Descriptor.Cost);
    }
}
