using InvoiceReader.Web.Data;
using InvoiceReader.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace InvoiceReader.Web.Pages;

/// <summary>The logged-in user changes their own password; other sessions are logged out.</summary>
public class PasswordModel : PageModel
{
    private readonly AppDbContext _db;

    public PasswordModel(AppDbContext db) => _db = db;

    [BindProperty] public string CurrentPassword { get; set; } = "";
    [BindProperty] public string NewPassword { get; set; } = "";
    [BindProperty] public string ConfirmPassword { get; set; } = "";

    public string? Error { get; private set; }
    [TempData] public string? Message { get; set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        var userId = int.TryParse(User.FindFirst(UserAccounts.UserIdClaim)?.Value, out var id) ? id : 0;
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return NotFound();

        if (!UserAccounts.CheckPassword(user, CurrentPassword))
            Error = "Trenutna lozinka nije tačna.";
        else if (NewPassword != ConfirmPassword)
            Error = "Nova lozinka i potvrda se ne slažu.";
        else
            Error = UserAccounts.CheckNewPassword(NewPassword);
        if (Error is not null) return Page();

        UserAccounts.SetPassword(user, NewPassword);
        await _db.SaveChangesAsync();

        // New security stamp: sign in again so this session stays valid.
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, UserAccounts.ToPrincipal(user),
            new AuthenticationProperties { IsPersistent = true });
        Message = "Lozinka je promijenjena.";
        return RedirectToPage();
    }
}
