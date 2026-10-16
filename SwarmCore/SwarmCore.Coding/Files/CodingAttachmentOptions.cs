namespace SwarmCore.Coding.Files;

/// <summary>Host-selected attachment budgets. Zero means unlimited; provider request limits still apply.</summary>
public sealed class CodingAttachmentOptions
{
    public long MaxFileBytes { get; set; }
    public long MaxTotalBytes { get; set; }
    public int MaxFiles { get; set; }
    public int MaxTextCharacters { get; set; }

    public void Validate()
    {
        if (MaxFileBytes < 0 || MaxTotalBytes < 0 || MaxFiles < 0 || MaxTextCharacters < 0)
            throw new InvalidDataException("Attachment limits must be zero (unlimited) or positive.");
    }

    public void ValidateFile(long length)
    {
        Validate();
        if (length < 0) throw new InvalidDataException("Invalid attachment length.");
        if (MaxFileBytes > 0 && length > MaxFileBytes)
            throw new InvalidDataException($"The configured per-file limit is {MaxFileBytes / 1048576d:0.##} MB. Change it in Settings → Attachments.");
    }

    public void ValidateBatch(int count, long bytes)
    {
        Validate();
        if (MaxFiles > 0 && count > MaxFiles)
            throw new InvalidDataException($"The configured attachment count limit is {MaxFiles}. Change it in Settings → Attachments.");
        if (MaxTotalBytes > 0 && bytes > MaxTotalBytes)
            throw new InvalidDataException($"The configured total attachment limit is {MaxTotalBytes / 1048576d:0.##} MB. Change it in Settings → Attachments.");
    }

    public void ValidateText(long characters)
    {
        Validate();
        if (MaxTextCharacters > 0 && characters > MaxTextCharacters)
            throw new InvalidDataException($"The configured extracted-text limit is {MaxTextCharacters:N0} characters. Change it in Settings → Attachments or attach a smaller excerpt.");
    }
}
