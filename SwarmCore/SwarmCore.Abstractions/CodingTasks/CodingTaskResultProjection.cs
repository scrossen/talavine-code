using System.Text.Json;

namespace SwarmCore.Abstractions.CodingTasks;

/// <summary>Bounds presentation only. Durable mission proof and complete local artifacts remain unchanged.</summary>
public static class CodingTaskResultProjection
{
    public const int MaximumCharacters = 6000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static CodingWorkspaceIdentity WorkspaceForPresentation(CodingWorkspaceIdentity workspace)
        => workspace with { BaselineFiles = new Dictionary<string, string>(), GeneratedOutputDirectories = [] };

    public static CodingTaskResult Fit(CodingTaskResult original, Func<CodingTaskResult, int>? measure = null)
    {
        measure ??= value => JsonSerializer.Serialize(value, Json).Length;
        var result = original;
        if (original.Workspace is { } workspaceIdentity)
            result = result with
            {
                Truncated = result.Truncated || workspaceIdentity.BaselineFiles.Count > 0 || workspaceIdentity.GeneratedOutputDirectories.Count > 0,
                Workspace = WorkspaceForPresentation(workspaceIdentity)
            };
        if (measure(result) <= MaximumCharacters) return result;
        result = result with { Truncated = true, TranscriptTail = "" };
        while (measure(result) > MaximumCharacters)
        {
            if (result.Verification is { Diff.Length: > 0 } diff)
                result = result with { Verification = diff with { Diff = diff.Diff[..(diff.Diff.Length / 2)] } };
            else if (result.Workspace is { InitialStatus.Length: > 0 } workspace)
                result = result with { Workspace = workspace with { InitialStatus = "" } };
            else if (result.Verification is { ChangedFiles.Count: > 0 } files)
                result = result with { Verification = files with { ChangedFiles = files.ChangedFiles.Take(files.ChangedFiles.Count / 2).ToArray() } };
            else if (result.Verification is { ReceiptReferences.Count: > 0 } refs)
                result = result with { Verification = refs with { ReceiptReferences = refs.ReceiptReferences.Take(refs.ReceiptReferences.Count / 2).ToArray() } };
            else if (result.DiffStat.Length > 0) result = result with { DiffStat = "" };
            else if (result.Summary.Length > 0) result = result with { Summary = "" };
            else if (result.Error is { Length: > 0 }) result = result with { Error = "" };
            else if (result.Workspace is not null) result = result with { Workspace = null };
            else if (result.WorktreePath is not null) result = result with { WorktreePath = null };
            else if (result.BranchName.Length > 0) result = result with { BranchName = "" };
            else if (result.CliUsed.Length > 0) result = result with { CliUsed = "" };
            else throw new InvalidOperationException("Coding result identity metadata exceeds the presentation bound.");
        }
        return result;
    }
}
