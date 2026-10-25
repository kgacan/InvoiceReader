using InvoiceReader.Web.Data;
using InvoiceReader.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace InvoiceReader.Web.Pages.Clients;

public class DetailModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly FileStore _files;
    private readonly IWebHostEnvironment _env;
    private readonly AppMode _mode;

    public DetailModel(AppDbContext db, FileStore files, IWebHostEnvironment env, AppMode mode)
    {
        _db = db;
        _files = files;
        _env = env;
        _mode = mode;
    }

    public Client Client { get; private set; } = null!;
    public List<Document> Documents { get; private set; } = new();
    public string UploadUrl => $"{Request.Scheme}://{Request.Host}/u/{Client.UploadToken}";
    public bool AnyInProgress => Documents.Any(d => d.Status is DocumentStatus.Uploaded or DocumentStatus.Reading);

    [TempData] public string? Message { get; set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        if (!await LoadAsync(id)) return NotFound();
        return Page();
    }

    /// <summary>The office uploads files on the client's behalf.</summary>
    public async Task<IActionResult> OnPostUploadAsync(int id, List<IFormFile> files)
    {
        if (!await LoadAsync(id)) return NotFound();

        var saved = 0;
        var errors = new List<string>();
        foreach (var f in files)
        {
            await using var s = f.OpenReadStream();
            var (doc, error) = await _files.SaveAsync(s, f.FileName, f.Length, Client.OfficeId, Client.Id);
            if (doc is null) { errors.Add(error!); continue; }
            _db.Documents.Add(doc);
            saved++;
        }
        await _db.SaveChangesAsync();

        Message = saved > 0 ? $"Učitano: {saved}. AI ih čita." : "Nijedna datoteka nije učitana.";
        if (errors.Count > 0) Message += " " + string.Join(" ", errors);
        return RedirectToPage(new { id });
    }

    /// <summary>Demo helper: adds the bundled sample invoices to this client.</summary>
    public async Task<IActionResult> OnPostSamplesAsync(int id)
    {
        if (!_mode.IsDemo || !await LoadAsync(id)) return NotFound();

        var dir = Path.Combine(_env.ContentRootPath, "SampleInvoices");
        var samples = Directory.Exists(dir)
            ? Directory.GetFiles(dir).Where(p => !p.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p).ToList()
            : new List<string>();

        foreach (var path in samples)
        {
            await using var s = System.IO.File.OpenRead(path);
            var (doc, _) = await _files.SaveAsync(s, Path.GetFileName(path), s.Length, Client.OfficeId, Client.Id);
            if (doc is not null) _db.Documents.Add(doc);
        }
        await _db.SaveChangesAsync();

        Message = $"Dodano {samples.Count} primjera faktura. AI ih čita.";
        return RedirectToPage(new { id });
    }

    /// <summary>Send failed documents back to the AI.</summary>
    public async Task<IActionResult> OnPostRetryAsync(int id)
    {
        if (!await LoadAsync(id)) return NotFound();
        foreach (var d in Documents.Where(d => d.Status == DocumentStatus.Failed))
        {
            d.Status = DocumentStatus.Uploaded;
            d.Error = null;
        }
        await _db.SaveChangesAsync();
        return RedirectToPage(new { id });
    }

    private async Task<bool> LoadAsync(int id)
    {
        var officeId = User.OfficeId();
        var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == id && c.OfficeId == officeId);
        if (client is null) return false;
        Client = client;
        Documents = await _db.Documents
            .Include(d => d.Invoice)
            .Where(d => d.ClientId == id)
            .OrderByDescending(d => d.Id)
            .ToListAsync();
        return true;
    }
}
