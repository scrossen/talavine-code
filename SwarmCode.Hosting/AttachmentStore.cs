using System.Security.Cryptography;
using SwarmCore.Coding.Files;

namespace SwarmCode.Hosting;

/// <summary>Imports immutable copies; never modifies the user's original file.</summary>
public sealed class AttachmentStore(SwarmCodeDataPaths paths, CodingAttachmentOptions? limits = null)
{
    /// <summary>Remove every imported copy for a deleted session. Best effort; returns false when something is still locked.</summary>
    public bool DeleteConversation(string conversationId)
    {
        if (!StorageIds.IsValid(conversationId)) return false;
        var folder = Path.Combine(paths.SessionsDirectory, "attachments", conversationId);
        return SwarmCore.Abstractions.IO.ManagedScratch.TryDeleteTree(folder);
    }

    public async Task<CodingAttachment> ImportAsync(string conversationId, string fileName, Stream source, CancellationToken ct = default)
    {
        if (!StorageIds.IsValid(conversationId)) throw new ArgumentException("Invalid session id.", nameof(conversationId));
        var name = Path.GetFileName(fileName.Replace('\\', '/'));
        name = new string(name.Where(c => !char.IsControl(c) && !Path.GetInvalidFileNameChars().Contains(c)).ToArray());
        if (string.IsNullOrWhiteSpace(name)) name = "attachment";
        if (name.Length > 160) name = name[^160..];
        var id = Guid.NewGuid().ToString("N");
        var folder = Path.Combine(paths.SessionsDirectory, "attachments", conversationId, id);
        Directory.CreateDirectory(folder);
        // A document named CLAUDE.md/AGENTS.md/GEMINI.md must not become a CLI's
        // automatically discovered instruction file in an added directory.
        var path = Path.Combine(folder, "file-" + name);
        try
        {
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    (limits ?? new()).ValidateFile(output.Length + read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
                await output.FlushAsync(ct).ConfigureAwait(false);
            }
            await using var input = File.OpenRead(path);
            var length = input.Length;
            var header = new byte[16];
            var count = await input.ReadAsync(header, ct).ConfigureAwait(false);
            input.Position = 0;
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, ct).ConfigureAwait(false));
            return new(id, name, path, CodingAttachments.MediaType(name, header.AsSpan(0, count)), length, hash);
        }
        catch { File.Delete(path); Directory.Delete(folder); throw; }
    }
}
