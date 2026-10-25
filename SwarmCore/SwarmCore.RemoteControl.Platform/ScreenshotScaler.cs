using SkiaSharp;

namespace SwarmCore.RemoteControl.Platform;

/// <summary>
/// Downscales a screenshot so a model is not handed a multi-megabyte 4K/Retina frame. The full-resolution PNG stays on
/// disk; callers map image pixels back to screen points with screen = bounds.Left + imageX / Scale.
/// </summary>
public static class ScreenshotScaler
{
    /// <summary>The default longest side for an image handed to a model.</summary>
    public const int DefaultMaxSide = 1568;

    /// <summary>A scaled PNG larger than this is re-encoded as JPEG (quality 85).</summary>
    public const int JpegThresholdBytes = 1_500_000;

    /// <summary>
    /// Scales <paramref name="png"/> so its longest side is at most <paramref name="maxSide"/>. An image already within
    /// the limit is returned unchanged (scale 1). Throws <see cref="ArgumentException"/> when the bytes are not a
    /// decodable image.
    /// </summary>
    public static (byte[] Bytes, string Mime, int Width, int Height, double Scale) Scale(byte[] png, int maxSide)
    {
        ArgumentNullException.ThrowIfNull(png);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSide, 1);
        using var source = Decode(png);
        if (source is null || source.Width <= 0 || source.Height <= 0)
            throw new ArgumentException("The screenshot could not be decoded.", nameof(png));
        var scale = Math.Min(1.0, (double)maxSide / Math.Max(source.Width, source.Height));
        if (scale >= 1.0) return (png, "image/png", source.Width, source.Height, 1.0);
        var w = Math.Max(1, (int)Math.Round(source.Width * scale));
        var h = Math.Max(1, (int)Math.Round(source.Height * scale));
        using var resized = source.Resize(new SKImageInfo(w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
            ?? throw new ArgumentException("The screenshot could not be resized.", nameof(png));
        using var image = SKImage.FromBitmap(resized);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = encoded.ToArray();
        var mime = "image/png";
        if (bytes.Length > JpegThresholdBytes)
        {
            using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 85);
            bytes = jpeg.ToArray();
            mime = "image/jpeg";
        }
        return (bytes, mime, w, h, (double)w / source.Width);
    }

    private static SKBitmap? Decode(byte[] png)
    {
        // Skia throws ArgumentNullException (no codec) for bytes that are not an image at all.
        try { return SKBitmap.Decode(png); }
        catch (ArgumentNullException) { return null; }
    }
}
