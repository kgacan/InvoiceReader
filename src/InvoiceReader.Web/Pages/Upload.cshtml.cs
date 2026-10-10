using InvoiceReader.Web.Data;
using InvoiceReader.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace InvoiceReader.Web.Pages;

/// <summary>Public page behind the client's private link. No login: the token is the key.</summary>
public class UploadModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly FileStore _files;

    public UploadModel(AppDbContext db, FileStore files)
    {
        _db = db;
        _files = files;
    }

    public string ClientName { get; private set; } = "";
    public string OfficeName { get; private set; } = "";
    public int? SavedCount { get; private set; }
    public List<string> Errors { get; } = new();

    public async Task<IActionResult> OnGetAsync(string token)
    {
        var client = await FindAsync(token);
        if (client is null) return NotFound();
        await FillNamesAsync(client);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string token, List<IFormFile> files)
    {
        var client = await FindAsync(token);
        if (client is null) return NotFound();
        await FillNamesAsync(client);

        var saved = 0;
        foreach (var f in files.Take(20))
        {
            await using var s = f.OpenReadStream();
            var (doc, error) = await _files.SaveAsync(s, f.FileName, f.Length, client.OfficeId, client.Id);
            if (doc is null) { Errors.Add(error!); continue; }
            _db.Documents.Add(doc);
            saved++;
        }
        await _db.SaveChangesAsync();
        SavedCount = saved;
        return Page();
    }

    private Task<Client?> FindAsync(string token) =>
        string.IsNullOrWhiteSpace(token) || token.Length < 16
            ? Task.FromResult<Client?>(null)
            : _db.Clients.FirstOrDefaultAsync(c => c.UploadToken == token);

    private async Task FillNamesAsync(Client client)
    {
        ClientName = client.Name;
        OfficeName = await _db.Offices.Where(o => o.Id == client.OfficeId).Select(o => o.Name).FirstAsync();
    }
}
