using System.Security.Cryptography;
using InvoiceReader.Web.Data;

namespace InvoiceReader.Web.Services;

/// <summary>
/// Saves uploaded files to local disk (DataDir/uploads). For production, swap this for
/// private cloud storage (e.g. Azure Blob or S3-compatible) behind the same methods.
/// </summary>
public class FileStore
{
    public const long MaxBytes = 7 * 1024 * 1024; // the AI APIs accept roughly this size

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

    public static string AcceptAttribute => string.Join(",", Allowed.Keys);

    /// <summary>Validates and stores a file; returns a new Document (not yet saved to the DB) or an error.</summary>
    public async Task<(Document? Doc, string? Error)> SaveAsync(Stream content, string fileName, long length, int officeId, int clientId)
    {
        var ext = Path.GetExtension(fileName);
        if (!Allowed.TryGetValue(ext, out var contentType))
            return (null, $"{fileName}: dozvoljeni su samo JPG, PNG, WEBP i PDF.");
        if (length > MaxBytes)
            return (null, $"{fileName}: datoteka je veća od 7 MB.");

        using var ms = new MemoryStream();
        await content.CopyToAsync(ms);
        var bytes = ms.ToArray();

        var dir = Path.Combine(_root, officeId.ToString());
        Directory.CreateDirectory(dir);
        var stored = Path.Combine(dir, $"{Guid.NewGuid():N}{ext.ToLowerInvariant()}");
        await File.WriteAllBytesAsync(stored, bytes);

        return (new Document
        {
            OfficeId = officeId,
            ClientId = clientId,
            OriginalFileName = Path.GetFileName(fileName),
            StoredPath = stored,
            ContentType = contentType,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
            Status = DocumentStatus.Uploaded,
            UploadedAt = DateTime.UtcNow
        }, null);
    }

    public Task<byte[]> ReadAsync(Document doc, CancellationToken ct) => File.ReadAllBytesAsync(doc.StoredPath, ct);
}
