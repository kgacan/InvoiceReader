using InvoiceReader.Web.Data;
using InvoiceReader.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace InvoiceReader.Web.Pages;

public class SettingsModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly IInvoiceReader _reader;

    public SettingsModel(AppDbContext db, IInvoiceReader reader)
    {
        _db = db;
        _reader = reader;
    }

    public Office Office { get; private set; } = null!;
    public List<Account> Accounts { get; private set; } = new();
    public List<Client> ClientList { get; private set; } = new();
    public int RuleCount { get; private set; }
    public string AiName => _reader.Name;

    [TempData] public string? Message { get; set; }

    [BindProperty] public string? AccountsText { get; set; }
    [BindProperty] public string? VatInputAccount { get; set; }
    [BindProperty] public string? SuppliersAccount { get; set; }
    [BindProperty] public string? AccountingProgram { get; set; }
    [BindProperty] public string? NewClientName { get; set; }
    [BindProperty] public string? NewClientTaxId { get; set; }

    public async Task OnGetAsync()
    {
        await LoadAsync();
        AccountsText = string.Join('\n', Accounts.Select(a => $"{a.Code};{a.Name}"));
    }

    public async Task<IActionResult> OnPostOfficeAsync()
    {
        await LoadAsync();
        Office.VatInputAccount = (VatInputAccount ?? "").Trim();
        Office.SuppliersAccount = (SuppliersAccount ?? "").Trim();
        Office.AccountingProgram = string.IsNullOrWhiteSpace(AccountingProgram) ? "Generic CSV" : AccountingProgram.Trim();
        await _db.SaveChangesAsync();
        Message = "Postavke ureda su sačuvane.";
        return RedirectToPage();
    }

    /// <summary>Replaces the chart of accounts with pasted lines "code;name".</summary>
    public async Task<IActionResult> OnPostAccountsAsync()
    {
        await LoadAsync();
        var parsed = ParseAccounts(AccountsText);
        if (parsed.Count == 0)
        {
            Message = "Nije pronađen nijedan konto. Format: jedan po redu, šifra;naziv.";
            return RedirectToPage();
        }

        _db.Accounts.RemoveRange(Accounts);
        foreach (var (code, name) in parsed)
            _db.Accounts.Add(new Account { OfficeId = Office.Id, Code = code, Name = name });
        await _db.SaveChangesAsync();
        Message = $"Kontni plan je zamijenjen: {parsed.Count} konta.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAddClientAsync()
    {
        await LoadAsync();
        if (string.IsNullOrWhiteSpace(NewClientName))
        {
            Message = "Unesite naziv klijenta.";
            return RedirectToPage();
        }
        _db.Clients.Add(new Client
        {
            OfficeId = Office.Id,
            Name = NewClientName.Trim(),
            TaxId = new string((NewClientTaxId ?? "").Where(char.IsDigit).ToArray()),
            UploadToken = SeedData.NewToken()
        });
        await _db.SaveChangesAsync();
        Message = $"Klijent {NewClientName.Trim()} je dodan.";
        return RedirectToPage();
    }

    /// <summary>Issue a new upload link; the old one stops working.</summary>
    public async Task<IActionResult> OnPostNewLinkAsync(int clientId)
    {
        await LoadAsync();
        var client = ClientList.FirstOrDefault(c => c.Id == clientId);
        if (client is null) return NotFound();
        client.UploadToken = SeedData.NewToken();
        await _db.SaveChangesAsync();
        Message = $"Novi link za {client.Name} je napravljen. Stari više ne radi.";
        return RedirectToPage();
    }

    public static List<(string Code, string Name)> ParseAccounts(string? text)
    {
        var result = new List<(string, string)>();
        var seen = new HashSet<string>();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var sep = line.IndexOfAny(new[] { ';', '\t', ',' });
            if (sep <= 0) continue;
            var code = line[..sep].Trim();
            var name = line[(sep + 1)..].Trim();
            if (code.Length == 0 || name.Length == 0 || !seen.Add(code)) continue;
            result.Add((code, name));
        }
        return result;
    }

    private async Task LoadAsync()
    {
        var officeId = User.OfficeId();
        Office = await _db.Offices.FirstAsync(o => o.Id == officeId);
        Accounts = await _db.Accounts.Where(a => a.OfficeId == officeId).OrderBy(a => a.Code).ToListAsync();
        ClientList = await _db.Clients.Where(c => c.OfficeId == officeId).OrderBy(c => c.Name).ToListAsync();
        RuleCount = await _db.SupplierRules.CountAsync(r => r.OfficeId == officeId);
    }
}
