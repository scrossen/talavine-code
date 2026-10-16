using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting.Providers;

/// <summary>Per-invocation image files, owned until the contained child process is reaped.</summary>
internal sealed class CliImageAttachmentLease : IDisposable
{
    public const string PathsSetting = "__swarmcore_cli_image_paths";
    private readonly List<string> _paths = [];
    public IReadOnlyList<string> Paths => _paths;
    public static CliImageAttachmentLease Create(CliImageAttachments? attachments, string directory)
    {
        var lease = new CliImageAttachmentLease();
        try
        {
            if (attachments is null) return lease;
            if (attachments.Images.Count > 8) throw new ArgumentException("At most eight CLI images are supported.");
            long total = 0;
            foreach (var image in attachments.Images)
            {
                var extension = image.MediaType switch { "image/png" => ".png", "image/jpeg" => ".jpg", "image/webp" => ".webp", _ => throw new ArgumentException("Unsupported CLI image media type.") };
                if (image.Base64Data.Length > 28_000_000) throw new ArgumentException("CLI image exceeds the bounded attachment size.");
                var bytes = Convert.FromBase64String(image.Base64Data);
                total += bytes.Length;
                if (bytes.Length == 0 || total > 20_000_000) throw new ArgumentException("CLI images exceed the bounded attachment size.");
                var valid = extension switch
                {
                    ".png" => bytes.AsSpan().StartsWith(new byte[] {137,80,78,71,13,10,26,10}),
                    ".jpg" => bytes.AsSpan().StartsWith(new byte[] {255,216,255}),
                    _ => bytes.Length >= 12 && System.Text.Encoding.ASCII.GetString(bytes,0,4) == "RIFF" && System.Text.Encoding.ASCII.GetString(bytes,8,4) == "WEBP"
                };
                if (!valid) throw new ArgumentException("Image bytes do not match the declared media type.");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "codex-image-" + Guid.NewGuid().ToString("N") + extension);
                lease._paths.Add(path);
                var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using var stream = new FileStream(path, fileOptions);
                stream.Write(bytes);
            }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }
    public void Dispose()
    {
        foreach (var path in _paths)
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
