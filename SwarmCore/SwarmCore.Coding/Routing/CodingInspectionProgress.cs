using System.Security.Cryptography;
using System.Text;
using SwarmCore.Agent.Runtime;

namespace SwarmCore.Coding.Routing;

/// <summary>Detects evidence-free inspection loops during a host-authenticated editing task.</summary>
internal sealed class CodingInspectionProgress(int limit)
{
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private int repeated;

    public bool Observe(TurnEvent evt)
    {
        if (limit <= 0 || evt is not ToolCallCompleted { IsError: false } result) return false;
        if (result.ToolName is "fs.write_file" or "code.edit_file" or Tools.NotebookEditTool.ToolId
            or "Write" or "Edit" or "MultiEdit" or "NotebookEdit" or "write_file" or "replace")
        {
            seen.Clear();
            repeated = 0;
            return false;
        }
        // A successful command such as `ls` is not proof of a file change and
        // must not reset inspection history. Unknown tools are not classified.
        if (result.ToolName is not ("code.glob" or "code.grep" or "fs.list_files" or "fs.read_file" or Tools.ReadJsonTool.ToolId
            or "git.status" or "git.diff" or "Read" or "Glob" or "Grep")) return false;
        var key = result.ToolName + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(result.Result)));
        repeated = seen.Add(key) ? 0 : repeated + 1;
        return repeated >= limit;
    }
}
