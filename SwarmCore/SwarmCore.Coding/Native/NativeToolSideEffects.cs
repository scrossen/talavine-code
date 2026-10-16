using System.Text.Json.Nodes;
using SwarmCore.Agent.Catalog;

namespace SwarmCore.Coding.Native;

/// <summary>Receipt classification of native built-in tools, never a permission grant.
/// Unknown and external MCP tools stay unclassified so the host verifies their outcome conservatively.</summary>
internal static class NativeToolSideEffects
{
    public static ToolSideEffect? Claude(string name) => name switch
    {
        "Edit" or "Write" or "MultiEdit" or "NotebookEdit" => ToolSideEffect.WritesLocal,
        "Read" or "Glob" or "Grep" => ToolSideEffect.ReadOnly,
        "Bash" or "PowerShell" => ToolSideEffect.Exec,
        _ when name.StartsWith(ClaudeBridgePrefix, StringComparison.Ordinal) => HostBridge(name[ClaudeBridgePrefix.Length..]),
        _ => null,
    };

    /// <summary>How Claude Code names a tool of SwarmCode's own MCP bridge.</summary>
    public const string ClaudeBridgePrefix = "mcp__" + McpServerLaunch.DefaultServerName + "__";

    /// <summary>
    /// SwarmCode's own bridge tools (the host's catalog tools under their MCP names). Only the host bridge server is
    /// classified; any other MCP server stays unclassified.
    /// </summary>
    public static ToolSideEffect? HostBridge(string bridgeToolName) => bridgeToolName switch
    {
        BridgeNotebookEdit or "git_switch_branch" => ToolSideEffect.WritesLocal,
        "notebook_read" or "credentials_list" or "schedules_list" or "shell_background_output" or "shell_background_list"
            or "github_pr_status" or "github_pr_for_branch" or "agents_wait" or "agents_status" or "agents_list" => ToolSideEffect.ReadOnly,
        "run_with_secrets" or "shell_background_start" => ToolSideEffect.Exec,
        "git_push" or "github_pr_create" or "github_pr_comment" => ToolSideEffect.ExternalSend,
        _ => null,
    };

    /// <summary>The bridge's notebook edit: a repository file change like Claude's own NotebookEdit.</summary>
    public const string BridgeNotebookEdit = "notebook_edit";

    /// <summary>Codex reports every MCP call under one tool name; the server and tool are in its input.</summary>
    public static ToolSideEffect? Codex(string name, JsonObject? input)
        => name == CodexNativeLaneRunner.McpToolName && IsHostBridgeServer(input) && (string?)input!["tool"] is { } tool
            ? HostBridge(tool)
            : Codex(name);

    /// <summary>True for a Codex MCP call to SwarmCode's bridge whose tool changes repository files.</summary>
    public static bool IsCodexBridgeFileChange(string name, JsonObject? input)
        => name == CodexNativeLaneRunner.McpToolName && IsHostBridgeServer(input) && (string?)input!["tool"] == BridgeNotebookEdit;

    private static bool IsHostBridgeServer(JsonObject? input)
        => input is not null && string.Equals((string?)input["server"], McpServerLaunch.DefaultServerName, StringComparison.Ordinal);

    public static ToolSideEffect? Codex(string name) => name switch
    {
        CodexNativeLaneRunner.FileChangeToolName => ToolSideEffect.WritesLocal,
        CodexNativeLaneRunner.CommandToolName => ToolSideEffect.Exec,
        CodexNativeLaneRunner.WebSearchToolName => ToolSideEffect.ReadOnly,
        _ => null,
    };

    public static ToolSideEffect? Gemini(string name) => name switch
    {
        "write_file" or "replace" => ToolSideEffect.WritesLocal,
        "read_file" or "read_many_files" or "list_directory" or "glob" or "search_file_content" => ToolSideEffect.ReadOnly,
        "run_shell_command" => ToolSideEffect.Exec,
        _ => null,
    };
}
