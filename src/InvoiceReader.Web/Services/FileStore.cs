using System.Security.Cryptography;
using InvoiceReader.Web.Data;

namespace InvoiceReader.Web.Services;

/// <summary>
/// Saves uploaded files to local disk (DataDir/uploads). Paths in the database are relative to
/// that folder, so the data folder can move (e.g. onto a mounted disk). For many offices, swap
/// this for private cloud storage (e.g. Azure Blob or S3-compatible) behind the same methods.
/// </summary>
public class FileStore
{
    public const long MaxPdfBytes = 7 * 1024 * 1024; // the AI APIs accept roughly this size

    /// <summary>Photos are made small before storing (ImageShrinker), so the original may be bigger.</summary>
    public const long MaxImageBytes = 25 * 1024 * 1024;

    private static readonly Dictionary<string, string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".webp"] = "image/webp",
        [".pdf"] = "application/pdf"
    };

    private readonly string _root;

    public FileStore(IConfiguration config, IWebHostEnvironment env)
    {
        var dataDir = config["DataDir"] ?? "App_Data";
        _root = Path.Combine(Path.IsPathRooted(dataDir) ? dataDir : Path.Combine(env.ContentRootPath, dataDir), "uploads");
        Directory.CreateDirectory(_root);
    }

    internal FileStore(string uploadsRoot)
    {
        _root = uploadsRoot;
        Directory.CreateDirectory(_root);
    }

    public static string AcceptAttribute => string.Join(",", Allowed.Keys);

    /// <summary>
    /// Validates and stores a file; returns a new Document (not yet saved to the DB) or an error.
    /// Large or sideways photos are stored smaller and upright; Sha256 is always of the file as
    /// uploaded, so the same photo sent twice is still recognised.
    /// </summary>
    public async Task<(Document? Doc, string? Error)> SaveAsync(Stream content, string fileName, long length, int officeId, int clientId)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (!Allowed.TryGetValue(ext, out var contentType))
            return (null, $"{fileName}: dozvoljeni su samo JPG, PNG, WEBP i PDF.");
        var isPdf = ext == ".pdf";
        if (length > (isPdf ? MaxPdfBytes : MaxImageBytes))
            return (null, $"{fileName}: datoteka je veća od {(isPdf ? MaxPdfBytes : MaxImageBytes) / (1024 * 1024)} MB.");

        using var ms = new MemoryStream();
        await content.CopyToAsync(ms);
        var original = ms.ToArray();
        var bytes = original;

        if (!isPdf)
        {
            var shrunk = await ImageShrinker.ShrinkAsync(original);
            if (shrunk is null)
                return (null, $"{fileName}: slika se ne može otvoriti. Pošaljite je ponovo kao JPG ili PNG.");
            if (shrunk.Changed)
            {
                bytes = shrunk.Bytes;
                ext = ".jpg";
                contentType = "image/jpeg";
            }
        }

        Directory.CreateDirectory(Path.Combine(_root, officeId.ToString()));
        var stored = $"{officeId}/{Guid.NewGuid():N}{ext}";
        await File.WriteAllBytesAsync(Path.Combine(_root, stored), bytes);

        return (new Document
        {
            OfficeId = officeId,
            ClientId = clientId,
            OriginalFileName = Path.GetFileName(fileName),
            StoredPath = stored,
            ContentType = contentType,
            Sha256 = Convert.ToHexString(SHA256.HashData(original)),
            Status = DocumentStatus.Uploaded,
            UploadedAt = DateTime.UtcNow
        }, null);
    }

    /// <summary>Where the file is on disk. Rows from before relative paths hold an absolute path, used as it is.</summary>
    public string FullPath(Document doc) =>
        Path.IsPathRooted(doc.StoredPath) ? doc.StoredPath : Path.Combine(_root, doc.StoredPath);

    public Task<byte[]> ReadAsync(Document doc, CancellationToken ct) => File.ReadAllBytesAsync(FullPath(doc), ct);
}
