using InvoiceReader.Web.Data;
using InvoiceReader.Web.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace InvoiceReader.Web.Pages;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly IInvoiceReader _ai;

    public IndexModel(AppDbContext db, IInvoiceReader ai)
    {
        _db = db;
        _ai = ai;
    }

    public record ClientRow(int Id, string Name, string TaxId, int Waiting, int InProgress, int Confirmed, int Exported, int Failed);

    public List<ClientRow> Rows { get; private set; } = new();
    public int TotalWaiting => Rows.Sum(r => r.Waiting);
    public int TotalInProgress => Rows.Sum(r => r.InProgress);
    public int TotalConfirmed => Rows.Sum(r => r.Confirmed);
    public string AiName => _ai.Name;

    public async Task OnGetAsync()
    {
        var officeId = User.OfficeId();

        var counts = await _db.Documents
            .Where(d => d.OfficeId == officeId)
            .GroupBy(d => new { d.ClientId, d.Status })
            .Select(g => new { g.Key.ClientId, g.Key.Status, Count = g.Count() })
            .ToListAsync();

        var clients = await _db.Clients.Where(c => c.OfficeId == officeId).OrderBy(c => c.Name).ToListAsync();

        int C(int clientId, params DocumentStatus[] statuses) =>
            counts.Where(x => x.ClientId == clientId && statuses.Contains(x.Status)).Sum(x => x.Count);

        Rows = clients.Select(c => new ClientRow(
            c.Id, c.Name, c.TaxId,
            C(c.Id, DocumentStatus.NeedsReview),
            C(c.Id, DocumentStatus.Uploaded, DocumentStatus.Reading),
            C(c.Id, DocumentStatus.Confirmed),
            C(c.Id, DocumentStatus.Exported),
            C(c.Id, DocumentStatus.Failed))).ToList();
    }
}
