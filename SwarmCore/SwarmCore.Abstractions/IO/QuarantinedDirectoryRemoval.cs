using SwarmCore.Abstractions.Processes;

namespace SwarmCore.Abstractions.IO;

public sealed record DirectoryRemovalPlan(Guid WorkId, Guid OwnerId, string SourceRoot, string SourceName, string QuarantineRoot)
{
    public string SourcePath => ContainedDirectoryPath.Child(SourceRoot, SourceName);
    public string WorkPath => ContainedDirectoryPath.Child(QuarantineRoot, WorkId.ToString("N"));
    public string PayloadPath => ContainedDirectoryPath.Child(WorkPath, "payload");
}

/// <summary>Host transactions own intent and detach acknowledgement; file work never decides current source ownership.</summary>
public interface IQuarantinedDirectoryRemoval
{
    void ValidateForDetach(DirectoryRemovalPlan plan, CancellationToken ct);
    void Detach(DirectoryRemovalPlan plan, CancellationToken ct);
    void DeleteDetached(DirectoryRemovalPlan plan, CancellationToken ct);
}

public sealed class QuarantinedDirectoryRemoval : IQuarantinedDirectoryRemoval
{
    private sealed record Owner(Guid WorkId, Guid OwnerId);
    private static string Marker(DirectoryRemovalPlan plan) => Path.Combine(plan.WorkPath, "owner.json");

    public void ValidateForDetach(DirectoryRemovalPlan plan, CancellationToken ct)
    {
        ContainedDirectoryPath.ValidateForDeletion(plan.SourceRoot, plan.SourceName, plan.SourcePath, ct);
        ContainedDirectoryPath.ValidateForDeletion(plan.QuarantineRoot, plan.WorkId.ToString("N"), plan.WorkPath, ct);
    }

    // Called inside a brief host ownership transaction, after expensive tree
    // validation. Only directory/marker checks and the same-volume rename occur here.
    public void Detach(DirectoryRemovalPlan plan, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ContainedDirectoryPath.IsUnlinkedDirectory(plan.SourceRoot);
        ContainedDirectoryPath.IsUnlinkedDirectory(plan.QuarantineRoot);
        Directory.CreateDirectory(plan.QuarantineRoot);
        var sourceExists = ContainedDirectoryPath.IsUnlinkedDirectory(plan.SourcePath);
        var workExists = ContainedDirectoryPath.IsUnlinkedDirectory(plan.WorkPath);
        if (!workExists) Directory.CreateDirectory(plan.WorkPath);
        var marker = Marker(plan);
        if (!File.Exists(marker))
        {
            // An empty container can be left by a crash before marker publication.
            // Never adopt a pre-existing payload or foreign files without an owner.
            if (Directory.EnumerateFileSystemEntries(plan.WorkPath).Any())
                throw new IOException("Removal quarantine has no verified owner.");
            AtomicJsonFile.Write(marker, new Owner(plan.WorkId, plan.OwnerId));
            using var durable = new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            durable.Flush(flushToDisk: true);
        }
        VerifyOwner(plan);
        if (ContainedDirectoryPath.IsUnlinkedDirectory(plan.PayloadPath)) return;
        // A prior successful rename can survive a rolled-back host transaction.
        // Its owned payload above is authoritative; never revisit a new source.
        if (sourceExists) Directory.Move(plan.SourcePath, plan.PayloadPath);
    }

    public void DeleteDetached(DirectoryRemovalPlan plan, CancellationToken ct)
    {
        // A persisted detach acknowledgement is required by the caller. This
        // method deliberately never inspects or deletes SourcePath.
        ContainedDirectoryPath.ValidateForDeletion(plan.QuarantineRoot, plan.WorkId.ToString("N"), plan.WorkPath, ct);
        if (!ContainedDirectoryPath.IsUnlinkedDirectory(plan.WorkPath)) return;
        if (!File.Exists(Marker(plan)))
        {
            // Last cleanup step may have removed the marker before a crash.
            // Nonrecursive removal is safe only for an actually empty container.
            Directory.Delete(plan.WorkPath, recursive: false);
            return;
        }
        VerifyOwner(plan);
        if (ContainedDirectoryPath.IsUnlinkedDirectory(plan.PayloadPath))
            Directory.Delete(plan.PayloadPath, recursive: true);
        ct.ThrowIfCancellationRequested();
        File.Delete(Marker(plan));
        Directory.Delete(plan.WorkPath, recursive: false);
    }

    private static void VerifyOwner(DirectoryRemovalPlan plan)
    {
        if ((File.GetAttributes(Marker(plan)) & FileAttributes.ReparsePoint) != 0
            || AtomicJsonFile.Read<Owner>(Marker(plan)) != new Owner(plan.WorkId, plan.OwnerId))
            throw new IOException("Removal quarantine belongs to a different owner.");
    }
}
