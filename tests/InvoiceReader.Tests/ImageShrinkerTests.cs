using System.Security.Cryptography;
using InvoiceReader.Web.Services;
using SkiaSharp;

namespace InvoiceReader.Tests;

public sealed class ImageShrinkerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "invoicereader-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>An image whose left half is red and right half blue.</summary>
    private static byte[] TwoColour(int width, int height, SKEncodedImageFormat format, int quality = 90)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Blue);
            using var red = new SKPaint { Color = SKColors.Red };
            canvas.DrawRect(0, 0, width / 2f, height, red);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality);
        return data.ToArray();
    }

    /// <summary>Adds an EXIF block with only the orientation tag, as a phone camera writes it.</summary>
    private static byte[] WithExifOrientation(byte[] jpeg, ushort orientation)
    {
        byte[] app1 =
        {
            0xFF, 0xE1, 0x00, 0x22,
            (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
            0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08, // TIFF header, big-endian, IFD at 8
            0x00, 0x01,                                     // one entry
            0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, // Orientation, SHORT, count 1
            (byte)(orientation >> 8), (byte)orientation, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00                          // no next IFD
        };
        return jpeg.Take(2).Concat(app1).Concat(jpeg.Skip(2)).ToArray();
    }

    private static SKBitmap Decode(byte[] bytes) => SKBitmap.Decode(bytes);

    private static bool IsRed(SKColor c) => c.Red > 180 && c.Blue < 80;
    private static bool IsBlue(SKColor c) => c.Blue > 180 && c.Red < 80;

    [Fact]
    public async Task Large_photo_is_scaled_to_the_maximum_side_as_jpeg()
    {
        var original = TwoColour(4000, 3000, SKEncodedImageFormat.Png);

        var result = await ImageShrinker.ShrinkAsync(original);

        Assert.True(result!.Changed);
        using var codec = SKCodec.Create(SKData.CreateCopy(result.Bytes));
        Assert.Equal(SKEncodedImageFormat.Jpeg, codec.EncodedFormat);
        Assert.Equal(ImageShrinker.MaxSide, codec.Info.Width);
        Assert.Equal(1500, codec.Info.Height);
    }

    [Fact]
    public async Task Large_jpeg_is_decoded_scaled_and_keeps_its_content()
    {
        var original = TwoColour(6000, 4000, SKEncodedImageFormat.Jpeg);

        var result = await ImageShrinker.ShrinkAsync(original);

        using var bitmap = Decode(result!.Bytes);
        Assert.Equal((2000, 1333), (bitmap.Width, bitmap.Height));
        Assert.True(IsRed(bitmap.GetPixel(200, 600)));
        Assert.True(IsBlue(bitmap.GetPixel(1800, 600)));
    }

    [Theory]
    [InlineData(6, true)]  // quarter turn clockwise: left (red) ends up on top
    [InlineData(8, false)] // quarter turn counter-clockwise: left (red) ends up at the bottom
    public async Task Sideways_phone_photo_is_turned_upright(ushort orientation, bool redOnTop)
    {
        var original = WithExifOrientation(TwoColour(400, 200, SKEncodedImageFormat.Jpeg), orientation);

        var result = await ImageShrinker.ShrinkAsync(original);

        Assert.True(result!.Changed);
        using var bitmap = Decode(result.Bytes);
        Assert.Equal((200, 400), (bitmap.Width, bitmap.Height));
        Assert.Equal(redOnTop, IsRed(bitmap.GetPixel(100, 50)));
        Assert.Equal(!redOnTop, IsRed(bitmap.GetPixel(100, 350)));

        // The stored file carries no EXIF orientation, so viewers don't turn it a second time.
        using var codec = SKCodec.Create(SKData.CreateCopy(result.Bytes));
        Assert.Equal(SKEncodedOrigin.TopLeft, codec.EncodedOrigin);
    }

    [Fact]
    public async Task Small_upright_image_is_kept_byte_for_byte()
    {
        var sample = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "SampleInvoices", "01_energo_septembar.png"));

        var result = await ImageShrinker.ShrinkAsync(sample);

        Assert.False(result!.Changed);
        Assert.Same(sample, result.Bytes);
    }

    [Fact]
    public async Task Bytes_that_are_not_an_image_are_rejected()
    {
        Assert.Null(await ImageShrinker.ShrinkAsync(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public async Task FileStore_stores_the_small_jpeg_but_hashes_the_original()
    {
        var store = new FileStore(_dir);
        var original = TwoColour(4000, 3000, SKEncodedImageFormat.Png);
        using var content = new MemoryStream(original);

        var (doc, error) = await store.SaveAsync(content, "IMG_1234.png", content.Length, officeId: 1, clientId: 1);

        Assert.Null(error);
        Assert.EndsWith(".jpg", doc!.StoredPath);
        Assert.Equal("image/jpeg", doc.ContentType);
        Assert.Equal("IMG_1234.png", doc.OriginalFileName);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(original)), doc.Sha256);
        Assert.True((await store.ReadAsync(doc, CancellationToken.None)).Length < original.Length);
    }

    [Fact]
    public async Task FileStore_rejects_a_broken_image_and_an_oversized_pdf()
    {
        var store = new FileStore(_dir);

        using var broken = new MemoryStream(new byte[] { 1, 2, 3 });
        var (doc, error) = await store.SaveAsync(broken, "slika.jpg", broken.Length, 1, 1);
        Assert.Null(doc);
        Assert.Contains("ne može otvoriti", error);

        using var pdf = new MemoryStream(new byte[] { 1 });
        (doc, error) = await store.SaveAsync(pdf, "velika.pdf", FileStore.MaxPdfBytes + 1, 1, 1);
        Assert.Null(doc);
        Assert.Contains("7 MB", error);
    }
}
