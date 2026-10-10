using System.ComponentModel.DataAnnotations;
using InvoiceReader.Web.Data;
using InvoiceReader.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace InvoiceReader.Web.Pages;

public class ReviewModel : PageModel
{
    private readonly AppDbContext _db;

    public ReviewModel(AppDbContext db) => _db = db;

    public Document? Doc { get; private set; }
    public List<Account> Accounts { get; private set; } = new();
    public int Position { get; private set; }
    public int WaitingForClient { get; private set; }
    public List<string> Warnings { get; private set; } = new();
    public string? Error { get; private set; }

    [BindProperty] public InvoiceForm Form { get; set; } = new();

    public class InvoiceForm
    {
        public string? SupplierName { get; set; }
        public string? SupplierTaxId { get; set; }
        public string? SupplierVatNumber { get; set; }
        public string? InvoiceNumber { get; set; }
        public DateOnly? InvoiceDate { get; set; }
        public string? NetAmount { get; set; }
        public string? VatAmount { get; set; }
        public string? TotalAmount { get; set; }
        public string? AccountCode { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        var officeId = User.OfficeId();
        if (id is null)
        {
            var first = await _db.Documents
                .Where(d => d.OfficeId == officeId && d.Status == DocumentStatus.NeedsReview)
                .OrderBy(d => d.ClientId).ThenBy(d => d.Id)
                .Select(d => (int?)d.Id)
                .FirstOrDefaultAsync();
            if (first is null) return Page(); // nothing to review
            return RedirectToPage(new { id = first });
        }

        if (!await LoadAsync(id.Value)) return NotFound();

        var inv = Doc!.Invoice!;

        // A rule may have been learned after this invoice was read (e.g. a batch uploaded together).
        if (inv.AccountCode is null && Doc.Status == DocumentStatus.NeedsReview)
        {
            var rule = await AccountRules.FindAsync(_db, Doc.OfficeId, Doc.ClientId, inv.SupplierTaxId, HttpContext.RequestAborted);
            if (rule is not null)
            {
                inv.AccountCode = rule.AccountCode;
                inv.AccountSuggested = true;
                await _db.SaveChangesAsync();
            }
        }

        Form = new InvoiceForm
        {
            SupplierName = inv.SupplierName,
            SupplierTaxId = inv.SupplierTaxId,
            SupplierVatNumber = inv.SupplierVatNumber,
            InvoiceNumber = inv.InvoiceNumber,
            InvoiceDate = inv.InvoiceDate,
            NetAmount = CsvExporter.Amount(inv.NetAmount),
            VatAmount = CsvExporter.Amount(inv.VatAmount),
            TotalAmount = CsvExporter.Amount(inv.TotalAmount),
            AccountCode = inv.AccountCode
        };
        Warnings = inv.WarningList.ToList();
        return Page();
    }

    public async Task<IActionResult> OnPostConfirmAsync(int id)
    {
        if (!await LoadAsync(id)) return NotFound();
        var inv = Doc!.Invoice!;

        var parsed = new AiInvoice
        {
            SupplierName = Form.SupplierName?.Trim(),
            SupplierTaxId = Digits(Form.SupplierTaxId),
            SupplierVatNumber = Digits(Form.SupplierVatNumber),
            InvoiceNumber = Form.InvoiceNumber?.Trim(),
            InvoiceDate = Form.InvoiceDate,
            NetAmount = AiJson.ParseAmount(Form.NetAmount),
            VatAmount = AiJson.ParseAmount(Form.VatAmount),
            TotalAmount = AiJson.ParseAmount(Form.TotalAmount)
        };

        // Hard requirements before an invoice can be exported.
        if (parsed.TotalAmount is null || parsed.InvoiceDate is null || string.IsNullOrWhiteSpace(Form.AccountCode))
        {
            Error = "Za potvrdu su obavezni: datum, ukupan iznos i konto.";
            Warnings = InvoiceChecks.Run(parsed, false, DateOnly.FromDateTime(DateTime.UtcNow));
            return Page();
        }

        var accountChangedByUser = inv.AccountCode != Form.AccountCode;

        inv.SupplierName = parsed.SupplierName;
        inv.SupplierTaxId = parsed.SupplierTaxId;
        inv.SupplierVatNumber = parsed.SupplierVatNumber;
        inv.InvoiceNumber = parsed.InvoiceNumber;
        inv.InvoiceDate = parsed.InvoiceDate;
        inv.NetAmount = parsed.NetAmount;
        inv.VatAmount = parsed.VatAmount;
        inv.TotalAmount = parsed.TotalAmount;
        inv.AccountCode = Form.AccountCode!.Trim();
        if (accountChangedByUser) inv.AccountSuggested = false;
        inv.ConfirmedAt = DateTime.UtcNow;
        Doc.Status = DocumentStatus.Confirmed;

        await AccountRules.LearnAsync(_db, Doc.OfficeId, Doc.ClientId, inv.SupplierTaxId, inv.AccountCode);
        await _db.SaveChangesAsync();

        var next = await NextIdAsync(Doc);
        return next is null ? RedirectToPage("/Clients/Detail", new { id = Doc.ClientId }) : RedirectToPage(new { id = next });
    }

    public async Task<IActionResult> OnPostSkipAsync(int id)
    {
        if (!await LoadAsync(id)) return NotFound();
        var next = await NextIdAsync(Doc!);
        return next is null ? RedirectToPage("/Clients/Detail", new { id = Doc!.ClientId }) : RedirectToPage(new { id = next });
    }

    /// <summary>Re-open a confirmed invoice for editing.</summary>
    public async Task<IActionResult> OnPostReopenAsync(int id)
    {
        if (!await LoadAsync(id)) return NotFound();
        if (Doc!.Status == DocumentStatus.Confirmed) Doc.Status = DocumentStatus.NeedsReview;
        await _db.SaveChangesAsync();
        return RedirectToPage(new { id });
    }

    private async Task<bool> LoadAsync(int id)
    {
        var officeId = User.OfficeId();
        Doc = await _db.Documents
            .Include(d => d.Client)
            .Include(d => d.Invoice)
            .FirstOrDefaultAsync(d => d.Id == id && d.OfficeId == officeId && d.Invoice != null);
        if (Doc is null) return false;

        Accounts = await _db.Accounts.Where(a => a.OfficeId == officeId).OrderBy(a => a.Code).ToListAsync();

        var waitingIds = await _db.Documents
            .Where(d => d.ClientId == Doc.ClientId && d.Status == DocumentStatus.NeedsReview)
            .OrderBy(d => d.Id).Select(d => d.Id).ToListAsync();
        WaitingForClient = waitingIds.Count;
        Position = waitingIds.IndexOf(id) + 1;
        return true;
    }

    /// <summary>Next invoice to review: same client first, then any client of the office.</summary>
    private async Task<int?> NextIdAsync(Document current)
    {
        var sameClient = await _db.Documents
            .Where(d => d.ClientId == current.ClientId && d.Status == DocumentStatus.NeedsReview && d.Id != current.Id)
            .OrderBy(d => d.Id > current.Id ? 0 : 1).ThenBy(d => d.Id)
            .Select(d => (int?)d.Id).FirstOrDefaultAsync();
        if (sameClient is not null) return sameClient;

        return await _db.Documents
            .Where(d => d.OfficeId == current.OfficeId && d.Status == DocumentStatus.NeedsReview && d.Id != current.Id)
            .OrderBy(d => d.Id)
            .Select(d => (int?)d.Id).FirstOrDefaultAsync();
    }

    private static string? Digits(string? s)
    {
        if (s is null) return null;
        var d = new string(s.Where(char.IsDigit).ToArray());
        return d.Length == 0 ? null : d;
    }
}
