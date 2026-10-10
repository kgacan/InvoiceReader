using System.Globalization;
using InvoiceReader.Web.Data;
using InvoiceReader.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace InvoiceReader.Web.Pages;

public class ExportModel : PageModel
{
    private readonly AppDbContext _db;

    public ExportModel(AppDbContext db) => _db = db;

    public List<Client> Clients { get; private set; } = new();
    public List<ExportRecord> Recent { get; private set; } = new();
    public Office Office { get; private set; } = null!;
    public int ReadyCount { get; private set; }
    public string? Error { get; private set; }

    [BindProperty(SupportsGet = true)] public int? ClientId { get; set; }

    /// <summary>Month as "yyyy-MM" (from an input type="month").</summary>
    [BindProperty(SupportsGet = true)] public string? Period { get; set; }

    [BindProperty] public bool IncludeExported { get; set; }

    public async Task OnGetAsync()
    {
        await LoadAsync();
        Period ??= DateTime.Today.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        if (ClientId is not null && TryParsePeriod(Period, out var from, out var to))
            ReadyCount = (await Query(ClientId.Value, from, to, includeExported: false).ToListAsync()).Count;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadAsync();
        if (ClientId is null || !TryParsePeriod(Period, out var from, out var to))
        {
            Error = "Izaberite klijenta i mjesec.";
            return Page();
        }

        var client = Clients.FirstOrDefault(c => c.Id == ClientId);
        if (client is null) return NotFound();

        var invoices = await Query(client.Id, from, to, IncludeExported).ToListAsync();
        if (invoices.Count == 0)
        {
            Error = "Nema potvrđenih faktura za ovaj mjesec.";
            return Page();
        }

        var bytes = CsvExporter.Build(invoices, Office);
        var fileName = $"{Slug(client.Name)}_{Period}.csv";

        foreach (var i in invoices) i.Document!.Status = DocumentStatus.Exported;
        _db.Exports.Add(new ExportRecord
        {
            OfficeId = Office.Id,
            ClientId = client.Id,
            Period = Period!,
            FileName = fileName,
            InvoiceCount = invoices.Count
        });
        await _db.SaveChangesAsync();

        return File(bytes, "text/csv; charset=utf-8", fileName);
    }

    private IQueryable<InvoiceData> Query(int clientId, DateOnly from, DateOnly to, bool includeExported)
    {
        var officeId = User.OfficeId();
        return _db.Invoices
            .Include(i => i.Document)
            .Where(i => i.Document!.OfficeId == officeId && i.Document.ClientId == clientId)
            .Where(i => i.Document!.Status == DocumentStatus.Confirmed ||
                        (includeExported && i.Document.Status == DocumentStatus.Exported))
            .Where(i => i.InvoiceDate >= from && i.InvoiceDate < to);
    }

    private async Task LoadAsync()
    {
        var officeId = User.OfficeId();
        Office = await _db.Offices.FirstAsync(o => o.Id == officeId);
        Clients = await _db.Clients.Where(c => c.OfficeId == officeId).OrderBy(c => c.Name).ToListAsync();
        Recent = await _db.Exports.Where(e => e.OfficeId == officeId).OrderByDescending(e => e.Id).Take(10).ToListAsync();
    }

    public static bool TryParsePeriod(string? period, out DateOnly from, out DateOnly to)
    {
        from = to = default;
        if (!DateOnly.TryParseExact(period + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out from))
            return false;
        to = from.AddMonths(1);
        return true;
    }

    private static string Slug(string s) =>
        new string(s.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
}
