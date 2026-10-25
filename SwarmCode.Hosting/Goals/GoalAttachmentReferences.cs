using SwarmCore.Agent.Communication;
using SwarmCore.Coding.Files;

namespace SwarmCode.Hosting.Goals;

/// <summary>Code-local preparation only. Companion transports cannot import a caller's filesystem paths.</summary>
public interface IGoalAttachmentMailbox
{
    Task<IReadOnlyList<GoalMessageAttachment>> PrepareAttachmentsAsync(Guid goalId,
        IReadOnlyList<CodingAttachment> files, CancellationToken ct = default);
}

internal sealed class GoalAttachmentReferences(SwarmCodeDataPaths paths)
{
    private string Root => Path.Combine(paths.SessionsDirectory, "attachments");
    private CodingAttachmentOptions Limits => new UserSettingsFile(paths).ReadCodingSection<CodingAttachmentOptions>("AttachmentLimits");

    internal async Task<IReadOnlyList<GoalMessageAttachment>> PrepareAsync(string conversation,
        IReadOnlyList<CodingAttachment> files, CancellationToken ct)
    {
        CodingAttachments.ValidateBatch(files, Limits);
        var result = new List<GoalMessageAttachment>();
        foreach (var file in files)
        {
            var reference = new GoalMessageAttachment(file.Id, file.FileName, file.LocalPath, file.MediaType, file.Length, file.Sha256);
            var sourceConversation = ValidatePath(reference, null);
            await CodingAttachments.VerifyAsync(file, ct, Limits).ConfigureAwait(false);
            ValidatePath(reference, sourceConversation);
            if (sourceConversation != conversation)
            {
                await using var input = new FileStream(file.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                var imported = await new AttachmentStore(paths, Limits).ImportAsync(conversation, file.FileName, input, ct).ConfigureAwait(false);
                // Never bless bytes changed between verification and copying.
                if (imported.Length != file.Length || !imported.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("An attachment changed while it was copied. Reattach the original file.");
                reference = new(imported.Id, imported.FileName, imported.LocalPath, imported.MediaType, imported.Length, imported.Sha256);
            }
            result.Add(reference);
        }
        return result.ToArray();
    }

    internal async Task<Verified[]> VerifyAsync(IReadOnlyList<GoalMessageAttachment>? files, string conversation, CancellationToken ct)
    {
        if (!GoalMessaging.AreValidAttachments(files)) throw new InvalidDataException("Invalid attachment references. Reattach the original files.");
        var attachments = files ?? [];
        var limits = Limits;
        limits.ValidateBatch(attachments.Count, attachments.Aggregate(0L, (total, f) => checked(total + f.Length)));
        var verified = new List<Verified>();
        foreach (var file in attachments)
        {
            ValidatePath(file, conversation);
            var before = Stamp(file.LocalPath);
            await CodingAttachments.VerifyAsync(new(file.Id, file.FileName, file.LocalPath, file.MediaType, file.Length, file.Sha256), ct, limits).ConfigureAwait(false);
            ValidatePath(file, conversation);
            if (before != Stamp(file.LocalPath)) throw new InvalidDataException("An attachment changed during verification. Reattach the original file.");
            verified.Add(new(file, before));
        }
        return verified.ToArray();
    }

    internal void Recheck(IEnumerable<Verified> verified, string conversation)
    {
        foreach (var file in verified)
        {
            ValidatePath(file.Reference, conversation);
            if (file.Stamp != Stamp(file.Reference.LocalPath)) throw new InvalidDataException("An attachment changed after verification. Reattach the original file.");
        }
    }

    private string ValidatePath(GoalMessageAttachment file, string? conversation)
    {
        if (!GoalMessaging.AreValidAttachments([file]) || !Guid.TryParseExact(file.Id, "N", out _)
            || Path.GetFileName(file.FileName.Replace('\\', '/')) != file.FileName)
            throw new InvalidDataException("Invalid attachment identity. Reattach the original file.");
        var full = Path.GetFullPath(file.LocalPath);
        var relative = Path.GetRelativePath(Root, full).Split(Path.DirectorySeparatorChar);
        if (relative.Length != 3 || !StorageIds.IsValid(relative[0]) || relative[1] != file.Id
            || relative[2] != "file-" + file.FileName || conversation is not null && relative[0] != conversation
            || !string.Equals(full, file.LocalPath, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Attachments must be immutable copies in this goal's Code conversation. Reattach the file here.");
        // Reject links in every owned component, including the profile root. A lexical prefix is insufficient.
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Linked attachment paths cannot be delivered. Reattach a regular file.");
            if (current == paths.RootDirectory) break;
        }
        return relative[0];
    }

    private static FileStamp Stamp(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("An attachment is missing. Reattach the original file.");
        return new(info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc);
    }
    internal sealed record Verified(GoalMessageAttachment Reference, FileStamp Stamp);
    internal sealed record FileStamp(long Length, DateTime Modified, DateTime Created);
}
