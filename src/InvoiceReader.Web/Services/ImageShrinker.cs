using SkiaSharp;

namespace InvoiceReader.Web.Services;

/// <summary>
/// Makes phone photos small before they are stored: at most MaxSide pixels on the long side,
/// turned upright (EXIF orientation), saved as JPEG. Invoice text stays readable for the AI at
/// this size, and a 3-4 MB photo becomes a few hundred KB. The new file has no EXIF data, so
/// e.g. the GPS location of the photo is not kept.
/// Images that are already small and upright are left exactly as they are.
/// </summary>
public static class ImageShrinker
{
    public const int MaxSide = 2000;
    public const int JpegQuality = 82;

    /// <summary>Above this size an image is re-encoded even if its dimensions are small.</summary>
    public const long MaxKeptBytes = 1024 * 1024;

    // Full-size decoding of a 48 MP photo needs ~200 MB, so only two run at a time.
    private static readonly SemaphoreSlim Gate = new(2);

    public record Result(byte[] Bytes, bool Changed);

    /// <summary>The image to store, or null when the bytes are not an image Skia can open.</summary>
    public static async Task<Result?> ShrinkAsync(byte[] original)
    {
        await Gate.WaitAsync();
        try
        {
            return Shrink(original);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static Result? Shrink(byte[] original)
    {
        using var data = SKData.CreateCopy(original);
        using var codec = SKCodec.Create(data);
        if (codec is null) return null;

        var origin = codec.EncodedOrigin;
        var (width, height) = (codec.Info.Width, codec.Info.Height);
        var longest = Math.Max(width, height);
        if (longest <= MaxSide && original.Length <= MaxKeptBytes && origin == SKEncodedOrigin.TopLeft)
            return new Result(original, false);

        using var decoded = Decode(codec, longest);
        if (decoded is null) return null;

        var scale = Math.Min(1.0, (double)MaxSide / Math.Max(decoded.Width, decoded.Height));
        var info = new SKImageInfo(
            Math.Max(1, (int)Math.Round(decoded.Width * scale)),
            Math.Max(1, (int)Math.Round(decoded.Height * scale)),
            SKColorType.Rgba8888, SKAlphaType.Premul);
        using var resized = scale < 1 ? decoded.Resize(info, SKFilterQuality.High) : null;
        using var upright = Orient(resized ?? decoded, origin);

        using var image = SKImage.FromBitmap(upright ?? resized ?? decoded);
        using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
        var bytes = jpeg.ToArray();

        // A small upright image that only got bigger as JPEG (e.g. a clean PNG scan) stays as it was.
        if (bytes.Length >= original.Length && longest <= MaxSide && origin == SKEncodedOrigin.TopLeft)
            return new Result(original, false);
        return new Result(bytes, true);
    }

    /// <summary>
    /// Decodes at about twice the target size when the format allows it (JPEG decodes in steps of
    /// 1/8), so a huge photo never needs a full-size bitmap in memory.
    /// </summary>
    private static SKBitmap? Decode(SKCodec codec, int longest)
    {
        var scale = Math.Min(1f, 2f * MaxSide / longest);
        var size = codec.GetScaledDimensions(scale);
        var info = new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is SKCodecResult.Success or SKCodecResult.IncompleteInput) return bitmap;
        bitmap.Dispose();
        return null;
    }

    /// <summary>Applies the EXIF orientation so the stored pixels are upright. Null when already upright.</summary>
    private static SKBitmap? Orient(SKBitmap src, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft) return null;

        var (w, h) = (src.Width, src.Height);
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var dst = new SKBitmap(new SKImageInfo(swap ? h : w, swap ? w : h, src.ColorType, src.AlphaType));
        using var canvas = new SKCanvas(dst);

        switch (origin)
        {
            case SKEncodedOrigin.TopRight:    // mirrored
                canvas.Translate(w, 0);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.BottomRight: // upside down
                canvas.Translate(w, h);
                canvas.RotateDegrees(180);
                break;
            case SKEncodedOrigin.BottomLeft:  // mirrored upside down
                canvas.Translate(0, h);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.LeftTop:     // transposed
                canvas.RotateDegrees(90);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.RightTop:    // needs a quarter turn clockwise (usual portrait photo)
                canvas.Translate(h, 0);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.RightBottom: // transversed
                canvas.Translate(h, w);
                canvas.Scale(1, -1);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.LeftBottom:  // needs a quarter turn counter-clockwise
                canvas.Translate(0, w);
                canvas.RotateDegrees(270);
                break;
        }
        canvas.DrawBitmap(src, 0, 0);
        return dst;
    }
}
