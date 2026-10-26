using InvoiceReader.Web.Data;
using InvoiceReader.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace InvoiceReader.Web.Pages;

/// <summary>
/// For the app owner only (policy Admin): create an office with its first login, add logins,
/// reset passwords. A new password is shown once, to pass on to the accountant.
/// </summary>
public class AdminModel : PageModel
{
    private readonly AppDbContext _db;

    public AdminModel(AppDbContext db) => _db = db;

    public record OfficeRow(Office Office, List<User> Users, int Clients, int DocumentsThisMonth);

    public List<OfficeRow> Offices { get; private set; } = new();
    public int CurrentUserId => int.TryParse(User.FindFirst(UserAccounts.UserIdClaim)?.Value, out var id) ? id : 0;

    [TempData] public string? Message { get; set; }

    /// <summary>Email and password of a just-created or reset login, shown once.</summary>
    [TempData] public string? NewLoginEmail { get; set; }
    [TempData] public string? NewLoginPassword { get; set; }

    /// <summary>What the copy button puts on the clipboard, ready to send to the accountant.</summary>
    public string NewLoginText =>
        $"Prijava: {Request.Scheme}://{Request.Host}/Login\nEmail: {NewLoginEmail}\nLozinka: {NewLoginPassword}";

    public async Task OnGetAsync()
    {
        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var offices = await _db.Offices.OrderBy(o => o.Name).ToListAsync();
        var users = await _db.Users.OrderBy(u => u.Email).ToListAsync();
        var clients = await _db.Clients.GroupBy(c => c.OfficeId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
        var docs = await _db.Documents.Where(d => d.UploadedAt >= monthStart)
            .GroupBy(d => d.OfficeId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();

        Offices = offices.Select(o => new OfficeRow(
            o,
            users.Where(u => u.OfficeId == o.Id).ToList(),
            clients.FirstOrDefault(c => c.Key == o.Id)?.Count ?? 0,
            docs.FirstOrDefault(d => d.Key == o.Id)?.Count ?? 0)).ToList();
    }

    public async Task<IActionResult> OnPostCreateOfficeAsync(string officeName, string email, string? name)
    {
        if (string.IsNullOrWhiteSpace(officeName))
        {
            Message = "Unesite naziv ureda.";
            return Back();
        }
        var office = new Office { Name = officeName.Trim() };
        var error = await AddUserAsync(office, email, name);
        if (error is not null)
        {
            Message = error;
            return Back();
        }
        Message = $"Ured {office.Name} je napravljen. Kontni plan i klijente ured unosi u Postavkama.";
        return Back();
    }

    public async Task<IActionResult> OnPostAddUserAsync(int officeId, string email, string? name)
    {
        var office = await _db.Offices.FirstOrDefaultAsync(o => o.Id == officeId);
        if (office is null) return NotFound();
        Message = await AddUserAsync(office, email, name) ?? $"Korisnik je dodan u {office.Name}.";
        return Back();
    }

    /// <summary>New random password; the old one and its sessions stop working.</summary>
    public async Task<IActionResult> OnPostResetPasswordAsync(int userId)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return NotFound();
        var password = UserAccounts.GeneratePassword();
        UserAccounts.SetPassword(user, password);
        await _db.SaveChangesAsync();
        NewLoginEmail = user.Email;
        NewLoginPassword = password;
        Message = "Nova lozinka je napravljena. Stara više ne radi.";
        return Back();
    }

    public async Task<IActionResult> OnPostToggleActiveAsync(int userId)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return NotFound();
        if (user.Id == CurrentUserId)
        {
            Message = "Ne možete isključiti svoj nalog.";
            return Back();
        }
        user.IsActive = !user.IsActive;
        await _db.SaveChangesAsync();
        Message = user.IsActive ? $"{user.Email} je ponovo uključen." : $"{user.Email} je isključen i odjavljen.";
        return Back();
    }

    /// <summary>
    /// Back to the page, scrolled to the message and new password at the top. Without the
    /// fragment the browser keeps the old scroll position and they stay out of sight.
    /// </summary>
    private IActionResult Back() => RedirectToPage(pageName: null, pageHandler: null, routeValues: null, fragment: "result");

    /// <summary>Adds a login with a generated password to the office (saving a new office too). Null on success.</summary>
    private async Task<string?> AddUserAsync(Office office, string? email, string? name)
    {
        var normalized = UserAccounts.NormalizeEmail(email ?? "");
        if (!normalized.Contains('@')) return "Unesite ispravan email.";
        if (await _db.Users.AnyAsync(u => u.Email == normalized)) return $"{normalized} već ima nalog.";

        var password = UserAccounts.GeneratePassword();
        var user = new User
        {
            Office = office,
            Email = normalized,
            Name = string.IsNullOrWhiteSpace(name) ? normalized : name.Trim()
        };
        UserAccounts.SetPassword(user, password);
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        NewLoginEmail = normalized;
        NewLoginPassword = password;
        return null;
    }
}
